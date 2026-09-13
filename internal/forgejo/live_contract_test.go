package forgejo

import (
	"context"
	"os"
	"strings"
	"testing"
)

func TestLiveForgejoPullContract(t *testing.T) {
	token := os.Getenv("TS_LIVE_FORGEJO_TOKEN")
	if token == "" {
		t.Skip("live contract token not configured")
	}
	c := New("https://git.terrascale.tech", token)
	pr, err := c.Pull(context.Background(), "TerraScale", "ts-reviewer", 1)
	if err != nil {
		t.Fatal(err)
	}
	if pr.Head.SHA == "" || pr.Head.Ref == "" {
		t.Fatalf("missing head: %#v", pr.Head)
	}
	files, err := c.Files(context.Background(), "TerraScale", "ts-reviewer", 1)
	if err != nil {
		t.Fatal(err)
	}
	raw, err := c.Diff(context.Background(), "TerraScale", "ts-reviewer", 1)
	if err != nil {
		t.Fatal(err)
	}
	if len(files) == 0 || raw == "" {
		t.Fatalf("missing files or diff: files=%d diff=%d", len(files), len(raw))
	}
	parsed := ParseUnifiedDiff(raw)
	if len(parsed) == 0 {
		t.Fatal("diff parser returned no files")
	}
	if !strings.Contains(parsed[0].Patch, "@@") {
		t.Fatal("parsed diff has no hunk")
	}
}
