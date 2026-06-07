using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Server.Core;

namespace Server.Network
{
    /// <summary>
    /// TCP voice server — reliable, ordered, stream-based transport.
    /// Each client holds a persistent TcpClient connection.
    ///
    /// Frame framing: [4-byte uint32 LE message length][N bytes packet data]
    ///
    /// Best for: clients behind strict firewalls, or when reliable delivery
    /// matters more than lowest latency (e.g. control channels, slow networks).
    /// </summary>
    public class TcpVoiceServer : IVoiceServer
    {
        private readonly int _port;
        private readonly RoomManager _roomManager;
        private readonly PacketRouter _packetRouter;
        private TcpListener? _listener;
        private CancellationTokenSource? _cts;
        private bool _started;

        public string ProtocolName => "TCP";

        public TcpVoiceServer(int port, RoomManager roomManager)
        {
            _port = port;
            _roomManager = roomManager;
            _packetRouter = new PacketRouter(roomManager);
        }

        public void Start()
        {
            if (_started) return;
            _started = true;
            _cts = new CancellationTokenSource();
            _listener = new TcpListener(IPAddress.Any, _port);
            _listener.Server.NoDelay = true; // Disable Nagle — critical for voice latency
            _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _listener.Start(backlog: 256);
            Console.WriteLine($"[TCP] Listening on port {_port}");
            _ = Task.Run(() => AcceptLoopAsync(_cts.Token));
        }

        public void Stop()
        {
            if (!_started) return;
            _started = false;
            _cts?.Cancel();
            _listener?.Stop();
            _cts?.Dispose();
            _cts = null;
            Console.WriteLine("[TCP] Server stopped");
        }

        private async Task AcceptLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    TcpClient tcp = await _listener!.AcceptTcpClientAsync(ct);
                    tcp.NoDelay = true;
                    tcp.ReceiveBufferSize = 256 * 1024;
                    tcp.SendBufferSize    = 256 * 1024;
                    _ = Task.Run(() => HandleClientAsync(tcp, ct), ct);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { Console.WriteLine($"[TCP] Accept error: {ex.Message}"); }
            }
        }

        private async Task HandleClientAsync(TcpClient tcp, CancellationToken ct)
        {
            IPEndPoint? remoteEP = tcp.Client.RemoteEndPoint as IPEndPoint;
            if (remoteEP == null) { tcp.Dispose(); return; }

            Console.WriteLine($"[TCP] Client connected from {remoteEP}");

            NetworkStream stream = tcp.GetStream();
            var sendChannel = new TcpSendChannel(stream);

            // Temp client ID = 0 until we read the handshake packet and learn the real ID
            int knownClientId = -1;

            byte[] lenBuf = new byte[4];
            byte[] packetBuf = ArrayPool<byte>.Shared.Rent(4096);

            try
            {
                while (!ct.IsCancellationRequested && sendChannel.IsAlive)
                {
                    // ── Read 4-byte length prefix ───────────────────────────
                    if (!await ReadExactAsync(stream, lenBuf, 0, 4, ct)) break;
                    uint msgLen = BinaryPrimitives.ReadUInt32LittleEndian(lenBuf);

                    if (msgLen == 0 || msgLen > 4096)
                    {
                        Console.WriteLine($"[TCP] Invalid frame length {msgLen} from {remoteEP}. Closing.");
                        break;
                    }

                    // Grow buffer if needed
                    if (packetBuf.Length < (int)msgLen)
                    {
                        ArrayPool<byte>.Shared.Return(packetBuf);
                        packetBuf = ArrayPool<byte>.Shared.Rent((int)msgLen);
                    }

                    // ── Read packet body ────────────────────────────────────
                    if (!await ReadExactAsync(stream, packetBuf, 0, (int)msgLen, ct)) break;

                    long ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    _packetRouter.RoutePacket(packetBuf, (int)msgLen, remoteEP, sendChannel, ts);

                    // Track client ID for cleanup (bytes 8-11 = ClientId in header)
                    if (knownClientId == -1 && msgLen >= 12)
                        knownClientId = BinaryPrimitives.ReadInt32LittleEndian(packetBuf.AsSpan(8, 4));
                }
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
            catch (Exception ex) { Console.WriteLine($"[TCP] Client {remoteEP} error: {ex.Message}"); }
            finally
            {
                ArrayPool<byte>.Shared.Return(packetBuf);
                if (knownClientId != -1)
                    _roomManager.UnregisterClient(knownClientId, DateTime.UtcNow.Ticks, 0);
                tcp.Dispose();
                Console.WriteLine($"[TCP] Client {remoteEP} disconnected.");
            }
        }

        /// <summary>Read exactly <paramref name="count"/> bytes, blocking until available.</summary>
        private static async Task<bool> ReadExactAsync(
            Stream stream, byte[] buf, int offset, int count, CancellationToken ct)
        {
            int read = 0;
            while (read < count)
            {
                int n = await stream.ReadAsync(buf, offset + read, count - read, ct);
                if (n == 0) return false; // graceful close
                read += n;
            }
            return true;
        }
    }
}
