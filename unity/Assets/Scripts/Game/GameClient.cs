using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using Google.Protobuf;
using JumJump.Network;
using JumJump.Proto;

namespace JumJump.Game
{
    public sealed class GameClient : IDisposable
    {
        private readonly WsClient _ws = new WsClient();
        private readonly ConcurrentQueue<Envelope> _inbound = new ConcurrentQueue<Envelope>();
        private ulong _seq = 0;
        private string _roomId = string.Empty;
        private uint _roomVersion = 0;

        public event Action<Envelope> OnEnvelope;
        public event Action OnDisconnected;

        public bool IsConnected => _ws.IsConnected;
        public uint LastRoomVersion => _roomVersion;
        public string LastRoomId => _roomId;

        public GameClient()
        {
            _ws.OnBinaryMessage += HandleBinary;
            _ws.OnClosed += () => OnDisconnected?.Invoke();
        }

        public Task ConnectAsync(string url)
        {
            return _ws.ConnectAsync(url);
        }

        public Task LoginAsync(string wxCode, string clientVersion)
        {
            var req = new LoginReq { WxCode = wxCode, ClientVersion = clientVersion };
            return SendAsync(Cmd.C2SLogin, req);
        }

        public Task StartMatchAsync(uint mode = 1)
        {
            var req = new MatchStartReq { Mode = mode };
            return SendAsync(Cmd.C2SMatchStart, req);
        }

        public Task CancelMatchAsync()
        {
            return SendAsync(Cmd.C2SMatchCancel, new MatchCancelReq());
        }

        public Task MoveAsync(int fromX, int fromY, int toX, int toY)
        {
            return MoveAsync(fromX, fromY, toX, toY, null);
        }

        public Task MoveAsync(int fromX, int fromY, int toX, int toY, IList<Pos> path)
        {
            var req = new MoveReq
            {
                From = new Pos { X = fromX, Y = fromY },
                To = new Pos { X = toX, Y = toY },
            };
            if (path != null && path.Count > 0)
            {
                req.Path.AddRange(path);
            }
            return SendAsync(Cmd.C2SMove, req);
        }

        public Task PingAsync()
        {
            var req = new Ping { ClientTs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
            return SendAsync(Cmd.C2SPing, req);
        }

        public Task ReconnectAsync(string token, string roomId, uint lastRoomVersion)
        {
            var req = new ReconnectReq
            {
                Token = token,
                RoomId = roomId,
                LastRoomVersion = lastRoomVersion,
            };
            return SendAsync(Cmd.C2SReconnect, req);
        }

        // Pump incoming packets on the Unity main thread.
        public void PumpIncoming()
        {
            while (_inbound.TryDequeue(out var env))
            {
                _roomId = env.RoomId;
                _roomVersion = env.RoomVersion;
                OnEnvelope?.Invoke(env);
            }
        }

        private async Task SendAsync<T>(Cmd cmd, T msg) where T : IMessage
        {
            _seq++;
            var bytes = ProtocolCodec.Pack(cmd, _seq, _roomId, _roomVersion, msg);
            await _ws.SendBinaryAsync(bytes);
        }

        private void HandleBinary(byte[] data)
        {
            var env = ProtocolCodec.Unpack(data);
            _inbound.Enqueue(env);
        }

        public void Dispose()
        {
            _ws.Dispose();
        }
    }
}

