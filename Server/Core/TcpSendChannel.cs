using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Server.Core
{
    /// <summary>
    /// TCP ISendChannel — writes length-prefixed frames to a NetworkStream.
    /// Frame format: [4-byte uint32 LE payload length][payload bytes]
    /// Thread-safe and non-blocking via a packet send queue.
    /// Drops packets if client is too slow to maintain low latency.
    /// </summary>
    public sealed class TcpSendChannel : ISendChannel, IDisposable
    {
        private readonly NetworkStream _stream;
        private readonly ConcurrentQueue<byte[]> _sendQueue = new();
        private readonly SemaphoreSlim _queueSignal = new(0);
        private readonly CancellationTokenSource _cts = new();
        private volatile bool _alive = true;
        private const int MaxQueueSize = 50;

        public string Protocol => "TCP";
        public bool IsAlive => _alive && _stream.Socket.Connected;

        public TcpSendChannel(NetworkStream stream)
        {
            _stream = stream;
            Task.Run(SendLoopAsync);
        }

        public void Send(byte[] data, int offset, int length)
        {
            if (!_alive) return;

            byte[] copy = new byte[length];
            Buffer.BlockCopy(data, offset, copy, 0, length);

            if (_sendQueue.Count >= MaxQueueSize)
            {
                // Drop oldest packet
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
            byte[] lenBuf = new byte[4];
            try
            {
                while (!ct.IsCancellationRequested && IsAlive)
                {
                    await _queueSignal.WaitAsync(ct);
                    if (_sendQueue.TryDequeue(out byte[]? data))
                    {
                        BinaryPrimitives.WriteUInt32LittleEndian(lenBuf, (uint)data.Length);
                        await _stream.WriteAsync(lenBuf, 0, 4, ct);
                        await _stream.WriteAsync(data, 0, data.Length, ct);
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
