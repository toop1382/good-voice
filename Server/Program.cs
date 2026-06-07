using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Server.Core;
using Server.Network;

namespace Server
{
    class Program
    {
        static async Task Main(string[] args)
        {
            // ── Defaults ─────────────────────────────────────────────────
            int    udpPort    = 50005;
            int    tcpPort    = 50006;
            int    wsPort     = 50007;
            int    webPort    = 5000;
            bool   enableUdp  = true;
            bool   enableTcp  = true;
            bool   enableWs   = true;
            bool   enableWeb  = true;
 
            // ── Argument parsing ──────────────────────────────────────────
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--udp-port"   when i + 1 < args.Length: int.TryParse(args[++i], out udpPort);  break;
                    case "--tcp-port"   when i + 1 < args.Length: int.TryParse(args[++i], out tcpPort);  break;
                    case "--ws-port"    when i + 1 < args.Length: int.TryParse(args[++i], out wsPort);   break;
                    case "--dashboard-port" when i + 1 < args.Length: int.TryParse(args[++i], out webPort); break;
                    case "--port" or "-p" when i + 1 < args.Length:
                        // Legacy --port sets UDP port for backward compatibility
                        int.TryParse(args[++i], out udpPort); break;
                    case "--no-udp":    enableUdp = false; break;
                    case "--no-tcp":    enableTcp = false; break;
                    case "--no-ws":     enableWs  = false; break;
                    case "--no-dashboard": enableWeb = false; break;
                    case "--protocols" or "--proto" when i + 1 < args.Length:
                        string proto = args[++i].ToLowerInvariant();
                        enableUdp = proto.Contains("udp");
                        enableTcp = proto.Contains("tcp");
                        enableWs  = proto.Contains("ws") || proto.Contains("websocket");
                        break;
                }
            }

            // ── Header ───────────────────────────────────────────────────
            Console.WriteLine("══════════════════════════════════════════════════");
            Console.WriteLine("     LOW-LATENCY VOICE CHAT SERVER  (Multi-Protocol)");
            Console.WriteLine("══════════════════════════════════════════════════");
            if (enableUdp) Console.WriteLine($"  UDP       → port {udpPort}  (low-latency, fire-and-forget)");
            if (enableTcp) Console.WriteLine($"  TCP       → port {tcpPort}  (reliable, length-prefixed frames)");
            if (enableWs ) Console.WriteLine($"  WebSocket → ws://0.0.0.0:{wsPort}/voice/  (browser/WebGL friendly)");
            if (enableWeb) Console.WriteLine($"  Dashboard → http://localhost:{webPort}/  (web control panel)");
            Console.WriteLine("══════════════════════════════════════════════════");

            // ── Shared state (all protocols use the same room manager) ────
            var roomManager     = new RoomManager();
            var pruningService  = new SessionPruningService(roomManager);
            var multiServer     = new MultiProtocolVoiceServer();

            if (enableUdp) multiServer.Add(new UdpVoiceServer(udpPort, roomManager));
            if (enableTcp) multiServer.Add(new TcpVoiceServer(tcpPort, roomManager));
            if (enableWs ) multiServer.Add(new WebSocketVoiceServer(wsPort, roomManager));

            // ── Start ─────────────────────────────────────────────────────
            multiServer.StartAll();
            pruningService.Start();
 
            WebApplication? webApp = null;
            if (enableWeb)
            {
                try
                {
                    var webBuilder = WebApplication.CreateBuilder(args);
                    webBuilder.Logging.ClearProviders(); // Disable verbose web logging to keep server console clean
                    webBuilder.WebHost.UseUrls($"http://0.0.0.0:{webPort}");
                    webBuilder.Services.AddRazorComponents().AddInteractiveServerComponents();
                    webBuilder.Services.AddSingleton(roomManager);
 
                    webApp = webBuilder.Build();
                    webApp.UseStaticFiles();
                    webApp.UseAntiforgery();
                    webApp.MapRazorComponents<Server.Dashboard.App>()
                        .AddInteractiveServerRenderMode();
 
                    _ = webApp.RunAsync();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Web] Failed to start dashboard: {ex.Message}");
                }
            }
 
            Console.WriteLine("\nServer running. Press CTRL+C or Enter to stop.\n");
 
            // ── Wait for shutdown signal ──────────────────────────────────
            var tcs = new TaskCompletionSource<bool>();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; tcs.TrySetResult(true); };
            _ = Task.Run(() => { try { Console.ReadLine(); } catch { } tcs.TrySetResult(true); });
            await tcs.Task;

            // ── Shutdown ──────────────────────────────────────────────────
            Console.WriteLine("\nShutting down...");
            pruningService.Stop();
            multiServer.StopAll();
            if (webApp != null)
            {
                await webApp.StopAsync();
            }
            Console.WriteLine("Shutdown complete.");
        }
    }
}
