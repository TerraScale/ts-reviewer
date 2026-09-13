package forgejo

import (
	"bytes"
	"context"
	"encoding/json/v2"
	"fmt"
	"io"
	"net/http"
	"net/url"
	"strconv"
	"strings"
)

type Client struct {
	base, token string
	http        *http.Client
}
type PullRequest struct {
	Number int    `json:"number"`
	Title  string `json:"title"`
	Body   string `json:"body"`
	Base   struct {
		Repo struct {
			Owner string `json:"owner"`
			Name  string `json:"name"`
		} `json:"repo"`
		SHA string `json:"sha"`
	} `json:"base"`
	Head PRBranch `json:"head"`
}
type PRBranch struct {
	SHA  string `json:"sha"`
	Ref  string `json:"ref"`
	Repo struct {
		Owner struct {
			Login string `json:"login"`
		} `json:"owner"`
		Name string `json:"name"`
	} `json:"repo"`
}
type File struct {
	Filename  string `json:"filename"`
	Status    string `json:"status"`
	Patch     string `json:"patch"`
	Additions int    `json:"additions"`
	Deletions int    `json:"deletions"`
}
type Comment struct {
	ID   int64  `json:"id"`
	Body string `json:"body"`
	User struct {
		Login string `json:"login"`
	} `json:"user"`
}
type ReviewComment struct {
	Body        string `json:"body"`
	Path        string `json:"path"`
	NewPosition int    `json:"new_position"`
	OldPosition int    `json:"old_position,omitempty"`
	ExtraLines  int    `json:"extra_lines_count,omitempty"`
}
type Review struct {
	Body     string          `json:"body"`
	Event    string          `json:"event"`
	CommitID string          `json:"commit_id"`
	Comments []ReviewComment `json:"comments,omitempty"`
}
type Issue struct {
	Number int    `json:"number"`
	Title  string `json:"title"`
	Body   string `json:"body"`
}
type Status struct {
	State       string `json:"state"`
	TargetURL   string `json:"target_url,omitempty"`
	Description string `json:"description"`
	Context     string `json:"context"`
}

func New(base, token string) *Client {
	return &Client{base: strings.TrimRight(base, "/"), token: token, http: http.DefaultClient}
}
func (c *Client) do(ctx context.Context, method, path string, in, out any) error {
	var body io.Reader
	if in != nil {
		b, err := json.Marshal(in)
		if err != nil {
			return err
		}
		body = bytes.NewReader(b)
	}
	req, err := http.NewRequestWithContext(ctx, method, c.base+path, body)
	if err != nil {
		return err
	}
	req.Header.Set("Authorization", "token "+c.token)
	req.Header.Set("Accept", "application/json")
	if in != nil {
		req.Header.Set("Content-Type", "application/json")
	}
	res, err := c.http.Do(req)
	if err != nil {
		return err
	}
	defer res.Body.Close()
	if res.StatusCode < 200 || res.StatusCode >= 300 {
		b, _ := io.ReadAll(io.LimitReader(res.Body, 4096))
		return fmt.Errorf("forgejo %s %s: status %d: %s", method, path, res.StatusCode, strings.TrimSpace(string(b)))
	}
	if out != nil {
		return json.UnmarshalRead(res.Body, out)
	}
	return nil
}
func repo(owner, name string) string {
	return "/api/v1/repos/" + url.PathEscape(owner) + "/" + url.PathEscape(name)
}
func (c *Client) Pull(ctx context.Context, owner, name string, n int) (PullRequest, error) {
	var p PullRequest
	err := c.do(ctx, "GET", repo(owner, name)+"/pulls/"+strconv.Itoa(n), nil, &p)
	return p, err
}
func (c *Client) Files(ctx context.Context, owner, name string, n int) ([]File, error) {
	var f []File
	err := c.do(ctx, "GET", repo(owner, name)+"/pulls/"+strconv.Itoa(n)+"/files?limit=100", nil, &f)
	return f, err
}
func (c *Client) Diff(ctx context.Context, owner, name string, n int) (string, error) {
	req, err := http.NewRequestWithContext(ctx, "GET", c.base+repo(owner, name)+"/pulls/"+strconv.Itoa(n)+".diff", nil)
	if err != nil {
		return "", err
	}
	req.Header.Set("Authorization", "token "+c.token)
	res, err := c.http.Do(req)
	if err != nil {
		return "", err
	}
	defer res.Body.Close()
	if res.StatusCode < 200 || res.StatusCode >= 300 {
		b, _ := io.ReadAll(io.LimitReader(res.Body, 4096))
		return "", fmt.Errorf("forgejo diff: status %d: %s", res.StatusCode, strings.TrimSpace(string(b)))
	}
	b, err := io.ReadAll(io.LimitReader(res.Body, 8<<20+1))
	if err != nil {
		return "", err
	}
	if len(b) > 8<<20 {
		return "", fmt.Errorf("pull request diff exceeds limit")
	}
	return string(b), nil
}
func (c *Client) Comments(ctx context.Context, owner, name string, n int) ([]Comment, error) {
	var v []Comment
	err := c.do(ctx, "GET", repo(owner, name)+"/issues/"+strconv.Itoa(n)+"/comments?limit=100", nil, &v)
	return v, err
}
func (c *Client) Comment(ctx context.Context, owner, name string, n int, body string) error {
	return c.do(ctx, "POST", repo(owner, name)+"/issues/"+strconv.Itoa(n)+"/comments", map[string]string{"body": body}, nil)
}

func (c *Client) UpdateComment(ctx context.Context, owner, name string, id int64, body string) error {
	return c.do(ctx, "PATCH", repo(owner, name)+"/issues/comments/"+strconv.FormatInt(id, 10), map[string]string{"body": body}, nil)
}
func (c *Client) Review(ctx context.Context, owner, name string, n int, r Review) error {
	return c.do(ctx, "POST", repo(owner, name)+"/pulls/"+strconv.Itoa(n)+"/reviews", r, nil)
}
func (c *Client) Status(ctx context.Context, owner, name, sha string, s Status) error {
	return c.do(ctx, "POST", repo(owner, name)+"/statuses/"+url.PathEscape(sha), s, nil)
}
func (c *Client) CreateIssue(ctx context.Context, owner, name, title, body string, labels []int) (Issue, error) {
	var i Issue
	err := c.do(ctx, "POST", repo(owner, name)+"/issues", map[string]any{"title": title, "body": body, "labels": labels}, &i)
	return i, err
}
func (c *Client) Issues(ctx context.Context, owner, name string) ([]Issue, error) {
	var i []Issue
	err := c.do(ctx, "GET", repo(owner, name)+"/issues?state=all&limit=100", nil, &i)
	return i, err
}
