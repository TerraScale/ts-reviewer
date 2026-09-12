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
	b, err = json.Marshal(Review{Body: "ok", Event: "APPROVE"})
	if err != nil {
		t.Fatal(err)
	}
	if string(b) != `{"body":"ok","event":"APPROVE"}` {
		t.Fatalf("review payload = %s", b)
	}
}
