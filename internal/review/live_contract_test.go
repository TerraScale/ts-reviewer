package review

import (
	"context"
	"net/http"
	"os"
	"testing"
	"time"
	"git.terrascale.tech/TerraScale/ts-reviewer/internal/forgejo"
)

func TestLiveReviewEngineContract(t *testing.T) {
	ft, pt := os.Getenv("TS_LIVE_FORGEJO_TOKEN"), os.Getenv("TS_LIVE_PROXY_TOKEN")
	if ft == "" || pt == "" { t.Skip("live credentials not configured") }
	f := forgejo.New("https://git.terrascale.tech", ft); pr, err := f.Pull(context.Background(), "TerraScale", "ts-reviewer", 1); if err != nil { t.Fatal(err) }; raw, err := f.Diff(context.Background(), "TerraScale", "ts-reviewer", 1); if err != nil { t.Fatal(err) }
	e := &Engine{URL:"http://10.11.99.1:8317/v1", Token:pt, Model:"glm-5.3-flash", HTTP:&http.Client{Timeout:2*time.Minute}, MaxConcurrent:10, MaxDiffBytes:8<<20}
	result, err := e.Run(context.Background(), pr, forgejo.ParseUnifiedDiff(raw)); if err != nil { t.Fatal(err) }; if result.Summary == "" { t.Fatal("empty review summary") }
}
