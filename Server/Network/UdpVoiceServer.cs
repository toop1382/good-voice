using System;
using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Server.Core;

namespace Server.Network
{
    public readonly struct InboundPacket
    {
        public byte[] RawBuffer { get; }
        public int BytesReceived { get; }
        public EndPoint RemoteEndPoint { get; }
        public long ReceiveTimestampMs { get; }

        public InboundPacket(byte[] rawBuffer, int bytesReceived, EndPoint remoteEndPoint, long receiveTimestampMs)
        {
            RawBuffer = rawBuffer;
            BytesReceived = bytesReceived;
            RemoteEndPoint = remoteEndPoint;
            ReceiveTimestampMs = receiveTimestampMs;
        }
    }

    public class UdpVoiceServer : IVoiceServer
    {
        private readonly int _port;
        private readonly Socket _socket;
        private readonly Channel<InboundPacket> _packetChannel;
        private readonly PacketRouter _packetRouter;
        private const int MaxPacketSize = 2048;
        private CancellationTokenSource? _cts;
        private bool _started;

        public string ProtocolName => "UDP";

        public UdpVoiceServer(int port, RoomManager roomManager)
        {
            _port = port;
            _packetRouter = new PacketRouter(roomManager);
            _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            _socket.ReceiveBufferSize = 1024 * 1024 * 8;
            _socket.SendBufferSize    = 1024 * 1024 * 8;

            var opts = new UnboundedChannelOptions { SingleWriter = true, AllowSynchronousContinuations = false };
            _packetChannel = Channel.CreateUnbounded<InboundPacket>(opts);
        }

        public void Start()
        {
            if (_started) return;
            _started = true;
            _cts = new CancellationTokenSource();
            _socket.Bind(new IPEndPoint(IPAddress.Parse("192.168.1.105"), _port));
            Console.WriteLine($"[UDP] Listening on port {_port}");

            var ct = _cts.Token;
            _ = Task.Run(() => RunReceiveLoopAsync(ct), ct);
            int workers = Environment.ProcessorCount;
            for (int i = 0; i < workers; i++)
                _ = Task.Run(() => RunWorkerLoopAsync(ct), ct);
            Console.WriteLine($"[UDP] Started {workers} packet-routing workers");
        }

        public void Stop()
        {
            if (!_started) return;
            _started = false;
            _cts?.Cancel();
            _packetChannel.Writer.Complete();
            try { _socket.Close(); } catch { }
            _cts?.Dispose();
            _cts = null;
            Console.WriteLine("[UDP] Server stopped");
        }

        private async Task RunReceiveLoopAsync(CancellationToken ct)
        {
            EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
            while (!ct.IsCancellationRequested)
            {
                byte[] buf = ArrayPool<byte>.Shared.Rent(MaxPacketSize);
                try
                {
                    var result = await _socket.ReceiveFromAsync(new Memory<byte>(buf), SocketFlags.None, remote, ct);
                    long ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    var pkt = new InboundPacket(buf, result.ReceivedBytes, result.RemoteEndPoint, ts);
                    if (!_packetChannel.Writer.TryWrite(pkt))
                        ArrayPool<byte>.Shared.Return(buf);
                }
                catch (OperationCanceledException) { ArrayPool<byte>.Shared.Return(buf); break; }
                catch (SocketException) { ArrayPool<byte>.Shared.Return(buf); }
                catch (Exception ex) { Console.WriteLine($"[UDP] Recv error: {ex.Message}"); ArrayPool<byte>.Shared.Return(buf); }
            }
        }

        private async Task RunWorkerLoopAsync(CancellationToken ct)
        {
            var reader = _packetChannel.Reader;
            try
            {
                while (await reader.WaitToReadAsync(ct))
                {
                    while (reader.TryRead(out var pkt))
                    {
                        try
                        {
                            if (pkt.RemoteEndPoint is IPEndPoint ip)
                            {
                                // Create a per-packet UDP send channel for this source endpoint
                                var channel = new UdpSendChannel(_socket, ip);
                                _packetRouter.RoutePacket(pkt.RawBuffer, pkt.BytesReceived, ip, channel, pkt.ReceiveTimestampMs);
                            }
                        }
                        catch (Exception ex) { Console.WriteLine($"[UDP] Routing error: {ex.Message}"); }
                        finally { ArrayPool<byte>.Shared.Return(pkt.RawBuffer); }
                    }
                }
            }
            catch (OperationCanceledException) { }
        }
    }
}
