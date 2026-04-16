package config

import "os"

// Config stores runtime settings for the game server.
type Config struct {
	Addr string
}

func FromEnv() Config {
	addr := os.Getenv("JUMJUMP_ADDR")
	if addr == "" {
		addr = ":8080"
	}
	return Config{Addr: addr}
}
