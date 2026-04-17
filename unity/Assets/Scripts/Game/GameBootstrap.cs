using System;
using System.Collections.Generic;
using JumJump.Network;
using JumJump.Proto;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace JumJump.Game
{
    public class GameBootstrap : MonoBehaviour
    {
        [SerializeField] private string serverUrl = "ws://127.0.0.1:8080/ws";
        [SerializeField] private bool showCoordinates;

        private const int CenterRadius = 4;
        private const int CampDepth = 4;
        private const int BoardExtent = 8;
        private const float HexSize = 36f;
        private const float CellSize = 28f;
        private static readonly Vector2Int[] GridLineDirs =
        {
            new Vector2Int(1, 0),
            new Vector2Int(0, 1),
            new Vector2Int(1, -1),
        };
        private static readonly Color[] CampColors =
        {
            new Color(0.95f, 0.60f, 0.60f, 0.35f),
            new Color(0.95f, 0.80f, 0.55f, 0.35f),
            new Color(0.88f, 0.92f, 0.56f, 0.35f),
            new Color(0.60f, 0.88f, 0.63f, 0.35f),
            new Color(0.62f, 0.78f, 0.95f, 0.35f),
            new Color(0.82f, 0.68f, 0.95f, 0.35f),
        };

        private readonly LocalBoardState _board = new LocalBoardState();
        private readonly List<Vector2Int> _plannedPath = new List<Vector2Int>();
        private readonly HashSet<Vector2Int> _starCells = BuildStarCells();
        private readonly Dictionary<Vector2Int, int> _campByCell = BuildCampByCell();

        private GameClient _client;
        private string _status = "init";
        private ulong _selfUserId;
        private string _token = string.Empty;
        private string _roomId = string.Empty;
        private Vector2Int? _selectedFrom;
        private float _nextPingAt;

        [Header("View")]
        [SerializeField] private GameBootstrapView view;
        [SerializeField] private GameBootstrapView viewPrefab;

        private bool _reconnectPending;
        private bool _reconnectInFlight;
        private float _nextReconnectAt;
        private bool _uiBound;
        private readonly Dictionary<Vector2Int, CellView> _cellViews = new Dictionary<Vector2Int, CellView>();

        private sealed class CellView
        {
            public RawImage Fill;
            public Text Label;
            public GameObject CoordRoot;
        }

        private async void Start()
        {
            EnsureEventSystem();
            EnsureView();
            BindUi();

            _client = new GameClient();
            _client.OnEnvelope += HandleEnvelope;
            _client.OnDisconnected += HandleDisconnected;

            await InitialConnectAsync();
        }

        private void Update()
        {
            _client?.PumpIncoming();

            if (_client != null && _selfUserId != 0 && _client.IsConnected && Time.realtimeSinceStartup >= _nextPingAt)
            {
                _nextPingAt = Time.realtimeSinceStartup + 5f;
                _ = _client.PingAsync();
            }

            if (_reconnectPending && !_reconnectInFlight && Time.realtimeSinceStartup >= _nextReconnectAt)
            {
                _ = TryReconnectAsync();
            }

            RefreshUi();
        }

        private async System.Threading.Tasks.Task InitialConnectAsync()
        {
            _status = "connecting";
            try
            {
                await _client.ConnectAsync(serverUrl);
                _status = "connected, login";
                await _client.LoginAsync("dev_wx_code", Application.version);
            }
            catch (Exception ex)
            {
                _status = $"connect failed: {ex.Message}";
                ScheduleReconnect();
            }
        }

        private async System.Threading.Tasks.Task TryReconnectAsync()
        {
            _reconnectInFlight = true;
            try
            {
                _status = "reconnecting...";
                await _client.ConnectAsync(serverUrl);

                if (!string.IsNullOrEmpty(_token))
                {
                    await _client.ReconnectAsync(_token, _roomId, _client.LastRoomVersion);
                    _status = "reconnect request sent";
                }
                else
                {
                    await _client.LoginAsync("dev_wx_code", Application.version);
                    _status = "reconnect login sent";
                }

                _reconnectPending = false;
            }
            catch (Exception ex)
            {
                _status = $"reconnect failed: {ex.Message}";
                ScheduleReconnect();
            }
            finally
            {
                _reconnectInFlight = false;
            }
        }

        private void HandleDisconnected()
        {
            _status = "connection lost";
            ScheduleReconnect();
        }

        private void ScheduleReconnect()
        {
            _reconnectPending = true;
            _nextReconnectAt = Time.realtimeSinceStartup + 2f;
        }

        private void HandleEnvelope(Envelope env)
        {
            _roomId = env.RoomId;
            var cmd = (Cmd)env.Cmd;

            switch (cmd)
            {
                case Cmd.S2CLoginAck:
                {
                    var ack = ProtocolCodec.ParsePayload(env, LoginAck.Parser);
                    _selfUserId = ack.UserId;
                    _token = ack.Token;
                    _status = $"login ok uid={_selfUserId}, matching";
                    _ = _client.StartMatchAsync();
                    break;
                }
                case Cmd.S2CMatchFound:
                {
                    var found = ProtocolCodec.ParsePayload(env, MatchFound.Parser);
                    _status = $"matched room={found.RoomId} vs={found.OpponentUserId}";
                    break;
                }
                case Cmd.S2CGameStart:
                {
                    var start = ProtocolCodec.ParsePayload(env, GameStart.Parser);
                    _board.ApplyGameStart(start);
                    ClearMoveSelection();
                    _status = "game start";
                    break;
                }
                case Cmd.S2CMoveResult:
                {
                    var move = ProtocolCodec.ParsePayload(env, MoveResult.Parser);
                    _board.ApplyMoveResult(move);
                    ClearMoveSelection();
                    _status = move.Accepted ? "move accepted" : $"move rejected: {move.RejectReason}";
                    break;
                }
                case Cmd.S2CTurnChange:
                {
                    var turn = ProtocolCodec.ParsePayload(env, TurnChange.Parser);
                    _board.ApplyTurnChange(turn);
                    break;
                }
                case Cmd.S2CSnapshot:
                {
                    var snap = ProtocolCodec.ParsePayload(env, Snapshot.Parser);
                    _board.ApplySnapshot(snap);
                    ClearMoveSelection();
                    _status = "snapshot synced";
                    break;
                }
                case Cmd.S2CReconnectAck:
                {
                    var ack = ProtocolCodec.ParsePayload(env, ReconnectAck.Parser);
                    if (ack.Ok)
                    {
                        _status = "reconnect ok";
                        _reconnectPending = false;
                    }
                    else
                    {
                        _status = $"reconnect rejected: {ack.Reason}, relogin";
                        _ = _client.LoginAsync("dev_wx_code", Application.version);
                    }
                    break;
                }
                case Cmd.S2CPong:
                {
                    break;
                }
                case Cmd.S2CGameOver:
                {
                    var over = ProtocolCodec.ParsePayload(env, GameOver.Parser);
                    _status = $"game over winner={over.WinnerUserId}";
                    break;
                }
                case Cmd.S2CError:
                {
                    var err = ProtocolCodec.ParsePayload(env, ErrorMsg.Parser);
                    _status = $"error {err.Code}: {err.Message}";
                    break;
                }
            }

            Debug.Log($"recv cmd={cmd} room={env.RoomId} v={env.RoomVersion} status={_status}");
        }

        private void EnsureView()
        {
            if (view == null)
            {
                view = GetComponentInChildren<GameBootstrapView>(true);
            }

            if (view == null && viewPrefab != null)
            {
                view = Instantiate(viewPrefab, transform);
                view.name = viewPrefab.name;
            }

            if (view == null || !view.IsValid)
            {
                throw new InvalidOperationException("GameBootstrapView is missing or incomplete.");
            }

            if (_cellViews.Count == 0)
            {
                DrawGridLines(view.BoardGridRoot);
                BuildBoardCells(view.BoardCellRoot);
            }
        }

        private static void EnsureEventSystem()
        {
            if (FindObjectOfType<EventSystem>() != null)
            {
                return;
            }

            new GameObject("EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));
        }

        private void BindUi()
        {
            if (_uiBound)
            {
                return;
            }

            view.SubmitJumpButton.onClick.AddListener(HandleSubmitJumpClicked);
            view.UndoJumpButton.onClick.AddListener(HandleUndoJumpClicked);
            view.ClearSelectionButton.onClick.AddListener(HandleClearSelectionClicked);
            view.StartMatchButton.onClick.AddListener(HandleStartMatchClicked);
            view.CancelMatchButton.onClick.AddListener(HandleCancelMatchClicked);
            view.CoordToggleButton.onClick.AddListener(HandleCoordToggleClicked);
            _uiBound = true;
        }

        private void UnbindUi()
        {
            if (!_uiBound || view == null)
            {
                return;
            }

            view.SubmitJumpButton.onClick.RemoveListener(HandleSubmitJumpClicked);
            view.UndoJumpButton.onClick.RemoveListener(HandleUndoJumpClicked);
            view.ClearSelectionButton.onClick.RemoveListener(HandleClearSelectionClicked);
            view.StartMatchButton.onClick.RemoveListener(HandleStartMatchClicked);
            view.CancelMatchButton.onClick.RemoveListener(HandleCancelMatchClicked);
            view.CoordToggleButton.onClick.RemoveListener(HandleCoordToggleClicked);
            _uiBound = false;
        }

        private void HandleSubmitJumpClicked()
        {
            SubmitPlannedPath();
            RefreshUi();
        }

        private void HandleUndoJumpClicked()
        {
            UndoLastJump();
            RefreshUi();
        }

        private void HandleClearSelectionClicked()
        {
            ClearMoveSelection();
            RefreshUi();
        }

        private void HandleStartMatchClicked()
        {
            _ = _client?.StartMatchAsync();
            RefreshUi();
        }

        private void HandleCancelMatchClicked()
        {
            _ = _client?.CancelMatchAsync();
            RefreshUi();
        }

        private void HandleCoordToggleClicked()
        {
            showCoordinates = !showCoordinates;
            RefreshUi();
        }

        private void DrawGridLines(RectTransform parent)
        {
            var center = Vector2.zero;
            for (var r = -BoardExtent; r <= BoardExtent; r++)
            {
                for (var q = -BoardExtent; q <= BoardExtent; q++)
                {
                    var from = new Vector2Int(q, r);
                    if (!_starCells.Contains(from))
                    {
                        continue;
                    }

                    var fromP = AxialToScreen(from, center);
                    for (var i = 0; i < GridLineDirs.Length; i++)
                    {
                        var to = from + GridLineDirs[i];
                        if (!_starCells.Contains(to))
                        {
                            continue;
                        }

                        var toP = AxialToScreen(to, center);
                        CreateLine(parent, fromP, toP, new Color(0f, 0f, 0f, 0.22f), 1.5f);
                    }
                }
            }
        }

        private static void CreateLine(Transform parent, Vector2 from, Vector2 to, Color color, float width)
        {
            var go = new GameObject("GridLine", typeof(RectTransform), typeof(RawImage));
            go.transform.SetParent(parent, false);

            var rect = go.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0f, 0.5f);
            rect.anchoredPosition = from;

            var delta = to - from;
            rect.sizeDelta = new Vector2(delta.magnitude, width);
            rect.localRotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg);

            var image = go.GetComponent<RawImage>();
            image.color = color;
            image.raycastTarget = false;
        }

        private void BuildBoardCells(RectTransform parent)
        {
            var center = Vector2.zero;
            foreach (var pos in _starCells)
            {
                var go = new GameObject($"Cell_{pos.x}_{pos.y}", typeof(RectTransform), typeof(RawImage), typeof(Button));
                go.transform.SetParent(parent, false);

                var rect = go.GetComponent<RectTransform>();
                rect.anchorMin = new Vector2(0.5f, 0.5f);
                rect.anchorMax = new Vector2(0.5f, 0.5f);
                rect.pivot = new Vector2(0.5f, 0.5f);
                rect.anchoredPosition = AxialToScreen(pos, center);
                rect.sizeDelta = new Vector2(CellSize, CellSize);

                var fill = go.GetComponent<RawImage>();
                fill.color = GetBaseCellColor(pos);

                var button = go.GetComponent<Button>();
                button.targetGraphic = fill;
                button.onClick.AddListener(() =>
                {
                    OnCellClicked(pos);
                    RefreshUi();
                });

                var label = CreateRuntimeText("Label", rect, TextAnchor.MiddleCenter, 18, Color.black);
                StretchToParent(label.rectTransform, Vector2.zero, Vector2.zero);

                var coordRoot = new GameObject("CoordRoot", typeof(RectTransform), typeof(RawImage));
                coordRoot.transform.SetParent(rect, false);

                var coordRect = coordRoot.GetComponent<RectTransform>();
                coordRect.anchorMin = new Vector2(0.5f, 1f);
                coordRect.anchorMax = new Vector2(0.5f, 1f);
                coordRect.pivot = new Vector2(0.5f, 1f);
                coordRect.anchoredPosition = new Vector2(0f, 2f);
                coordRect.sizeDelta = new Vector2(CellSize + 16f, 12f);

                var coordBg = coordRoot.GetComponent<RawImage>();
                coordBg.color = new Color(1f, 1f, 1f, 0.90f);
                coordBg.raycastTarget = false;

                var coordText = CreateRuntimeText("CoordText", coordRect, TextAnchor.MiddleCenter, 9, Color.black);
                coordText.text = $"{pos.x},{pos.y}";
                StretchToParent(coordText.rectTransform, Vector2.zero, Vector2.zero);

                _cellViews[pos] = new CellView
                {
                    Fill = fill,
                    Label = label,
                    CoordRoot = coordRoot,
                };
            }
        }

        private static Text CreateRuntimeText(string name, Transform parent, TextAnchor alignment, int fontSize, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Text));
            go.transform.SetParent(parent, false);

            var text = go.GetComponent<Text>();
            text.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            text.fontSize = fontSize;
            text.alignment = alignment;
            text.color = color;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            text.raycastTarget = false;
            return text;
        }

        private static void StretchToParent(RectTransform rect, Vector2 offsetMin, Vector2 offsetMax)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = offsetMin;
            rect.offsetMax = offsetMax;
        }

        private void RefreshUi()
        {
            if (view == null || !view.IsValid)
            {
                return;
            }

            view.StatusText.text = $"Status: {_status}";
            view.RoomText.text = $"Room: {_roomId}";
            view.SelfText.text = $"Self: {_selfUserId}";
            view.TurnText.text = $"Turn: {_board.CurrentTurnUserId}";
            view.SelectedText.text = _selectedFrom.HasValue
                ? $"Selected: {_selectedFrom.Value.x},{_selectedFrom.Value.y}"
                : "Selected: none";
            view.PlannedPathText.text = $"Planned jumps: {FormatPath(_plannedPath)}";
            view.CoordToggleLabel.text = showCoordinates ? "Coords: ON" : "Coords: OFF";

            foreach (var pair in _cellViews)
            {
                UpdateCellView(pair.Key, pair.Value);
            }
        }

        private void UpdateCellView(Vector2Int pos, CellView cell)
        {
            var label = ".";
            var isHint = false;

            if (_board.Pieces.TryGetValue(pos, out var owner))
            {
                label = owner == _selfUserId ? "S" : "O";
            }
            else if (_selectedFrom.HasValue)
            {
                var current = _plannedPath.Count == 0 ? _selectedFrom.Value : _plannedPath[_plannedPath.Count - 1];
                if (_plannedPath.Count == 0 && IsAdjacentStep(current, pos))
                {
                    label = "+";
                    isHint = true;
                }
                else if (CanJumpTo(current, pos))
                {
                    label = "J";
                    isHint = true;
                }
            }

            var color = GetBaseCellColor(pos);
            if (isHint)
            {
                color = new Color(0.70f, 1f, 0.70f, 1f);
            }

            if (_selectedFrom.HasValue && _selectedFrom.Value == pos)
            {
                color = new Color(0.45f, 0.80f, 1f, 1f);
            }
            else if (_plannedPath.Contains(pos))
            {
                color = new Color(1f, 0.88f, 0.55f, 1f);
            }

            cell.Fill.color = color;
            cell.Label.text = label;
            cell.CoordRoot.SetActive(showCoordinates);
        }

        private Color GetBaseCellColor(Vector2Int pos)
        {
            if (_campByCell.TryGetValue(pos, out var campId))
            {
                var campColor = CampColors[campId % CampColors.Length];
                return new Color(campColor.r, campColor.g, campColor.b, 0.95f);
            }

            return new Color(0.92f, 0.92f, 0.92f, 1f);
        }

        private static Vector2 AxialToScreen(Vector2Int pos, Vector2 center)
        {
            // Standard pointy-top axial projection:
            // x = sqrt(3) * size * (q + r/2), y = 3/2 * size * r
            var x = center.x + HexSize * (pos.x + pos.y * 0.5f);
            var y = center.y - HexSize * Mathf.Sqrt(3f) * 0.5f * pos.y;
            return new Vector2(x, y);
        }

        private void OnCellClicked(Vector2Int pos)
        {
            if (!_starCells.Contains(pos))
            {
                return;
            }
            if (_selfUserId == 0)
            {
                return;
            }
            if (_board.CurrentTurnUserId != _selfUserId)
            {
                _status = "not your turn";
                return;
            }

            if (!_selectedFrom.HasValue)
            {
                if (_board.Pieces.TryGetValue(pos, out var owner) && owner == _selfUserId)
                {
                    _selectedFrom = pos;
                    _status = "source selected";
                }
                return;
            }

            if (_board.Pieces.ContainsKey(pos))
            {
                _status = "target occupied";
                return;
            }

            var current = _plannedPath.Count == 0 ? _selectedFrom.Value : _plannedPath[_plannedPath.Count - 1];
            if (IsAdjacentStep(current, pos) && _plannedPath.Count == 0)
            {
                _ = _client.MoveAsync(current.x, current.y, pos.x, pos.y);
                _status = "submitted adjacent move";
                return;
            }

            if (IsJumpStep(current, pos))
            {
                if (CanJumpTo(current, pos))
                {
                    _plannedPath.Add(pos);
                    _status = "jump step added";
                }
                else
                {
                    _status = "jump needs middle piece";
                }
                return;
            }

            _status = "invalid click for move";
        }

        private void SubmitPlannedPath()
        {
            if (!_selectedFrom.HasValue || _plannedPath.Count == 0)
            {
                _status = "no planned jump path";
                return;
            }

            var from = _selectedFrom.Value;
            var to = _plannedPath[_plannedPath.Count - 1];
            var protoPath = new List<Pos>(_plannedPath.Count);
            for (var i = 0; i < _plannedPath.Count; i++)
            {
                var p = _plannedPath[i];
                protoPath.Add(new Pos { X = p.x, Y = p.y });
            }

            _ = _client.MoveAsync(from.x, from.y, to.x, to.y, protoPath);
            _status = "submitted jump path";
        }

        private void UndoLastJump()
        {
            if (_plannedPath.Count == 0)
            {
                return;
            }
            _plannedPath.RemoveAt(_plannedPath.Count - 1);
            _status = "last jump removed";
        }

        private void ClearMoveSelection()
        {
            _selectedFrom = null;
            _plannedPath.Clear();
        }

        private bool CanJumpTo(Vector2Int from, Vector2Int to)
        {
            if (!_starCells.Contains(to) || !IsJumpStep(from, to))
            {
                return false;
            }
            var occupied = BuildVirtualOccupied();
            if (occupied.Contains(to))
            {
                return false;
            }

            var mid = new Vector2Int((from.x + to.x) / 2, (from.y + to.y) / 2);
            return occupied.Contains(mid);
        }

        private HashSet<Vector2Int> BuildVirtualOccupied()
        {
            var occupied = new HashSet<Vector2Int>(_board.Pieces.Keys);
            if (!_selectedFrom.HasValue)
            {
                return occupied;
            }

            var current = _selectedFrom.Value;
            for (var i = 0; i < _plannedPath.Count; i++)
            {
                occupied.Remove(current);
                current = _plannedPath[i];
                occupied.Add(current);
            }
            return occupied;
        }

        private static bool IsAdjacentStep(Vector2Int from, Vector2Int to)
        {
            var dx = to.x - from.x;
            var dy = to.y - from.y;
            return (dx == 1 && dy == 0) ||
                   (dx == 1 && dy == -1) ||
                   (dx == 0 && dy == -1) ||
                   (dx == -1 && dy == 0) ||
                   (dx == -1 && dy == 1) ||
                   (dx == 0 && dy == 1);
        }

        private static bool IsJumpStep(Vector2Int from, Vector2Int to)
        {
            var dx = to.x - from.x;
            var dy = to.y - from.y;
            return (dx == 2 && dy == 0) ||
                   (dx == 2 && dy == -2) ||
                   (dx == 0 && dy == -2) ||
                   (dx == -2 && dy == 0) ||
                   (dx == -2 && dy == 2) ||
                   (dx == 0 && dy == 2);
        }

        private static HashSet<Vector2Int> BuildStarCells()
        {
            var outSet = new HashSet<Vector2Int>();

            for (var q = -CenterRadius; q <= CenterRadius; q++)
            {
                for (var r = -CenterRadius; r <= CenterRadius; r++)
                {
                    if (IsCenterHexCell(q, r))
                    {
                        outSet.Add(new Vector2Int(q, r));
                    }
                }
            }

            var baseCamp = new List<Vector2Int>(10);
            for (var a = 0; a < CampDepth; a++)
            {
                for (var b = 0; b < CampDepth - a; b++)
                {
                    var q = CenterRadius + 1 + b;
                    var r = -CenterRadius + a;
                    baseCamp.Add(new Vector2Int(q, r));
                }
            }

            for (var i = 0; i < 6; i++)
            {
                for (var idx = 0; idx < baseCamp.Count; idx++)
                {
                    var p = baseCamp[idx];
                    for (var k = 0; k < i; k++)
                    {
                        p = Rotate60CW(p);
                    }
                    outSet.Add(p);
                }
            }

            return outSet;
        }

        private static Dictionary<Vector2Int, int> BuildCampByCell()
        {
            var map = new Dictionary<Vector2Int, int>(64);

            var baseCamp = new List<Vector2Int>(10);
            for (var a = 0; a < CampDepth; a++)
            {
                for (var b = 0; b < CampDepth - a; b++)
                {
                    var q = CenterRadius + 1 + b;
                    var r = -CenterRadius + a;
                    baseCamp.Add(new Vector2Int(q, r));
                }
            }

            for (var camp = 0; camp < 6; camp++)
            {
                for (var i = 0; i < baseCamp.Count; i++)
                {
                    var p = baseCamp[i];
                    for (var k = 0; k < camp; k++)
                    {
                        p = Rotate60CW(p);
                    }
                    map[p] = camp;
                }
            }

            return map;
        }

        private static bool IsCenterHexCell(int q, int r)
        {
            var s = -q - r;
            return Mathf.Abs(q) <= CenterRadius && Mathf.Abs(r) <= CenterRadius && Mathf.Abs(s) <= CenterRadius;
        }

        private static Vector2Int Rotate60CW(Vector2Int p)
        {
            return new Vector2Int(-p.y, p.x + p.y);
        }

        private static string FormatPath(List<Vector2Int> path)
        {
            if (path.Count == 0)
            {
                return "none";
            }
            var parts = new string[path.Count];
            for (var i = 0; i < path.Count; i++)
            {
                parts[i] = $"({path[i].x},{path[i].y})";
            }
            return string.Join(" -> ", parts);
        }

        private void OnDestroy()
        {
            UnbindUi();
            _client?.Dispose();
        }
    }
}
