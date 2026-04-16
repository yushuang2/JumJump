package gateway

import (
	"log"
	"sync"
	"time"

	"github.com/gorilla/websocket"
	"jumjump/server/internal/service"
)

const (
	writeWait = 8 * time.Second
	pongWait  = 30 * time.Second
	pingEvery = 10 * time.Second
)

// Client wraps one websocket session.
type Client struct {
	id     string
	conn   *websocket.Conn
	svc    *service.GameService
	sendCh chan []byte
	mu     sync.Mutex
	closed bool
}

func NewClient(id string, conn *websocket.Conn, svc *service.GameService) *Client {
	return &Client{
		id:     id,
		conn:   conn,
		svc:    svc,
		sendCh: make(chan []byte, 64),
	}
}

func (c *Client) ID() string {
	return c.id
}

func (c *Client) Run() {
	c.conn.SetReadLimit(1024 * 1024)
	_ = c.conn.SetReadDeadline(time.Now().Add(pongWait))
	c.conn.SetPongHandler(func(string) error {
		return c.conn.SetReadDeadline(time.Now().Add(pongWait))
	})

	c.svc.OnConnect(c)
	go c.writeLoop()
	c.readLoop()
}

func (c *Client) readLoop() {
	defer c.Close()
	for {
		msgType, payload, err := c.conn.ReadMessage()
		if err != nil {
			return
		}
		if msgType != websocket.BinaryMessage {
			continue
		}
		if err := c.svc.OnPacket(c, payload); err != nil {
			log.Printf("session=%s packet error: %v", c.id, err)
		}
	}
}

func (c *Client) writeLoop() {
	ticker := time.NewTicker(pingEvery)
	defer ticker.Stop()

	for {
		select {
		case data, ok := <-c.sendCh:
			if !ok {
				return
			}
			if err := c.conn.SetWriteDeadline(time.Now().Add(writeWait)); err != nil {
				return
			}
			if err := c.conn.WriteMessage(websocket.BinaryMessage, data); err != nil {
				return
			}
		case <-ticker.C:
			if err := c.conn.SetWriteDeadline(time.Now().Add(writeWait)); err != nil {
				return
			}
			if err := c.conn.WriteMessage(websocket.PingMessage, nil); err != nil {
				return
			}
		}
	}
}

func (c *Client) SendBinary(data []byte) error {
	c.mu.Lock()
	defer c.mu.Unlock()
	if c.closed {
		return ErrSessionClosed
	}
	select {
	case c.sendCh <- data:
		return nil
	default:
		return ErrSessionClosed
	}
}

func (c *Client) Close() error {
	c.mu.Lock()
	if c.closed {
		c.mu.Unlock()
		return nil
	}
	c.closed = true
	close(c.sendCh)
	c.mu.Unlock()

	c.svc.OnDisconnect(c)
	return c.conn.Close()
}
