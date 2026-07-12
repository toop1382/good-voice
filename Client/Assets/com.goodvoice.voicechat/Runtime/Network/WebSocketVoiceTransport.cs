using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Client.Network
{
    /// <summary>
    /// WebSocket voice transport — works through HTTP proxies and firewalls.
    /// Each WebSocket message = one voice packet (no length prefix needed).
    ///
    /// Ideal for WebGL Unity builds and browser-based clients.
    /// Connects to ws://host:{port}/voice/
    /// Uses ClientWebSocket from System.Net.WebSockets (available in Unity 2021.2+).
    /// </summary>
    public class WebSocketVoiceTransport : IVoiceTransport
    {
        private const int HeaderSize    = 28;
        private const int MaxPacketSize = 4096;

        public string Protocol    => "WebSocket";
        public bool   IsConnected { get; private set; }
        public int    ClientId    { get; set; }
        public int    RoomId      { get; private set; }

        public event Action<int, byte[], int, uint, long> OnAudioReceived;
        public event Action<double>           OnHandshakeAck;
        public event Action<bool>             OnRoomJoinAck;
        public event Action                   OnDisconnected;
        public event Action<double>           OnHeartbeatAck;
        public event Action<int, string>      OnUserJoined;
        public event Action<int>              OnUserLeft;

        private readonly string _url; // e.g. "ws://127.0.0.1:50007/voice/"
        private ClientWebSocket      _ws;
        private CancellationTokenSource _cts;
        private readonly ConcurrentQueue<byte[]> _sendQueue = new();
        private readonly SemaphoreSlim _queueSignal = new(0);
        private Task _sendLoopTask;
        private TaskCompletionSource<bool> _handshakeTcs;
        private uint  _outSeq;
        private long  _lastHandshakeMs;

        public WebSocketVoiceTransport(string host, int port, int clientId)
        {
            _url = $"ws://{host}:{port}/voice/";
            ClientId = clientId;
        }

        public async Task<bool> ConnectAsync(string metadata = "", CancellationToken ct = default)
        {
            try
            {
                 _ws = new ClientWebSocket();
                 _ws.Options.SetBuffer(receiveBufferSize: 256 * 1024, sendBufferSize: 256 * 1024);
                 await _ws.ConnectAsync(new Uri(_url), ct);
                 _cts = new CancellationTokenSource();
                 _handshakeTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                 IsConnected = true;
                 _ = Task.Run(() => ReceiveLoopAsync(_cts.Token));
                 _sendLoopTask = Task.Run(() => SendLoopAsync(_cts.Token));
                 SendHandshake(metadata);

                 var handshakeTimeoutTask = Task.Delay(5000, ct);
                 if (await Task.WhenAny(_handshakeTcs.Task, handshakeTimeoutTask) == handshakeTimeoutTask)
                 {
                     Disconnect();
                     throw new TimeoutException("Handshake timed out.");
                 }

                 await _handshakeTcs.Task;
                 Debug.Log($"[WsTransport] Connected to {_url} as client {ClientId}");
                 return true;
            }
            catch (Exception ex) { Debug.LogError($"[WsTransport] Connect failed: {ex.Message}"); return false; }
        }

        public void JoinRoom(int roomId, string metadata = "")
        {
            RoomId = roomId;
            byte[] metadataBytes = System.Text.Encoding.UTF8.GetBytes(metadata ?? string.Empty);
            byte[] buf = new byte[HeaderSize + metadataBytes.Length];
            WriteHeader(buf, 2, roomId, ClientId, _outSeq++, NowMs(), metadataBytes.Length);
            Buffer.BlockCopy(metadataBytes, 0, buf, HeaderSize, metadataBytes.Length);
            TrySend(buf, buf.Length);
        }

        public void SendAudio(byte[] opus, int len)
        {
            if (!IsConnected) return;
            byte[] buf = new byte[HeaderSize + len];
            WriteHeader(buf, 3, RoomId, ClientId, _outSeq++, NowMs(), len);
            Buffer.BlockCopy(opus, 0, buf, HeaderSize, len);
            TrySend(buf, buf.Length);
        }

        public void SendHeartbeat()
        {
            if (!IsConnected) return;
            byte[] buf = new byte[HeaderSize];
            WriteHeader(buf, 4, RoomId, ClientId, _outSeq++, NowMs(), 0);
            TrySend(buf, buf.Length);
        }

        public void Disconnect()
        {
            IsConnected = false;
            _cts?.Cancel();
            try
            {
                if (_ws?.State == WebSocketState.Open)
                    _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None)
                       .GetAwaiter().GetResult();
            }
            catch { }
        }

        private void SendHandshake(string metadata)
        {
            _lastHandshakeMs = NowMs();
            byte[] buf = new byte[HeaderSize];
            WriteHeader(buf, 1, 0, 0, 0, _lastHandshakeMs, 0);
            TrySend(buf, HeaderSize);
        }

        private async Task ReceiveLoopAsync(CancellationToken ct)
        {
            byte[] buf = new byte[MaxPacketSize];
            try
            {
                while (!ct.IsCancellationRequested && _ws.State == WebSocketState.Open)
                {
                    var result = await _ws.ReceiveAsync(new ArraySegment<byte>(buf), ct);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", ct);
                        break;
                    }

                    if (result.MessageType == WebSocketMessageType.Binary && result.EndOfMessage)
                        ParsePacket(buf, result.Count);
                }
            }
            catch (OperationCanceledException) { }
            catch (WebSocketException ex) { Debug.LogWarning($"[WsTransport] Recv: {ex.Message}"); }
            catch (Exception ex)          { Debug.LogError($"[WsTransport] Recv error: {ex.Message}"); }

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
                    ClientId = senderId;
                    _handshakeTcs?.TrySetResult(true);
                    OnHandshakeAck?.Invoke(NowMs() - _lastHandshakeMs);
                    break;
                case 2:
                    bool ok = payloadLen >= 4 && BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(HeaderSize, 4)) == 1;
                    OnRoomJoinAck?.Invoke(ok);
                    break;
                case 3 when payloadLen > 0 && senderId != ClientId:
                    {
                        byte[] opus = System.Buffers.ArrayPool<byte>.Shared.Rent(payloadLen);
                        try
                        {
                            Buffer.BlockCopy(buf, HeaderSize, opus, 0, payloadLen);
                            uint seq = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(12, 4));
                            long ts = BinaryPrimitives.ReadInt64LittleEndian(buf.AsSpan(16, 8));
                            OnAudioReceived?.Invoke(senderId, opus, payloadLen, seq, ts);
                        }
                        finally
                        {
                            System.Buffers.ArrayPool<byte>.Shared.Return(opus);
                        }
                    }
                    break;
                 case 4:
                    long heartbeatTs = BinaryPrimitives.ReadInt64LittleEndian(buf.AsSpan(16, 8));
                    double heartbeatRtt = NowMs() - heartbeatTs;
                    OnHeartbeatAck?.Invoke(heartbeatRtt);
                    break;
                case 5:
                    if (payloadLen >= 0)
                    {
                        string metadata = System.Text.Encoding.UTF8.GetString(buf, HeaderSize, payloadLen);
                        OnUserJoined?.Invoke(senderId, metadata);
                    }
                    break;
                case 6:
                    OnUserLeft?.Invoke(senderId);
                    break;
            }
        }

        private void TrySend(byte[] data, int len)
        {
            if (!IsConnected || _ws?.State != WebSocketState.Open) return;
 
            byte[] copy = new byte[len];
            Buffer.BlockCopy(data, 0, copy, 0, len);
 
            if (_sendQueue.Count >= 100)
            {
                _sendQueue.TryDequeue(out _);
            }
 
            _sendQueue.Enqueue(copy);
            try { _queueSignal.Release(); } catch (ObjectDisposedException) { }
        }
 
        private async Task SendLoopAsync(CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested && IsConnected && _ws?.State == WebSocketState.Open)
                {
                    await _queueSignal.WaitAsync(ct);
                    if (_sendQueue.TryDequeue(out byte[] data))
                    {
                        await _ws.SendAsync(new ArraySegment<byte>(data),
                            WebSocketMessageType.Binary, endOfMessage: true, ct);
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Debug.LogWarning($"[WsTransport] SendLoop error: {ex.Message}");
                IsConnected = false;
                OnDisconnected?.Invoke();
            }
        }

        private static void WriteHeader(byte[] buf, int type, int room, int client, uint seq, long ts, int payloadLen)
        {
            BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(0, 4),  type);
            BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(4, 4),  room);
            BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(8, 4),  client);
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(12, 4), seq);
            BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(16, 8), ts);
            BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(24, 4), payloadLen);
        }

        private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

         public void Dispose() { Disconnect(); _cts?.Dispose(); _ws?.Dispose(); _queueSignal.Dispose(); }
    }
}
