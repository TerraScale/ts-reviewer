package config

import "testing"

func TestLoadRequiresSecrets(t *testing.T) {
	if _, err := Load(nil); err == nil {
		t.Fatal("accepted missing secrets")
	}
}
