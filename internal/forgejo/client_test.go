package forgejo

import (
	"encoding/json/v2"
	"testing"
)

func TestMutationPayloadsUseForgejoFieldNames(t *testing.T) {
	b, err := json.Marshal(Status{State: "success", Context: "ts-reviewer", Description: "ok"})
	if err != nil {
		t.Fatal(err)
	}
	if string(b) != `{"state":"success","description":"ok","context":"ts-reviewer"}` {
		t.Fatalf("status payload = %s", b)
	}
	b, err = json.Marshal(Review{Body: "ok", Event: "APPROVE", CommitID: "abc"})
	if err != nil {
		t.Fatal(err)
	}
	if string(b) != `{"body":"ok","event":"APPROVE","commit_id":"abc"}` {
		t.Fatalf("review payload = %s", b)
	}
}

func TestUnifiedDiffReviewPosition(t *testing.T) {
	raw := "diff --git a/main.go b/main.go\n@@ -1,2 +1,3 @@\n package main\n+var value = 1\n func main() {}\n"
	files := ParseUnifiedDiff(raw)
	if len(files) != 1 || files[0].Filename != "main.go" {
		t.Fatalf("files = %#v", files)
	}
	if got := ReviewPosition(files[0].Patch, 2); got == 0 {
		t.Fatal("missing position for added line")
	}
}
