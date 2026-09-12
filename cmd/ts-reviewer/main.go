package main

import (
	"context"
	"errors"
	"flag"
	"git.terrascale.tech/TerraScale/ts-reviewer/internal/config"
	"git.terrascale.tech/TerraScale/ts-reviewer/internal/forgejo"
	"git.terrascale.tech/TerraScale/ts-reviewer/internal/review"
	"git.terrascale.tech/TerraScale/ts-reviewer/internal/webhook"
	"log/slog"
	"net/http"
	"os"
	"os/signal"
	"syscall"
)

func main() {
	c, err := config.Load(os.Args[1:])
	if err != nil {
		if errors.Is(err, flag.ErrHelp) {
			return
		}
		slog.Error("configuration invalid", "error", err)
		os.Exit(2)
	}
	f := forgejo.New(c.ForgejoURL, c.ForgejoToken)
	e := &review.Engine{URL: c.ProxyURL, Token: c.ProxyToken, Model: c.Model, HTTP: http.DefaultClient, MaxConcurrent: c.MaxConcurrent, MaxDiffBytes: c.MaxDiffBytes}
	h := &webhook.Handler{Forgejo: f, Engine: e, Organization: c.Organization, BotLogin: c.BotLogin, WebhookSecret: os.Getenv("TS_REVIEWER_WEBHOOK_SECRET"), ReviewTimeout: c.ReviewTimeout}
	mux := http.NewServeMux()
	mux.Handle("POST /webhook", h)
	mux.HandleFunc("GET /healthz", func(w http.ResponseWriter, _ *http.Request) { w.WriteHeader(http.StatusOK) })
	srv := &http.Server{Addr: c.ListenAddr, Handler: mux}
	ctx, stop := signal.NotifyContext(context.Background(), syscall.SIGINT, syscall.SIGTERM)
	defer stop()
	go func() {
		slog.Info("ts-reviewer listening", "addr", c.ListenAddr, "model", c.Model, "max_concurrent", c.MaxConcurrent)
		if err := srv.ListenAndServe(); err != nil && !errors.Is(err, http.ErrServerClosed) {
			slog.Error("server stopped", "error", err)
			stop()
		}
	}()
	<-ctx.Done()
	shutdown, cancel := context.WithTimeout(context.Background(), c.ReviewTimeout)
	defer cancel()
	_ = srv.Shutdown(shutdown)
}
