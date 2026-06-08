using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Net;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Server.Core;

namespace Server.Network
{
    /// <summary>
    /// WebSocket voice server built on HttpListener.
    /// No external dependencies — uses the System.Net.WebSockets API included in .NET 5+.
    ///
    /// Each WebSocket message = one voice packet (no length prefix, messages are framed natively).
    ///
    /// Best for: WebGL Unity builds, browser clients, or clients behind HTTP proxies.
    ///
    /// URL: ws://host:{port}/voice/
    /// To enable on Windows without admin rights: run once as admin to reserve the URL prefix:
    ///   netsh http add urlacl url=http://+:{port}/voice/ user=EVERYONE
    /// </summary>
    public class WebSocketVoiceServer : IVoiceServer
    {
        private readonly string _prefix;
        private readonly int _port;
        private readonly RoomManager _roomManager;
        private readonly PacketRouter _packetRouter;
        private HttpListener? _httpListener;
        private CancellationTokenSource? _cts;
        private bool _started;

        public string ProtocolName => "WebSocket";

        public WebSocketVoiceServer(int port, RoomManager roomManager)
        {
            _port = port;
            _prefix = $"http://+:{port}/voice/";
            _roomManager = roomManager;
            _packetRouter = new PacketRouter(roomManager);
        }

        public void Start()
        {
            if (_started) return;
            _started = true;
            _cts = new CancellationTokenSource();

            string[] prefixesToTry = new[]
            {
                _prefix,
                $"http://localhost:{_port}/voice/",
                $"http://127.0.0.1:{_port}/voice/"
            };

            foreach (var prefix in prefixesToTry)
            {
                try
                {
                    _httpListener = new HttpListener();
                    _httpListener.Prefixes.Add(prefix);
                    _httpListener.Start();
                    break;
                }
                catch (HttpListenerException ex)
                {
                    Console.WriteLine($"[WS] Failed to bind to prefix {prefix}: {ex.Message} (Error code: {ex.ErrorCode})");
                    try { _httpListener?.Close(); } catch { }
                    _httpListener = null;
                }
            }

            if (_httpListener == null)
            {
                Console.WriteLine("[WS] Failed to start WebSocket server on any of the prefixes.");
                _started = false;
                return;
            }

            Console.WriteLine($"[WS] Listening on {string.Join(", ", _httpListener.Prefixes)}");
            _ = Task.Run(() => AcceptLoopAsync(_cts.Token));
        }

        public void Stop()
        {
            if (!_started) return;
            _started = false;
            _cts?.Cancel();
            _httpListener?.Stop();
            _cts?.Dispose();
            _cts = null;
            Console.WriteLine("[WS] Server stopped");
        }

        private async Task AcceptLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    HttpListenerContext ctx = await _httpListener!.GetContextAsync().WaitAsync(ct);
                    if (ctx.Request.IsWebSocketRequest)
                        _ = Task.Run(() => HandleWebSocketAsync(ctx, ct), ct);
                    else
                    {
                        ctx.Response.StatusCode = 426; // Upgrade Required
                        ctx.Response.AddHeader("Upgrade", "websocket");
                        ctx.Response.Close();
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (HttpListenerException) { break; }
                catch (Exception ex) { Console.WriteLine($"[WS] Accept error: {ex.Message}"); }
            }
        }

        private async Task HandleWebSocketAsync(HttpListenerContext ctx, CancellationToken ct)
        {
            IPEndPoint? remoteEP = ctx.Request.RemoteEndPoint;
            WebSocket? ws = null;

            try
            {
                var wsCtx = await ctx.AcceptWebSocketAsync(subProtocol: null);
                ws = wsCtx.WebSocket;
                Console.WriteLine($"[WS] Client connected from {remoteEP}");

                var sendChannel = new WebSocketSendChannel(ws);
                int knownClientId = -1;

                byte[] buf = ArrayPool<byte>.Shared.Rent(4096);
                try
                {
                    while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
                    {
                        WebSocketReceiveResult result = await ws.ReceiveAsync(
                            new ArraySegment<byte>(buf), ct);

                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", ct);
                            break;
                        }

                        if (!result.EndOfMessage)
                        {
                            // Skip oversized fragmented messages
                            Console.WriteLine("[WS] Fragmented message received — skipping.");
                            continue;
                        }

                        int length = result.Count;
                        if (length < 1) continue;

                        long ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                        _packetRouter.RoutePacket(buf, length, remoteEP!, sendChannel, ts);

                        if (knownClientId == -1 && length >= 12)
                            knownClientId = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(8, 4));
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buf);
                    if (knownClientId != -1)
                        _roomManager.UnregisterClient(knownClientId, sendChannel);
                }
            }
            catch (OperationCanceledException) { }
            catch (WebSocketException ex) { Console.WriteLine($"[WS] Client {remoteEP} WS error: {ex.Message}"); }
            catch (Exception ex) { Console.WriteLine($"[WS] Client {remoteEP} error: {ex.Message}"); }
            finally
            {
                ws?.Dispose();
                Console.WriteLine($"[WS] Client {remoteEP} disconnected.");
            }
        }
    }
}
