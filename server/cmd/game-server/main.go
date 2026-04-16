package main

import (
	"context"
	"log"
	"os/signal"
	"syscall"

	"jumjump/server/internal/config"
	"jumjump/server/internal/gateway"
	"jumjump/server/internal/service"
)

func main() {
	ctx, stop := signal.NotifyContext(context.Background(), syscall.SIGINT, syscall.SIGTERM)
	defer stop()

	cfg := config.FromEnv()
	svc := service.NewGameService()
	srv := gateway.NewServer(cfg, svc)

	log.Printf("game-server listening on %s", cfg.Addr)
	if err := srv.Start(ctx); err != nil {
		log.Fatalf("server stopped: %v", err)
	}
}
