using System;
using System.Net.WebSockets;
using System.Threading;

namespace Server.Core
{
    /// <summary>
    /// WebSocket ISendChannel — sends binary WebSocket messages.
    /// Each WebSocket message IS one voice packet (no length prefix needed).
    /// Thread-safe via SemaphoreSlim — WebSocket.SendAsync is not re-entrant.
    /// Uses synchronous blocking to satisfy the ISendChannel interface while
    /// respecting WebSocket's async-only API.
    /// </summary>
    public sealed class WebSocketSendChannel : ISendChannel
    {
        private readonly WebSocket _ws;
        private readonly SemaphoreSlim _sendGate = new(1, 1);
        private volatile bool _alive = true;

        public string Protocol => "WebSocket";
        public bool IsAlive => _alive && _ws.State == WebSocketState.Open;

        public WebSocketSendChannel(WebSocket ws)
        {
            _ws = ws;
        }

        public void Send(byte[] data, int offset, int length)
        {
            if (!IsAlive) return;

            _sendGate.Wait();
            try
            {
                var segment = new ArraySegment<byte>(data, offset, length);
                // Block the calling thread; Broadcast workers are already threadpool threads
                _ws.SendAsync(segment, WebSocketMessageType.Binary, true, CancellationToken.None)
                   .GetAwaiter().GetResult();
            }
            catch (WebSocketException)
            {
                _alive = false;
            }
            catch (OperationCanceledException)
            {
                _alive = false;
            }
            finally
            {
                _sendGate.Release();
            }
        }
    }
}
