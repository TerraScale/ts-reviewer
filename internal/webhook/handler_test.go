package webhook

import "testing"

func TestValidSignature(t *testing.T) {
	body := []byte(`{"action":"opened"}`)
	if validSignature(body, "sha256=bad", "secret") {
		t.Fatal("accepted invalid signature")
	}
}
