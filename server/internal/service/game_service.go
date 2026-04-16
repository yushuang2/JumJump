package service

import (
	"errors"
	"fmt"
	"sort"
	"sync"
	"sync/atomic"
	"time"

	"google.golang.org/protobuf/proto"
	pb "jumjump/server/internal/pb"
)

const (
	centerRadius    = int32(4)
	campDepth       = int32(4)
	turnDuration    = 30 * time.Second
	disconnectGrace = 20 * time.Second
)

var (
	ErrInvalidPayload = errors.New("invalid payload")
	starBoardCells    map[string]struct{}
	starCamps         [6][]boardPos
)

type player struct {
	UserID uint64
	Token  string
}

type boardPos struct {
	X int32
	Y int32
}

var hexDirs = []boardPos{
	{X: 1, Y: 0},
	{X: 1, Y: -1},
	{X: 0, Y: -1},
	{X: -1, Y: 0},
	{X: -1, Y: 1},
	{X: 0, Y: 1},
}

func init() {
	starBoardCells, starCamps = buildStarBoard()
}

type room struct {
	ID             string
	Version        uint32
	Players        [2]Session
	PlayerUsers    [2]uint64
	OfflineSinceMs [2]int64
	TurnIndex      int
	DeadlineMs     int64
	Finished       bool
	Pieces         map[string]uint64
	TargetCamp     [2]map[string]struct{}
}

// GameService keeps authoritative in-memory room state for MVP.
type GameService struct {
	mu sync.Mutex

	playersBySession map[Session]*player
	playerByToken    map[string]*player
	sessionByUserID  map[uint64]Session
	waiting          Session
	rooms            map[string]*room
	roomBySession    map[Session]*room
	roomByUserID     map[uint64]*room

	nextUserID uint64
	nextRoomID uint64
}

func NewGameService() *GameService {
	g := &GameService{
		playersBySession: make(map[Session]*player),
		playerByToken:    make(map[string]*player),
		sessionByUserID:  make(map[uint64]Session),
		rooms:            make(map[string]*room),
		roomBySession:    make(map[Session]*room),
		roomByUserID:     make(map[uint64]*room),
	}
	go g.timeoutLoop()
	return g
}

func (g *GameService) OnConnect(s Session) {
	g.mu.Lock()
	defer g.mu.Unlock()
	g.playersBySession[s] = &player{}
}

func (g *GameService) OnDisconnect(s Session) {
	g.mu.Lock()
	defer g.mu.Unlock()

	if g.waiting == s {
		g.waiting = nil
	}

	var userID uint64
	if p, ok := g.playersBySession[s]; ok {
		userID = p.UserID
		if p.UserID != 0 {
			delete(g.sessionByUserID, p.UserID)
		}
	}

	if rm, ok := g.roomBySession[s]; ok {
		if !rm.Finished {
			for i := 0; i < 2; i++ {
				if rm.Players[i] == s {
					rm.Players[i] = nil
				}
				if userID != 0 && rm.PlayerUsers[i] == userID {
					rm.OfflineSinceMs[i] = time.Now().UnixMilli()
				}
			}
		}
	}

	delete(g.roomBySession, s)
	delete(g.playersBySession, s)
}

func (g *GameService) OnPacket(s Session, raw []byte) error {
	env := &pb.Envelope{}
	if err := proto.Unmarshal(raw, env); err != nil {
		return err
	}

	switch pb.Cmd(env.GetCmd()) {
	case pb.Cmd_C2S_LOGIN:
		return g.handleLogin(s, env)
	case pb.Cmd_C2S_MATCH_START:
		return g.handleMatchStart(s, env)
	case pb.Cmd_C2S_MATCH_CANCEL:
		return g.handleMatchCancel(s)
	case pb.Cmd_C2S_MOVE:
		return g.handleMove(s, env)
	case pb.Cmd_C2S_PING:
		return g.handlePing(s, env)
	case pb.Cmd_C2S_RECONNECT:
		return g.handleReconnect(s, env)
	default:
		return g.sendError(s, 400, "unknown cmd")
	}
}

func (g *GameService) handleLogin(s Session, env *pb.Envelope) error {
	req := &pb.LoginReq{}
	if err := proto.Unmarshal(env.GetPayload(), req); err != nil {
		return ErrInvalidPayload
	}

	g.mu.Lock()
	defer g.mu.Unlock()

	p := g.playersBySession[s]
	if p == nil {
		p = &player{}
		g.playersBySession[s] = p
	}
	if p.UserID == 0 {
		p.UserID = atomic.AddUint64(&g.nextUserID, 1)
		p.Token = fmt.Sprintf("token-%d", p.UserID)
	}
	g.playerByToken[p.Token] = p
	g.sessionByUserID[p.UserID] = s

	ack := &pb.LoginAck{
		UserId:        p.UserID,
		Token:         p.Token,
		TokenExpireAt: time.Now().Add(24 * time.Hour).UnixMilli(),
	}
	return g.sendEnvelope(s, pb.Cmd_S2C_LOGIN_ACK, ack, "", 0, env.GetSeq())
}

func (g *GameService) handleMatchStart(s Session, env *pb.Envelope) error {
	req := &pb.MatchStartReq{}
	if err := proto.Unmarshal(env.GetPayload(), req); err != nil {
		return ErrInvalidPayload
	}
	if req.GetMode() != 1 {
		return g.sendError(s, 422, "unsupported mode")
	}

	g.mu.Lock()
	defer g.mu.Unlock()

	if g.playersBySession[s] == nil || g.playersBySession[s].UserID == 0 {
		return g.sendError(s, 401, "not logged in")
	}
	if _, ok := g.roomBySession[s]; ok {
		return g.sendError(s, 409, "already in room")
	}

	if g.waiting == nil {
		g.waiting = s
		return nil
	}
	if g.waiting == s {
		return nil
	}

	a := g.waiting
	b := s
	g.waiting = nil

	roomID := fmt.Sprintf("room-%d", atomic.AddUint64(&g.nextRoomID, 1))
	rm := g.newRoom(roomID, a, b)

	g.rooms[roomID] = rm
	g.roomBySession[a] = rm
	g.roomBySession[b] = rm
	g.roomByUserID[rm.PlayerUsers[0]] = rm
	g.roomByUserID[rm.PlayerUsers[1]] = rm

	foundA := &pb.MatchFound{RoomId: roomID, SelfUserId: rm.PlayerUsers[0], OpponentUserId: rm.PlayerUsers[1], SelfSide: 1}
	foundB := &pb.MatchFound{RoomId: roomID, SelfUserId: rm.PlayerUsers[1], OpponentUserId: rm.PlayerUsers[0], SelfSide: 2}
	_ = g.sendEnvelope(a, pb.Cmd_S2C_MATCH_FOUND, foundA, roomID, rm.Version, 0)
	_ = g.sendEnvelope(b, pb.Cmd_S2C_MATCH_FOUND, foundB, roomID, rm.Version, 0)

	start := &pb.GameStart{
		RoomId:            roomID,
		Pieces:            rm.piecesAsProto(),
		CurrentTurnUserId: rm.PlayerUsers[0],
		TurnDeadlineMs:    rm.DeadlineMs,
		RoomVersion:       rm.Version,
	}
	_ = g.sendEnvelope(a, pb.Cmd_S2C_GAME_START, start, roomID, rm.Version, 0)
	_ = g.sendEnvelope(b, pb.Cmd_S2C_GAME_START, start, roomID, rm.Version, 0)
	return nil
}

func (g *GameService) handleMatchCancel(s Session) error {
	g.mu.Lock()
	defer g.mu.Unlock()
	if g.waiting == s {
		g.waiting = nil
	}
	return nil
}

func (g *GameService) handleMove(s Session, env *pb.Envelope) error {
	req := &pb.MoveReq{}
	if err := proto.Unmarshal(env.GetPayload(), req); err != nil {
		return ErrInvalidPayload
	}

	g.mu.Lock()
	defer g.mu.Unlock()

	rm := g.roomBySession[s]
	if rm == nil {
		return g.sendError(s, 404, "room not found")
	}
	if rm.Finished {
		return g.sendError(s, 410, "room finished")
	}
	if time.Now().UnixMilli() > rm.DeadlineMs {
		winner := rm.PlayerUsers[(rm.TurnIndex+1)%2]
		g.endRoomWithWinner(rm, winner, 2)
		return nil
	}

	actor := g.playersBySession[s]
	if actor == nil {
		return g.sendError(s, 401, "not logged in")
	}
	if rm.PlayerUsers[rm.TurnIndex] != actor.UserID {
		return g.sendError(s, 409, "not your turn")
	}

	if !isInBounds(req.GetFrom()) || !isInBounds(req.GetTo()) {
		return g.sendRejectedMove(s, rm, actor.UserID, req.GetFrom(), req.GetTo(), req.GetPath(), "out of board", env.GetSeq())
	}

	fromKey := posKey(req.GetFrom().GetX(), req.GetFrom().GetY())
	toKey := posKey(req.GetTo().GetX(), req.GetTo().GetY())

	owner, ok := rm.Pieces[fromKey]
	if !ok || owner != actor.UserID {
		return g.sendRejectedMove(s, rm, actor.UserID, req.GetFrom(), req.GetTo(), req.GetPath(), "invalid source piece", env.GetSeq())
	}
	if _, occupied := rm.Pieces[toKey]; occupied {
		return g.sendRejectedMove(s, rm, actor.UserID, req.GetFrom(), req.GetTo(), req.GetPath(), "target occupied", env.GetSeq())
	}

	ok, normalizedPath, reason := validateMovePath(rm, req.GetFrom(), req.GetTo(), req.GetPath())
	if !ok {
		return g.sendRejectedMove(s, rm, actor.UserID, req.GetFrom(), req.GetTo(), req.GetPath(), reason, env.GetSeq())
	}

	delete(rm.Pieces, fromKey)
	rm.Pieces[toKey] = actor.UserID
	rm.Version++

	result := &pb.MoveResult{
		ActorUserId:  actor.UserID,
		From:         req.GetFrom(),
		To:           req.GetTo(),
		Accepted:     true,
		RoomVersion:  rm.Version,
		RejectReason: "",
		Path:         normalizedPath,
	}
	g.broadcastRoom(rm, pb.Cmd_S2C_MOVE_RESULT, result, env.GetSeq())

	if rm.isWin(actor.UserID) {
		g.endRoomWithWinner(rm, actor.UserID, 1)
		return nil
	}

	rm.TurnIndex = (rm.TurnIndex + 1) % 2
	rm.DeadlineMs = time.Now().Add(turnDuration).UnixMilli()
	turn := &pb.TurnChange{
		CurrentTurnUserId: rm.PlayerUsers[rm.TurnIndex],
		TurnDeadlineMs:    rm.DeadlineMs,
		RoomVersion:       rm.Version,
	}
	g.broadcastRoom(rm, pb.Cmd_S2C_TURN_CHANGE, turn, 0)
	return nil
}

func (g *GameService) handlePing(s Session, env *pb.Envelope) error {
	req := &pb.Ping{}
	if err := proto.Unmarshal(env.GetPayload(), req); err != nil {
		return ErrInvalidPayload
	}
	pong := &pb.Pong{ClientTs: req.GetClientTs(), ServerTs: time.Now().UnixMilli()}
	return g.sendEnvelope(s, pb.Cmd_S2C_PONG, pong, env.GetRoomId(), env.GetRoomVersion(), env.GetSeq())
}

func (g *GameService) handleReconnect(s Session, env *pb.Envelope) error {
	req := &pb.ReconnectReq{}
	if err := proto.Unmarshal(env.GetPayload(), req); err != nil {
		return ErrInvalidPayload
	}

	g.mu.Lock()
	defer g.mu.Unlock()

	playerState := g.playerByToken[req.GetToken()]
	if playerState == nil {
		ack := &pb.ReconnectAck{Ok: false, Reason: "invalid token"}
		return g.sendEnvelope(s, pb.Cmd_S2C_RECONNECT_ACK, ack, "", 0, env.GetSeq())
	}

	oldSession := g.sessionByUserID[playerState.UserID]
	g.playersBySession[s] = playerState
	if oldSession != nil && oldSession != s {
		delete(g.playersBySession, oldSession)
	}
	g.sessionByUserID[playerState.UserID] = s

	if rm := g.roomByUserID[playerState.UserID]; rm != nil && !rm.Finished {
		for i := 0; i < 2; i++ {
			if rm.PlayerUsers[i] == playerState.UserID {
				rm.Players[i] = s
				rm.OfflineSinceMs[i] = 0
			}
		}
		if oldSession != nil {
			delete(g.roomBySession, oldSession)
		}
		g.roomBySession[s] = rm

		ack := &pb.ReconnectAck{Ok: true, Reason: "ok"}
		if err := g.sendEnvelope(s, pb.Cmd_S2C_RECONNECT_ACK, ack, rm.ID, rm.Version, env.GetSeq()); err != nil {
			return err
		}

		snapshot := &pb.Snapshot{
			RoomId:            rm.ID,
			Pieces:            rm.piecesAsProto(),
			CurrentTurnUserId: rm.PlayerUsers[rm.TurnIndex],
			TurnDeadlineMs:    rm.DeadlineMs,
			RoomVersion:       rm.Version,
		}
		return g.sendEnvelope(s, pb.Cmd_S2C_SNAPSHOT, snapshot, rm.ID, rm.Version, 0)
	}

	ack := &pb.ReconnectAck{Ok: true, Reason: "ok"}
	return g.sendEnvelope(s, pb.Cmd_S2C_RECONNECT_ACK, ack, "", 0, env.GetSeq())
}

func (g *GameService) sendRejectedMove(s Session, rm *room, userID uint64, from, to *pb.Pos, path []*pb.Pos, reason string, ack uint64) error {
	result := &pb.MoveResult{
		ActorUserId:  userID,
		From:         from,
		To:           to,
		Accepted:     false,
		RejectReason: reason,
		RoomVersion:  rm.Version,
		Path:         path,
	}
	return g.sendEnvelope(s, pb.Cmd_S2C_MOVE_RESULT, result, rm.ID, rm.Version, ack)
}

func (g *GameService) sendError(s Session, code uint32, msg string) error {
	errMsg := &pb.ErrorMsg{Code: code, Message: msg}
	return g.sendEnvelope(s, pb.Cmd_S2C_ERROR, errMsg, "", 0, 0)
}

func (g *GameService) sendEnvelope(s Session, cmd pb.Cmd, payload proto.Message, roomID string, roomVersion uint32, ack uint64) error {
	body, err := proto.Marshal(payload)
	if err != nil {
		return err
	}
	resp := &pb.Envelope{
		Cmd:         uint32(cmd),
		Ack:         ack,
		Ts:          time.Now().UnixMilli(),
		Payload:     body,
		RoomId:      roomID,
		RoomVersion: roomVersion,
	}
	raw, err := proto.Marshal(resp)
	if err != nil {
		return err
	}
	return s.SendBinary(raw)
}

func (g *GameService) broadcastRoom(rm *room, cmd pb.Cmd, payload proto.Message, ack uint64) {
	for i := 0; i < 2; i++ {
		if rm.Players[i] != nil {
			_ = g.sendEnvelope(rm.Players[i], cmd, payload, rm.ID, rm.Version, ack)
		}
	}
}

func (g *GameService) newRoom(roomID string, a, b Session) *room {
	rm := &room{
		ID:         roomID,
		Version:    1,
		Players:    [2]Session{a, b},
		TurnIndex:  0,
		DeadlineMs: time.Now().Add(turnDuration).UnixMilli(),
		Pieces:     make(map[string]uint64),
	}
	if pa := g.playersBySession[a]; pa != nil {
		rm.PlayerUsers[0] = pa.UserID
	}
	if pb2 := g.playersBySession[b]; pb2 != nil {
		rm.PlayerUsers[1] = pb2.UserID
	}

	// 2-player mode uses opposite camps on a 6-corner star board.
	campA := starCamps[3]
	campB := starCamps[0]
	rm.TargetCamp[0] = toCampSet(campB)
	rm.TargetCamp[1] = toCampSet(campA)

	for _, p := range campA {
		rm.Pieces[posKey(p.X, p.Y)] = rm.PlayerUsers[0]
	}
	for _, p := range campB {
		rm.Pieces[posKey(p.X, p.Y)] = rm.PlayerUsers[1]
	}
	return rm
}

func (g *GameService) timeoutLoop() {
	ticker := time.NewTicker(1 * time.Second)
	defer ticker.Stop()
	for range ticker.C {
		g.mu.Lock()
		g.processRoomTimeouts(time.Now().UnixMilli())
		g.mu.Unlock()
	}
}

func (g *GameService) processRoomTimeouts(nowMs int64) {
	for _, rm := range g.rooms {
		if rm.Finished {
			continue
		}

		disconnectLose := false
		for i := 0; i < 2; i++ {
			offlineSince := rm.OfflineSinceMs[i]
			if offlineSince == 0 {
				continue
			}
			if nowMs-offlineSince < disconnectGrace.Milliseconds() {
				continue
			}
			winner := rm.PlayerUsers[(i+1)%2]
			g.endRoomWithWinner(rm, winner, 4)
			disconnectLose = true
			break
		}
		if disconnectLose {
			continue
		}

		if rm.DeadlineMs == 0 || nowMs <= rm.DeadlineMs {
			continue
		}
		winner := rm.PlayerUsers[(rm.TurnIndex+1)%2]
		g.endRoomWithWinner(rm, winner, 2)
	}
}

func (g *GameService) endRoomWithWinner(rm *room, winner uint64, reason uint32) {
	if rm.Finished {
		return
	}
	rm.Finished = true
	over := &pb.GameOver{RoomId: rm.ID, WinnerUserId: winner, Reason: reason}
	g.broadcastRoom(rm, pb.Cmd_S2C_GAME_OVER, over, 0)
	for i := 0; i < 2; i++ {
		delete(g.roomByUserID, rm.PlayerUsers[i])
		if rm.Players[i] != nil {
			delete(g.roomBySession, rm.Players[i])
		}
	}
	delete(g.rooms, rm.ID)
}

func (rm *room) piecesAsProto() []*pb.Piece {
	items := make([]boardPos, 0, len(rm.Pieces))
	for key := range rm.Pieces {
		x, y := parseKey(key)
		items = append(items, boardPos{X: x, Y: y})
	}
	sort.Slice(items, func(i, j int) bool {
		if items[i].Y != items[j].Y {
			return items[i].Y < items[j].Y
		}
		return items[i].X < items[j].X
	})

	out := make([]*pb.Piece, 0, len(items))
	for _, p := range items {
		out = append(out, &pb.Piece{OwnerId: rm.Pieces[posKey(p.X, p.Y)], Pos: &pb.Pos{X: p.X, Y: p.Y}})
	}
	return out
}

func (rm *room) isWin(userID uint64) bool {
	idx := -1
	if rm.PlayerUsers[0] == userID {
		idx = 0
	}
	if rm.PlayerUsers[1] == userID {
		idx = 1
	}
	if idx < 0 {
		return false
	}
	target := rm.TargetCamp[idx]
	for key, owner := range rm.Pieces {
		if owner != userID {
			continue
		}
		if _, ok := target[key]; !ok {
			return false
		}
	}
	return true
}

func isInBounds(p *pb.Pos) bool {
	if p == nil {
		return false
	}
	_, ok := starBoardCells[posKey(p.GetX(), p.GetY())]
	return ok
}

func isLegalMove(rm *room, from, to *pb.Pos) bool {
	if isNeighborStep(from, to) {
		return true
	}

	if !isJumpStep(from, to) {
		return false
	}
	mx := from.GetX() + (to.GetX()-from.GetX())/2
	my := from.GetY() + (to.GetY()-from.GetY())/2
	_, occupied := rm.Pieces[posKey(mx, my)]
	return occupied
}

func validateMovePath(rm *room, from, to *pb.Pos, path []*pb.Pos) (bool, []*pb.Pos, string) {
	if len(path) == 0 {
		if !isLegalMove(rm, from, to) {
			return false, nil, "illegal move"
		}
		return true, nil, ""
	}

	last := path[len(path)-1]
	if last.GetX() != to.GetX() || last.GetY() != to.GetY() {
		return false, nil, "path tail must equal to"
	}

	occ := copyPieces(rm.Pieces)
	cur := &pb.Pos{X: from.GetX(), Y: from.GetY()}
	for i := 0; i < len(path); i++ {
		next := path[i]
		if !isInBounds(next) {
			return false, nil, "path out of board"
		}
		if !isSingleJump(cur, next) {
			return false, nil, "path must be jump-only"
		}

		nextKey := posKey(next.GetX(), next.GetY())
		if _, occupied := occ[nextKey]; occupied {
			return false, nil, "path target occupied"
		}

		mx := cur.GetX() + (next.GetX()-cur.GetX())/2
		my := cur.GetY() + (next.GetY()-cur.GetY())/2
		if _, occupied := occ[posKey(mx, my)]; !occupied {
			return false, nil, "path jump needs middle piece"
		}

		curKey := posKey(cur.GetX(), cur.GetY())
		owner := occ[curKey]
		delete(occ, curKey)
		occ[nextKey] = owner
		cur = &pb.Pos{X: next.GetX(), Y: next.GetY()}
	}
	return true, clonePath(path), ""
}

func isSingleJump(from, to *pb.Pos) bool {
	return isJumpStep(from, to)
}

func copyPieces(src map[string]uint64) map[string]uint64 {
	out := make(map[string]uint64, len(src))
	for k, v := range src {
		out[k] = v
	}
	return out
}

func clonePath(src []*pb.Pos) []*pb.Pos {
	out := make([]*pb.Pos, 0, len(src))
	for _, p := range src {
		out = append(out, &pb.Pos{X: p.GetX(), Y: p.GetY()})
	}
	return out
}

func isNeighborStep(from, to *pb.Pos) bool {
	dx := to.GetX() - from.GetX()
	dy := to.GetY() - from.GetY()
	for _, d := range hexDirs {
		if dx == d.X && dy == d.Y {
			return true
		}
	}
	return false
}

func isJumpStep(from, to *pb.Pos) bool {
	dx := to.GetX() - from.GetX()
	dy := to.GetY() - from.GetY()
	for _, d := range hexDirs {
		if dx == d.X*2 && dy == d.Y*2 {
			return true
		}
	}
	return false
}

func isCenterHexCell(q, r int32) bool {
	s := -q - r
	return abs32(q) <= centerRadius && abs32(r) <= centerRadius && abs32(s) <= centerRadius
}

func buildStarBoard() (map[string]struct{}, [6][]boardPos) {
	out := make(map[string]struct{}, 121)
	camps := [6][]boardPos{}

	// Center hex.
	for q := -centerRadius; q <= centerRadius; q++ {
		for r := -centerRadius; r <= centerRadius; r++ {
			if isCenterHexCell(q, r) {
				out[posKey(q, r)] = struct{}{}
			}
		}
	}

	// Build one reference camp at +q corner, then rotate 60 degrees.
	base := make([]boardPos, 0, 10)
	for a := int32(0); a < campDepth; a++ {
		for b := int32(0); b < campDepth-a; b++ {
			q := centerRadius + 1 + b
			r := -centerRadius + a + b
			base = append(base, boardPos{X: q, Y: r})
		}
	}

	for i := 0; i < 6; i++ {
		camp := make([]boardPos, 0, len(base))
		for _, p := range base {
			q, r := p.X, p.Y
			for k := 0; k < i; k++ {
				q, r = rotate60CW(q, r)
			}
			camp = append(camp, boardPos{X: q, Y: r})
			out[posKey(q, r)] = struct{}{}
		}
		camps[i] = camp
	}
	return out, camps
}

func rotate60CW(q, r int32) (int32, int32) {
	return -r, q + r
}

func toCampSet(cells []boardPos) map[string]struct{} {
	out := make(map[string]struct{}, len(cells))
	for _, c := range cells {
		out[posKey(c.X, c.Y)] = struct{}{}
	}
	return out
}

func posKey(x, y int32) string {
	return fmt.Sprintf("%d:%d", x, y)
}

func parseKey(key string) (int32, int32) {
	var x, y int32
	_, _ = fmt.Sscanf(key, "%d:%d", &x, &y)
	return x, y
}

func abs32(v int32) int32 {
	if v < 0 {
		return -v
	}
	return v
}
