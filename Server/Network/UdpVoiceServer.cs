using System;
using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using Server.Core;

namespace Server.Network
{
    public readonly struct InboundPacket
    {
        public byte[] RawBuffer { get; }
        public int BytesReceived { get; }
        public EndPoint RemoteEndPoint { get; }
        public long ReceiveTimestampMs { get; }
        public Socket ReceiverSocket { get; }

        public InboundPacket(byte[] rawBuffer, int bytesReceived, EndPoint remoteEndPoint, long receiveTimestampMs, Socket receiverSocket)
        {
            RawBuffer = rawBuffer;
            BytesReceived = bytesReceived;
            RemoteEndPoint = remoteEndPoint;
            ReceiveTimestampMs = receiveTimestampMs;
            ReceiverSocket = receiverSocket;
        }
    }

    public class UdpVoiceServer : IVoiceServer
    {
        private readonly int _port;
        private readonly RoomManager _roomManager;
        private readonly Socket[] _sockets;
        private readonly Channel<InboundPacket> _packetChannel;
        private readonly PacketRouter _packetRouter;
        private const int MaxPacketSize = 2048;
        private CancellationTokenSource? _cts;
        private bool _started;

        public string ProtocolName => "UDP";

        public UdpVoiceServer(int port, RoomManager roomManager)
        {
            _port = port;
            _roomManager = roomManager;
            _packetRouter = new PacketRouter(roomManager);

            int workers = Environment.ProcessorCount;
            _sockets = new Socket[workers];
            for (int i = 0; i < workers; i++) {
                _sockets[i] = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                _sockets[i].ReceiveBufferSize = 1024 * 1024 * 8;
                _sockets[i].SendBufferSize    = 1024 * 1024 * 8;
                _sockets[i].SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) {
                    // SO_REUSEPORT
                    try { _sockets[i].SetSocketOption(SocketOptionLevel.Socket, (SocketOptionName)15, true); } catch { }
                }
            }

            var opts = new UnboundedChannelOptions { SingleWriter = false, AllowSynchronousContinuations = false };
            _packetChannel = Channel.CreateUnbounded<InboundPacket>(opts);
        }

        public void Start()
        {
            if (_started) return;
            _started = true;
            _cts = new CancellationTokenSource();

            var endPoint = new IPEndPoint(IPAddress.Any, _port);
            for (int i = 0; i < _sockets.Length; i++) {
                _sockets[i].Bind(endPoint);
            }
            Console.WriteLine($"[UDP] Listening on port {_port}");

            var ct = _cts.Token;
            for (int i = 0; i < _sockets.Length; i++)
                _ = Task.Run(() => RunReceiveLoopAsync(_sockets[i], ct), ct);
            for (int i = 0; i < _sockets.Length; i++)
                _ = Task.Run(() => RunWorkerLoopAsync(ct), ct);
            Console.WriteLine($"[UDP] Started {_sockets.Length} packet-routing workers with SO_REUSEPORT");
        }

        public void Stop()
        {
            if (!_started) return;
            _started = false;
            _cts?.Cancel();
            _packetChannel.Writer.Complete();
            for (int i = 0; i < _sockets.Length; i++) {
                try { _sockets[i].Close(); } catch { }
            }
            _cts?.Dispose();
            _cts = null;
            Console.WriteLine("[UDP] Server stopped");
        }

        private async Task RunReceiveLoopAsync(Socket socket, CancellationToken ct)
        {
            EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
            while (!ct.IsCancellationRequested)
            {
                byte[] buf = ArrayPool<byte>.Shared.Rent(MaxPacketSize);
                try
                {
                    var result = await socket.ReceiveFromAsync(new Memory<byte>(buf), SocketFlags.None, remote, ct);
                    long ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    var pkt = new InboundPacket(buf, result.ReceivedBytes, result.RemoteEndPoint, ts, socket);
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
                                ISendChannel? channel = null;
                                if (_roomManager.TryGetSessionByEndPoint(ip, out var session) && session != null)
                                {
                                    channel = session.SendChannel;
                                }

                                if (channel == null)
                                {
                                    channel = new UdpSendChannel(pkt.ReceiverSocket, ip);
                                }

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
