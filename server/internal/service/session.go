package service

// Session is implemented by the websocket client wrapper.
type Session interface {
	ID() string
	SendBinary(data []byte) error
}
