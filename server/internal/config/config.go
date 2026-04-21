package config

import "os"

// Config stores runtime settings for the game server.
type Config struct {
	Addr        string
	TLSCertFile string
	TLSKeyFile  string
}

func FromEnv() Config {
	addr := os.Getenv("JUMJUMP_ADDR")
	if addr == "" {
		addr = ":3000"
	}
	return Config{
		Addr:        addr,
		TLSCertFile: os.Getenv("JUMJUMP_TLS_CERT_FILE"),
		TLSKeyFile:  os.Getenv("JUMJUMP_TLS_KEY_FILE"),
	}
}
