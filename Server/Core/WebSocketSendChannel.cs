using System;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;

namespace Server.Core
{
    /// <summary>
    /// WebSocket ISendChannel — sends binary WebSocket messages.
    /// Each WebSocket message IS one voice packet (no length prefix needed).
    /// Thread-safe and non-blocking via a packet send queue.
    /// Drops packets if client is too slow to maintain low latency.
    /// </summary>
    public sealed class WebSocketSendChannel : ISendChannel, IDisposable
    {
        private readonly WebSocket _ws;
        private readonly ConcurrentQueue<byte[]> _sendQueue = new();
        private readonly SemaphoreSlim _queueSignal = new(0);
        private readonly CancellationTokenSource _cts = new();
        private volatile bool _alive = true;
        private const int MaxQueueSize = 50;

        public string Protocol => "WebSocket";
        public bool IsAlive => _alive && _ws.State == WebSocketState.Open;

        public WebSocketSendChannel(WebSocket ws)
        {
            _ws = ws;
            Task.Run(SendLoopAsync);
        }

        public void Send(byte[] data, int offset, int length)
        {
            if (!IsAlive) return;

            byte[] copy = new byte[length];
            Buffer.BlockCopy(data, offset, copy, 0, length);

            if (_sendQueue.Count >= MaxQueueSize)
            {
                // Drop the oldest packet to preserve low latency
                _sendQueue.TryDequeue(out _);
            }

            _sendQueue.Enqueue(copy);
            try
            {
                _queueSignal.Release();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private async Task SendLoopAsync()
        {
            var ct = _cts.Token;
            try
            {
                while (!ct.IsCancellationRequested && IsAlive)
                {
                    await _queueSignal.WaitAsync(ct);
                    if (_sendQueue.TryDequeue(out byte[]? data))
                    {
                        var segment = new ArraySegment<byte>(data);
                        await _ws.SendAsync(segment, WebSocketMessageType.Binary, true, ct);
                    }
                }
            }
            catch (Exception)
            {
                _alive = false;
            }
        }

        public void Dispose()
        {
            _alive = false;
            _cts.Cancel();
            try { _queueSignal.Dispose(); } catch { }
            try { _cts.Dispose(); } catch { }
        }
    }
}
