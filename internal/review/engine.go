package review

import (
	"bytes"
	"context"
	"encoding/json/v2"
	"errors"
	"fmt"
	"git.terrascale.tech/TerraScale/ts-reviewer/internal/forgejo"
	"io"
	"net/http"
	"regexp"
	"strings"
	"sync"
	"time"
)

type Finding struct {
	ID               string  `json:"id"`
	Severity         string  `json:"severity"`
	Title            string  `json:"title"`
	Body             string  `json:"body"`
	Fix              string  `json:"fix"`
	Path             string  `json:"path"`
	Line             int     `json:"line"`
	Confidence       float64 `json:"confidence"`
	Unrelated        bool    `json:"unrelated"`
	OwningRepository string  `json:"owning_repository"`
}
type Result struct {
	Summary  string    `json:"summary"`
	Findings []Finding `json:"findings"`
}
type Engine struct {
	URL, Token, Model string
	HTTP              *http.Client
	MaxConcurrent     int
	MaxDiffBytes      int64
}

var fenced = regexp.MustCompile("(?s)```(?:json)?\\s*(.*?)```")

func (e *Engine) Run(ctx context.Context, pr forgejo.PullRequest, files []forgejo.File) (Result, error) {
	diff := strings.Builder{}
	for _, f := range files {
		fmt.Fprintf(&diff, "FILE %s (%s +%d -%d)\\n%s\\n\\n", f.Filename, f.Status, f.Additions, f.Deletions, f.Patch)
	}
	limit := e.MaxDiffBytes
	if limit <= 0 {
		limit = 8 << 20
	}
	if int64(diff.Len()) > limit {
		return Result{}, errors.New("pull request diff exceeds review limit")
	}
	passes := []string{"correctness, data integrity, concurrency, and error handling", "security, authorization, secret exposure, and injection", "API contracts, migrations, compatibility, and operational behavior", "tests, observability, failure recovery, and adversarial edge cases"}
	results := make([]Result, len(passes))
	sem := make(chan struct{}, max(1, e.MaxConcurrent))
	errc := make(chan error, len(passes))
	done := make(chan struct{})
	var wg sync.WaitGroup
	for i, p := range passes {
		wg.Go(func() {
			sem <- struct{}{}
			defer func() { <-sem }()
			r, err := e.pass(ctx, pr, diff.String(), p)
			if err != nil {
				errc <- err
				return
			}
			results[i] = r
		})
	}
	go func() { wg.Wait(); close(done) }()
	select {
	case <-done:
	case <-ctx.Done():
		return Result{}, ctx.Err()
	}
	select {
	case err := <-errc:
		return Result{}, err
	default:
		candidates := merge(results)
		return e.adversarialPass(ctx, candidates, diff.String())
	}
}

func (e *Engine) adversarialPass(ctx context.Context, candidates Result, diff string) (Result, error) {
	payload, err := json.Marshal(candidates)
	if err != nil {
		return Result{}, err
	}
	prompt := fmt.Sprintf(`Act as the final adversarial reviewer. The candidate findings below came from several passes. Remove anything unsupported by the patch, duplicate, speculative, or merely stylistic. Preserve concrete correctness, security, data-integrity, and operational defects. Never invent a path or line. Return the same JSON schema and JSON only. Candidate findings: %s\nPATCH EVIDENCE:\n%s`, payload, diff)
	judged, err := e.chat(ctx, prompt)
	if err != nil {
		return Result{}, fmt.Errorf("adversarial review: %w", err)
	}
	return merge([]Result{judged}), nil
}
func (e *Engine) pass(ctx context.Context, pr forgejo.PullRequest, diff, focus string) (Result, error) {
	prompt := fmt.Sprintf(`You are a severe senior reviewer. Review only the supplied patch. Do not invent files, APIs, runtime behavior, or vulnerabilities. Trace callers and invariants mentally. Report only actionable findings supported by patch evidence. A finding must include an exact changed path and 1-based line when possible, impact, and a concrete fix or test. Severity is critical, high, medium, or low. Use unrelated=true only when the defect is proven independent of this patch; include owning_repository only then. Return JSON only: {"summary":string,"findings":[{"id":string,"severity":string,"title":string,"body":string,"fix":string,"path":string,"line":number,"confidence":number,"unrelated":boolean,"owning_repository":string}]}. Focus: %s\nPR: %s\n%s`, focus, pr.Title, diff)
	return e.chat(ctx, prompt)
}
func (e *Engine) chat(ctx context.Context, prompt string) (Result, error) {
	reqBody := map[string]any{"model": e.Model, "temperature": 0.1, "max_tokens": 12000, "messages": []map[string]string{{"role": "system", "content": "You are a code-review engine. Follow the requested JSON schema exactly."}, {"role": "user", "content": prompt}}}
	b, err := json.Marshal(reqBody)
	if err != nil {
		return Result{}, err
	}
	req, err := http.NewRequestWithContext(ctx, "POST", e.URL+"/chat/completions", bytes.NewReader(b))
	if err != nil {
		return Result{}, err
	}
	req.Header.Set("Authorization", "Bearer "+e.Token)
	req.Header.Set("Content-Type", "application/json")
	res, err := e.HTTP.Do(req)
	if err != nil {
		return Result{}, err
	}
	defer res.Body.Close()
	raw, err := io.ReadAll(io.LimitReader(res.Body, 2<<20))
	if err != nil {
		return Result{}, err
	}
	if res.StatusCode/100 != 2 {
		return Result{}, fmt.Errorf("cliproxy status %d: %s", res.StatusCode, strings.TrimSpace(string(raw)))
	}
	var envelope struct {
		Choices []struct {
			Message struct {
				Content string `json:"content"`
			} `json:"message"`
		} `json:"choices"`
	}
	if err := json.Unmarshal(raw, &envelope); err != nil {
		return Result{}, err
	}
	if len(envelope.Choices) == 0 {
		return Result{}, errors.New("cliproxy returned no choices")
	}
	content := strings.TrimSpace(envelope.Choices[0].Message.Content)
	if m := fenced.FindStringSubmatch(content); len(m) == 2 {
		content = m[1]
	}
	var out Result
	if err := json.Unmarshal([]byte(content), &out); err != nil {
		return Result{}, fmt.Errorf("review JSON: %w", err)
	}
	return out, nil
}
func merge(rs []Result) Result {
	out := Result{Summary: "Multi-pass review completed."}
	seen := map[string]bool{}
	for _, r := range rs {
		if r.Summary != "" {
			out.Summary += " " + r.Summary
		}
		for _, f := range r.Findings {
			if f.ID == "" {
				f.ID = fmt.Sprintf("%s:%d:%s", f.Path, f.Line, f.Title)
			}
			key := strings.ToLower(f.ID + f.Path + fmt.Sprint(f.Line))
			if seen[key] || f.Confidence < 0.72 {
				continue
			}
			seen[key] = true
			out.Findings = append(out.Findings, f)
		}
	}
	return out
}

var _ = time.Second
