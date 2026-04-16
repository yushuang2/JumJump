using System;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;

namespace JumJump.Network
{
    public sealed class WsClient : IDisposable
    {
        private ClientWebSocket _socket;
        private CancellationTokenSource _cts;

        public event Action<byte[]> OnBinaryMessage;
        public event Action OnClosed;

        public bool IsConnected => _socket != null && _socket.State == WebSocketState.Open;

        public async Task ConnectAsync(string url)
        {
            await CloseAsync();

            _socket = new ClientWebSocket();
            _cts = new CancellationTokenSource();
            await _socket.ConnectAsync(new Uri(url), _cts.Token);
            _ = Task.Run(ReceiveLoopAsync);
        }

        public async Task SendBinaryAsync(byte[] data)
        {
            if (!IsConnected)
            {
                return;
            }

            await _socket.SendAsync(data, WebSocketMessageType.Binary, true, _cts.Token);
        }

        public async Task CloseAsync()
        {
            if (_socket == null)
            {
                return;
            }

            try
            {
                _cts?.Cancel();
                if (_socket.State == WebSocketState.Open)
                {
                    await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "close", CancellationToken.None);
                }
            }
            catch
            {
                // Swallow close exceptions for reconnect flow.
            }
            finally
            {
                _cts?.Dispose();
                _cts = null;
                _socket.Dispose();
                _socket = null;
            }
        }

        private async Task ReceiveLoopAsync()
        {
            var socket = _socket;
            var cts = _cts;
            var buffer = new byte[1024 * 64];
            try
            {
                while (socket != null && socket.State == WebSocketState.Open)
                {
                    var segment = new ArraySegment<byte>(buffer);
                    using var ms = new System.IO.MemoryStream();
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await socket.ReceiveAsync(segment, cts.Token);
                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            OnClosed?.Invoke();
                            return;
                        }

                        ms.Write(segment.Array, segment.Offset, result.Count);
                    } while (!result.EndOfMessage);

                    if (result.MessageType == WebSocketMessageType.Binary)
                    {
                        OnBinaryMessage?.Invoke(ms.ToArray());
                    }
                }
            }
            catch
            {
                OnClosed?.Invoke();
            }
        }

        public void Dispose()
        {
            _ = CloseAsync();
        }
    }
}
