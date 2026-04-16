package service

import (
	"testing"
	"time"

	pb "jumjump/server/internal/pb"
)

func TestCampShape(t *testing.T) {
	board, camps := buildStarBoard()
	if len(board) != 121 {
		t.Fatalf("star board size=%d", len(board))
	}
	for i := 0; i < 6; i++ {
		if len(camps[i]) != 10 {
			t.Fatalf("camp[%d] size=%d", i, len(camps[i]))
		}
	}
}

func TestLegalMove(t *testing.T) {
	rm := &room{Pieces: map[string]uint64{}}
	rm.Pieces[posKey(0, 0)] = 1

	if !isLegalMove(rm, &pb.Pos{X: 0, Y: 0}, &pb.Pos{X: 1, Y: 0}) {
		t.Fatal("expected adjacent hex move legal")
	}
	if isLegalMove(rm, &pb.Pos{X: 0, Y: 0}, &pb.Pos{X: 2, Y: 0}) {
		t.Fatal("expected jump without middle piece illegal")
	}

	rm.Pieces[posKey(1, 0)] = 2
	if !isLegalMove(rm, &pb.Pos{X: 0, Y: 0}, &pb.Pos{X: 2, Y: 0}) {
		t.Fatal("expected jump with middle piece legal")
	}
}

func TestNewRoomInitPieces(t *testing.T) {
	g := NewGameService()
	a := &mockSession{id: "a"}
	b := &mockSession{id: "b"}
	g.playersBySession[a] = &player{UserID: 11}
	g.playersBySession[b] = &player{UserID: 22}

	rm := g.newRoom("r1", a, b)
	if len(rm.Pieces) != 20 {
		t.Fatalf("piece count=%d", len(rm.Pieces))
	}
}

func TestValidateMovePath_MultiJump(t *testing.T) {
	rm := &room{Pieces: map[string]uint64{}}
	rm.Pieces[posKey(0, 0)] = 1
	rm.Pieces[posKey(1, 0)] = 2
	rm.Pieces[posKey(3, 0)] = 2

	ok, path, reason := validateMovePath(
		rm,
		&pb.Pos{X: 0, Y: 0},
		&pb.Pos{X: 4, Y: 0},
		[]*pb.Pos{
			{X: 2, Y: 0},
			{X: 4, Y: 0},
		},
	)
	if !ok {
		t.Fatalf("expected multi-jump valid, reason=%s", reason)
	}
	if len(path) != 2 {
		t.Fatalf("unexpected path len=%d", len(path))
	}
}

func TestValidateMovePath_InvalidTail(t *testing.T) {
	rm := &room{Pieces: map[string]uint64{}}
	rm.Pieces[posKey(0, 0)] = 1
	rm.Pieces[posKey(1, 0)] = 2

	ok, _, reason := validateMovePath(
		rm,
		&pb.Pos{X: 0, Y: 0},
		&pb.Pos{X: 2, Y: 0},
		[]*pb.Pos{{X: 2, Y: 2}},
	)
	if ok {
		t.Fatal("expected invalid path tail")
	}
	if reason == "" {
		t.Fatal("expected reject reason")
	}
}

func TestProcessRoomTimeouts_DisconnectLose(t *testing.T) {
	g := &GameService{
		playersBySession: map[Session]*player{},
		playerByToken:    map[string]*player{},
		sessionByUserID:  map[uint64]Session{},
		rooms:            map[string]*room{},
		roomBySession:    map[Session]*room{},
		roomByUserID:     map[uint64]*room{},
	}
	a := &mockSession{id: "a"}
	b := &mockSession{id: "b"}
	rm := &room{
		ID:             "r1",
		Players:        [2]Session{a, b},
		PlayerUsers:    [2]uint64{11, 22},
		OfflineSinceMs: [2]int64{time.Now().Add(-disconnectGrace - time.Second).UnixMilli(), 0},
	}
	g.rooms[rm.ID] = rm
	g.roomBySession[a] = rm
	g.roomBySession[b] = rm
	g.roomByUserID[11] = rm
	g.roomByUserID[22] = rm

	g.processRoomTimeouts(time.Now().UnixMilli())

	if _, ok := g.rooms[rm.ID]; ok {
		t.Fatal("room should be removed after disconnect lose")
	}
	if _, ok := g.roomByUserID[11]; ok {
		t.Fatal("roomByUserID should be cleaned for offline player")
	}
	if _, ok := g.roomByUserID[22]; ok {
		t.Fatal("roomByUserID should be cleaned for winner")
	}
}

type mockSession struct {
	id string
}

func (m *mockSession) ID() string {
	return m.id
}

func (m *mockSession) SendBinary(_ []byte) error {
	return nil
}
