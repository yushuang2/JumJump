package gateway

import (
	"context"
	"fmt"
	"net/http"
	"sync/atomic"

	"github.com/gorilla/websocket"
	"jumjump/server/internal/config"
	"jumjump/server/internal/service"
)

// Server owns HTTP endpoints and websocket upgrades.
type Server struct {
	cfg      config.Config
	svc      *service.GameService
	upgrader websocket.Upgrader
	nextID   uint64
}

func NewServer(cfg config.Config, svc *service.GameService) *Server {
	return &Server{
		cfg: cfg,
		svc: svc,
		upgrader: websocket.Upgrader{
			ReadBufferSize:  1024,
			WriteBufferSize: 1024,
			CheckOrigin: func(r *http.Request) bool {
				return true
			},
		},
	}
}

func (s *Server) Start(ctx context.Context) error {
	mux := http.NewServeMux()
	mux.HandleFunc("/healthz", func(w http.ResponseWriter, r *http.Request) {
		w.WriteHeader(http.StatusOK)
		_, _ = w.Write([]byte("ok"))
	})
	mux.HandleFunc("/ws", s.handleWS)

	httpServer := &http.Server{Addr: s.cfg.Addr, Handler: mux}

	go func() {
		<-ctx.Done()
		_ = httpServer.Shutdown(context.Background())
	}()

	var err error
	if s.cfg.TLSCertFile != "" || s.cfg.TLSKeyFile != "" {
		if s.cfg.TLSCertFile == "" || s.cfg.TLSKeyFile == "" {
			return fmt.Errorf("both JUMJUMP_TLS_CERT_FILE and JUMJUMP_TLS_KEY_FILE are required for TLS")
		}
		err = httpServer.ListenAndServeTLS(s.cfg.TLSCertFile, s.cfg.TLSKeyFile)
	} else {
		err = httpServer.ListenAndServe()
	}
	if err == http.ErrServerClosed {
		return nil
	}
	return err
}

func (s *Server) handleWS(w http.ResponseWriter, r *http.Request) {
	conn, err := s.upgrader.Upgrade(w, r, nil)
	if err != nil {
		return
	}

	sid := fmt.Sprintf("s-%d", atomic.AddUint64(&s.nextID, 1))
	client := NewClient(sid, conn, s.svc)
	client.Run()
}
