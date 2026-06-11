using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Client.Network
{
    /// <summary>
    /// TCP voice transport — reliable, ordered delivery with length-prefix framing.
    /// Frame format: [4-byte uint32 LE length][N bytes packet data]
    ///
    /// TCP_NODELAY is set to avoid Nagle algorithm adding ~40ms delay.
    /// Use when UDP is blocked by firewalls, or when packet ordering matters.
    /// Connects to server TCP port (default 50006).
    /// </summary>
    public class TcpVoiceTransport : IVoiceTransport
    {
        private const int HeaderSize = 28;

        public string Protocol    => "TCP";
        public bool   IsConnected { get; private set; }
        public int    ClientId    { get; }
        public int    RoomId      { get; private set; }

        public event Action<int, byte[], int> OnAudioReceived;
        public event Action<double>           OnHandshakeAck;
        public event Action<bool>             OnRoomJoinAck;
        public event Action                   OnDisconnected;
        public event Action<double>           OnHeartbeatAck;

        private readonly string _host;
        private readonly int    _port;
        private TcpClient       _tcp;
        private NetworkStream   _stream;
        private CancellationTokenSource _cts;
        private uint  _outSeq;
        private long  _lastHandshakeMs;
        private readonly ConcurrentQueue<byte[]> _sendQueue = new();
        private readonly SemaphoreSlim _queueSignal = new(0);
        private Task _sendLoopTask;

        // Pre-allocated length prefix buffer
        private readonly byte[] _lenBuf = new byte[4];

        public TcpVoiceTransport(string host, int port, int clientId)
        {
            _host = host; _port = port; ClientId = clientId;
        }

        public async Task<bool> ConnectAsync(CancellationToken ct = default)
        {
            try
            {
                _tcp = new TcpClient { NoDelay = true };
                _tcp.ReceiveBufferSize = 256 * 1024;
                _tcp.SendBufferSize    = 256 * 1024;
                // ConnectAsync(host, port) — 3-arg overload with CancellationToken
                // is only available in .NET 6+; Unity uses an older runtime.
                var connectTask = _tcp.ConnectAsync(_host, _port);
                var timeoutTask = Task.Delay(5000, ct); // 5-second connect timeout
                if (await Task.WhenAny(connectTask, timeoutTask) == timeoutTask)
                    throw new TimeoutException("TCP connect timed out.");
                await connectTask; // rethrow any socket exception
                _stream = _tcp.GetStream();
                _cts = new CancellationTokenSource();
                IsConnected = true;
                _ = Task.Run(() => ReceiveLoopAsync(_cts.Token));
                _sendLoopTask = Task.Run(() => SendLoopAsync(_cts.Token));
                SendHandshake();
                Debug.Log($"[TcpTransport] Connected to {_host}:{_port} as client {ClientId}");
                return true;
            }
            catch (Exception ex) { Debug.LogError($"[TcpTransport] Connect failed: {ex.Message}"); return false; }
        }

        public void JoinRoom(int roomId)
        {
            RoomId = roomId;
            byte[] buf = new byte[HeaderSize];
            WriteHeader(buf, 2, roomId, ClientId, _outSeq++, NowMs(), 0);
            TrySendFramed(buf, HeaderSize);
        }

        public void SendAudio(byte[] opus, int len)
        {
            if (!IsConnected) return;
            byte[] buf = new byte[HeaderSize + len];
            WriteHeader(buf, 3, RoomId, ClientId, _outSeq++, NowMs(), len);
            Buffer.BlockCopy(opus, 0, buf, HeaderSize, len); // payload starts right after 28-byte header
            TrySendFramed(buf, HeaderSize + len);
        }

        public void SendHeartbeat()
        {
            if (!IsConnected) return;
            byte[] buf = new byte[HeaderSize];
            WriteHeader(buf, 4, RoomId, ClientId, _outSeq++, NowMs(), 0);
            TrySendFramed(buf, HeaderSize);
        }

        public void Disconnect()
        {
            IsConnected = false;
            _cts?.Cancel();
            try { _tcp?.Close(); } catch { }
        }

        private void SendHandshake()
        {
            _lastHandshakeMs = NowMs();
            byte[] buf = new byte[HeaderSize];
            WriteHeader(buf, 1, 0, ClientId, _outSeq++, _lastHandshakeMs, 0);
            TrySendFramed(buf, HeaderSize);
        }

        private async Task ReceiveLoopAsync(CancellationToken ct)
        {
            byte[] lenBuf = new byte[4];
            byte[] packetBuf = new byte[4096];
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    // Read 4-byte length prefix
                    if (!await ReadExactAsync(_stream, lenBuf, 4, ct)) break;
                    uint msgLen = BinaryPrimitives.ReadUInt32LittleEndian(lenBuf);
                    if (msgLen == 0 || msgLen > 4096) break;

                    if (packetBuf.Length < (int)msgLen)
                        packetBuf = new byte[msgLen];

                    if (!await ReadExactAsync(_stream, packetBuf, (int)msgLen, ct)) break;

                    ParsePacket(packetBuf, (int)msgLen);
                }
            }
            catch (OperationCanceledException) { }
            catch (IOException ex) { Debug.LogWarning($"[TcpTransport] Recv: {ex.Message}"); }
            catch (Exception ex)   { Debug.LogError($"[TcpTransport] Recv error: {ex.Message}"); }

            IsConnected = false;
            OnDisconnected?.Invoke();
        }

        private void ParsePacket(byte[] buf, int len)
        {
            if (len < HeaderSize) return;
            int pktType    = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(0, 4));
            int senderId   = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(8, 4));
            int payloadLen = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(24, 4));
            if (payloadLen < 0 || HeaderSize + payloadLen > len) return;

            switch (pktType)
            {
                case 1:
                    OnHandshakeAck?.Invoke(NowMs() - _lastHandshakeMs);
                    break;
                case 2:
                    bool ok = payloadLen >= 4 && BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(HeaderSize, 4)) == 1;
                    OnRoomJoinAck?.Invoke(ok);
                    break;
                case 3 when payloadLen > 0 && senderId != ClientId:
                    byte[] opus = new byte[payloadLen];
                    Buffer.BlockCopy(buf, HeaderSize, opus, 0, payloadLen);
                    OnAudioReceived?.Invoke(senderId, opus, payloadLen);
                    break;
                 case 4:
                    long heartbeatTs = BinaryPrimitives.ReadInt64LittleEndian(buf.AsSpan(16, 8));
                    double heartbeatRtt = NowMs() - heartbeatTs;
                    OnHeartbeatAck?.Invoke(heartbeatRtt);
                    break;
            }
        }

         private void TrySendFramed(byte[] data, int length)
         {
             if (!IsConnected || _stream == null) return;
 
             byte[] frame = new byte[4 + length];
             BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)length);
             Buffer.BlockCopy(data, 0, frame, 4, length);
 
             if (_sendQueue.Count >= 100)
             {
                 _sendQueue.TryDequeue(out _);
             }
 
             _sendQueue.Enqueue(frame);
             try { _queueSignal.Release(); } catch (ObjectDisposedException) { }
         }
 
         private async Task SendLoopAsync(CancellationToken ct)
         {
             try
             {
                 while (!ct.IsCancellationRequested && IsConnected && _stream != null)
                 {
                     await _queueSignal.WaitAsync(ct);
                     if (_sendQueue.TryDequeue(out byte[] frame))
                     {
                         await _stream.WriteAsync(frame, 0, frame.Length, ct);
                     }
                 }
             }
             catch (OperationCanceledException) { }
             catch (Exception ex)
             {
                 Debug.LogWarning($"[TcpTransport] SendLoop error: {ex.Message}");
                 IsConnected = false;
                 OnDisconnected?.Invoke();
             }
         }

        private void TrySend(byte[] data) => TrySendFramed(data, data.Length);

        private static void WriteHeader(byte[] buf, int type, int room, int client, uint seq, long ts, int payloadLen)
        {
            BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(0, 4),  type);
            BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(4, 4),  room);
            BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(8, 4),  client);
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(12, 4), seq);
            BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(16, 8), ts);
            BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(24, 4), payloadLen);
        }

        private static async Task<bool> ReadExactAsync(NetworkStream s, byte[] buf, int count, CancellationToken ct)
        {
            int read = 0;
            while (read < count)
            {
                int n = await s.ReadAsync(buf, read, count - read, ct);
                if (n == 0) return false;
                read += n;
            }
            return true;
        }

        private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

         public void Dispose() { Disconnect(); _cts?.Dispose(); _tcp?.Dispose(); _queueSignal?.Dispose(); }
    }
}
