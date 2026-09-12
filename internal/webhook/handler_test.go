package webhook

import "testing"

func TestValidSignature(t *testing.T) {
	body := []byte(`{"action":"opened"}`)
	if validSignature(body, "sha256=bad", "secret") {
		t.Fatal("accepted invalid signature")
	}
}

func TestParseCommand(t *testing.T) {
	if got := parseCommand("please @terrascale-bot recheck", "terrascale-bot"); got != "recheck" {
		t.Fatalf("command = %q", got)
	}
	if got := parseCommand("@other-bot review", "terrascale-bot"); got != "" {
		t.Fatalf("unexpected command %q", got)
	}
}
