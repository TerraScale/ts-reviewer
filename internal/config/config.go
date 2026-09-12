package config

import (
	"errors"
	"flag"
	"fmt"
	"os"
	"strconv"
	"strings"
	"time"
)

type Config struct {
	ListenAddr, ForgejoURL, ForgejoToken, ProxyURL, ProxyToken, Model, Organization, BotLogin string
	MaxConcurrent                                                                             int
	ReviewTimeout                                                                             time.Duration
	MaxDiffBytes                                                                              int64
}

func Load(args []string) (Config, error) {
	var c Config
	fs := flag.NewFlagSet("ts-reviewer", flag.ContinueOnError)
	fs.StringVar(&c.ListenAddr, "listen", env("TS_REVIEWER_LISTEN", ":8080"), "HTTP listen address")
	fs.StringVar(&c.ForgejoURL, "forgejo-url", env("TS_REVIEWER_FORGEJO_URL", "https://git.terrascale.tech"), "Forgejo base URL")
	fs.StringVar(&c.ForgejoToken, "forgejo-token", os.Getenv("TS_REVIEWER_FORGEJO_TOKEN"), "Forgejo API token")
	fs.StringVar(&c.ProxyURL, "proxy-url", env("TS_REVIEWER_PROXY_URL", "http://10.11.99.1:8317/v1"), "CLIProxy OpenAI-compatible base URL")
	fs.StringVar(&c.ProxyToken, "proxy-token", os.Getenv("TS_REVIEWER_PROXY_TOKEN"), "CLIProxy token")
	fs.StringVar(&c.Model, "model", env("TS_REVIEWER_MODEL", "glm-5.3-flash"), "review model")
	fs.StringVar(&c.Organization, "organization", env("TS_REVIEWER_ORGANIZATION", "TerraScale"), "Forgejo organization")
	fs.StringVar(&c.BotLogin, "bot-login", env("TS_REVIEWER_BOT_LOGIN", "terrascale-bot"), "bot login")
	fs.IntVar(&c.MaxConcurrent, "max-concurrent", intEnv("TS_REVIEWER_MAX_CONCURRENT", 10), "maximum concurrent review jobs")
	timeout := env("TS_REVIEWER_TIMEOUT", "10m")
	fs.StringVar(&timeout, "timeout", timeout, "per-review deadline")
	fs.Int64Var(&c.MaxDiffBytes, "max-diff-bytes", int64Env("TS_REVIEWER_MAX_DIFF_BYTES", 8<<20), "maximum diff sent to the model")
	if err := fs.Parse(args); err != nil {
		return Config{}, err
	}
	d, err := time.ParseDuration(timeout)
	if err != nil {
		return Config{}, fmt.Errorf("timeout: %w", err)
	}
	c.ReviewTimeout = d
	c.ForgejoURL = strings.TrimRight(c.ForgejoURL, "/")
	c.ProxyURL = strings.TrimRight(c.ProxyURL, "/")
	if c.ForgejoToken == "" || c.ProxyToken == "" {
		return Config{}, errors.New("TS_REVIEWER_FORGEJO_TOKEN and TS_REVIEWER_PROXY_TOKEN are required")
	}
	if c.MaxConcurrent < 1 || c.MaxConcurrent > 100 {
		return Config{}, errors.New("max-concurrent must be between 1 and 100")
	}
	if c.ReviewTimeout <= 0 {
		return Config{}, errors.New("timeout must be positive")
	}
	return c, nil
}
func env(k, fallback string) string {
	if v := os.Getenv(k); v != "" {
		return v
	}
	return fallback
}
func intEnv(k string, fallback int) int {
	if v, e := strconv.Atoi(os.Getenv(k)); e == nil && v > 0 {
		return v
	}
	return fallback
}
func int64Env(k string, fallback int64) int64 {
	if v, e := strconv.ParseInt(os.Getenv(k), 10, 64); e == nil && v > 0 {
		return v
	}
	return fallback
}
