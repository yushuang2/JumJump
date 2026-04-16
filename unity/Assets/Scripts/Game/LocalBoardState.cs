using System.Collections.Generic;
using JumJump.Proto;
using UnityEngine;

namespace JumJump.Game
{
    public sealed class LocalBoardState
    {
        private readonly Dictionary<Vector2Int, ulong> _pieces = new Dictionary<Vector2Int, ulong>();

        public IReadOnlyDictionary<Vector2Int, ulong> Pieces => _pieces;
        public ulong CurrentTurnUserId { get; private set; }

        public void ApplyGameStart(GameStart msg)
        {
            _pieces.Clear();
            for (var i = 0; i < msg.Pieces.Count; i++)
            {
                var p = msg.Pieces[i];
                _pieces[new Vector2Int(p.Pos.X, p.Pos.Y)] = p.OwnerId;
            }
            CurrentTurnUserId = msg.CurrentTurnUserId;
        }

        public void ApplySnapshot(Snapshot msg)
        {
            _pieces.Clear();
            for (var i = 0; i < msg.Pieces.Count; i++)
            {
                var p = msg.Pieces[i];
                _pieces[new Vector2Int(p.Pos.X, p.Pos.Y)] = p.OwnerId;
            }
            CurrentTurnUserId = msg.CurrentTurnUserId;
        }

        public void ApplyMoveResult(MoveResult msg)
        {
            if (!msg.Accepted)
            {
                return;
            }

            var from = new Vector2Int(msg.From.X, msg.From.Y);
            var to = new Vector2Int(msg.To.X, msg.To.Y);
            if (_pieces.Remove(from))
            {
                _pieces[to] = msg.ActorUserId;
            }
        }

        public void ApplyTurnChange(TurnChange msg)
        {
            CurrentTurnUserId = msg.CurrentTurnUserId;
        }
    }
}
