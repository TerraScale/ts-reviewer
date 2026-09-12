package webhook

import (
	"context"
	"crypto/hmac"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json/v2"
	"fmt"
	"git.terrascale.tech/TerraScale/ts-reviewer/internal/forgejo"
	"git.terrascale.tech/TerraScale/ts-reviewer/internal/review"
	"io"
	"net/http"
	"strings"
	"time"
)

type Handler struct {
	Forgejo                               *forgejo.Client
	Engine                                *review.Engine
	Organization, BotLogin, WebhookSecret string
	ReviewTimeout                         time.Duration
	Slots                                 chan struct{}
}
type event struct {
	Action     string `json:"action"`
	Number     int    `json:"number"`
	Repository struct {
		Name, FullName string
		Owner          struct{ Login string } `json:"owner"`
	} `json:"repository"`
	PullRequest struct {
		Number int `json:"number"`
	} `json:"pull_request"`
	Comment struct {
		Body string `json:"body"`
	} `json:"comment"`
}

func (h *Handler) ServeHTTP(w http.ResponseWriter, r *http.Request) {
	if r.Method != "POST" {
		http.Error(w, "method not allowed", 405)
		return
	}
	if r.ContentLength > 1<<20 {
		http.Error(w, "invalid payload", 400)
		return
	}
	body, err := io.ReadAll(io.LimitReader(r.Body, 1<<20+1))
	if err != nil {
		http.Error(w, "read payload", 400)
		return
	}
	if len(body) > 1<<20 {
		http.Error(w, "invalid payload", 400)
		return
	}
	if h.WebhookSecret != "" && !validSignature(body, r.Header.Get("X-Forgejo-Signature"), h.WebhookSecret) {
		http.Error(w, "invalid signature", 401)
		return
	}
	var e event
	if err := json.Unmarshal(body, &e); err != nil {
		http.Error(w, "invalid JSON", 400)
		return
	}
	if e.Repository.Owner.Login == "" || !strings.EqualFold(e.Repository.Owner.Login, h.Organization) {
		w.WriteHeader(http.StatusNoContent)
		return
	}
	n := e.Number
	if n == 0 {
		n = e.PullRequest.Number
	}
	if n < 1 {
		w.WriteHeader(http.StatusNoContent)
		return
	}
	switch e.Action {
	case "opened", "synchronize", "reopened":
		go h.review(e.Repository.Owner.Login, e.Repository.Name, n)
	case "created":
		if command := parseCommand(e.Comment.Body, h.BotLogin); command != "" {
			go h.command(e.Repository.Owner.Login, e.Repository.Name, n, command)
		}
	}
	w.WriteHeader(http.StatusAccepted)
}
func validSignature(body []byte, signature, secret string) bool {
	signature = strings.TrimPrefix(signature, "sha256=")
	got, err := hex.DecodeString(signature)
	if err != nil {
		return false
	}
	mac := hmac.New(sha256.New, []byte(secret))
	mac.Write(body)
	return hmac.Equal(got, mac.Sum(nil))
}
func (h *Handler) review(owner, name string, n int) {
	if h.Slots != nil {
		h.Slots <- struct{}{}
		defer func() { <-h.Slots }()
	}
	timeout := h.ReviewTimeout
	if timeout <= 0 {
		timeout = 10 * time.Minute
	}
	ctx, cancel := context.WithTimeout(context.Background(), timeout)
	defer cancel()
	pr, err := h.Forgejo.Pull(ctx, owner, name, n)
	if err != nil {
		return
	}
	files, err := h.Forgejo.Files(ctx, owner, name, n)
	if err != nil {
		return
	}
	result, err := h.Engine.Run(ctx, pr, files)
	if err != nil {
		_ = h.Forgejo.Status(ctx, owner, name, pr.Head.SHA, forgejo.Status{State: "error", Context: "ts-reviewer", Description: "review incomplete: " + err.Error()})
		return
	}
	latest, err := h.Forgejo.Pull(ctx, owner, name, n)
	if err != nil || !strings.EqualFold(latest.Head.SHA, pr.Head.SHA) {
		return
	}
	critical := false
	for _, f := range result.Findings {
		if f.Severity == "critical" || f.Severity == "high" {
			critical = true
		}
		if f.Unrelated && f.OwningRepository != "" {
			h.fileIssue(ctx, f)
		}
	}
	body := format(pr, result)
	existingComments, commentErr := h.Forgejo.Comments(ctx, owner, name, n)
	updated := false
	if commentErr == nil {
		for _, existing := range existingComments {
			if strings.EqualFold(existing.User.Login, h.BotLogin) && strings.HasPrefix(existing.Body, "<!-- ts-reviewer:") {
				if h.Forgejo.UpdateComment(ctx, existing.ID, body) == nil {
					updated = true
				}
				break
			}
		}
	}
	if !updated {
		_ = h.Forgejo.Comment(ctx, owner, name, n, body)
	}
	event := "APPROVE"
	state := "success"
	desc := "No critical or high findings"
	if critical {
		event = "REQUEST_CHANGES"
		state = "failure"
		desc = "Critical/high findings require changes"
	}
	comments := make([]forgejo.ReviewComment, 0, len(result.Findings))
	for _, f := range result.Findings {
		if f.Path != "" && f.Line > 0 {
			comments = append(comments, forgejo.ReviewComment{Body: f.Body + "\n\n**Fix:** " + f.Fix, Path: f.Path, Line: f.Line, Side: "RIGHT"})
		}
	}
	if len(comments) > 0 {
		_ = h.Forgejo.Review(ctx, owner, name, n, forgejo.Review{Body: "ts-reviewer findings are documented above.", Event: event, Comments: comments})
	}
	_ = h.Forgejo.Status(ctx, owner, name, pr.Head.SHA, forgejo.Status{State: state, Context: "ts-reviewer", Description: desc})
}
func (h *Handler) command(owner, name string, n int, command string) {
	switch command {
	case "review", "recheck":
		h.review(owner, name, n)
	case "explain":
		_ = h.Forgejo.Comment(context.Background(), owner, name, n, "ts-reviewer: the latest evidence and suggested fix are in the marked review comment. Use `@"+h.BotLogin+" recheck` after pushing a correction.")
	case "dismiss":
		_ = h.Forgejo.Comment(context.Background(), owner, name, n, "ts-reviewer: dismissals require a maintainer workflow and are not accepted from an unverified comment.")
	}
}
func parseCommand(body, bot string) string {
	words := strings.Fields(strings.ToLower(body))
	mention := "@" + strings.ToLower(bot)
	for i, word := range words {
		if word == mention && i+1 < len(words) {
			switch words[i+1] {
			case "review", "recheck", "explain", "dismiss":
				return words[i+1]
			}
		}
	}
	return ""
}
func (h *Handler) fileIssue(ctx context.Context, f review.Finding) {
	parts := strings.SplitN(f.OwningRepository, "/", 2)
	if len(parts) != 2 || parts[0] != h.Organization {
		return
	}
	issues, err := h.Forgejo.Issues(ctx, parts[0], parts[1])
	if err != nil {
		return
	}
	marker := "<!-- ts-reviewer:" + f.ID + " -->"
	for _, i := range issues {
		if strings.Contains(i.Body, marker) {
			return
		}
	}
	_, _ = h.Forgejo.CreateIssue(ctx, parts[0], parts[1], "[ts-reviewer] "+f.Title, marker+"\n\n"+f.Body+"\n\n**Suggested fix:** "+f.Fix, nil)
}
func format(pr forgejo.PullRequest, r review.Result) string {
	var b strings.Builder
	fmt.Fprintf(&b, "<!-- ts-reviewer:%s -->\n## ts-reviewer review\n\n%s\n\n", pr.Head.SHA, r.Summary)
	if len(r.Findings) == 0 {
		b.WriteString("✅ No evidence-backed findings met the reporting threshold.\n")
		return b.String()
	}
	for _, f := range r.Findings {
		fmt.Fprintf(&b, "### %s · %s (`%s:%d`)\n%s\n\n**Fix:** %s\n\n", strings.ToUpper(f.Severity), f.Title, f.Path, f.Line, f.Body, f.Fix)
	}
	return b.String()
}
