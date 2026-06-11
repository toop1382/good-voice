using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using global::MockClient.Network;

namespace MockClient
{
    class Program
    {
        private static readonly GlobalCounters _globalCounters = new();

        static async Task Main(string[] args)
        {
            string serverIp = "194.33.105.209";
            int serverPort = 50005;
            int totalClients = 10;
            int totalRooms = 2;
            int durationSeconds = 60;
            int packetIntervalMs = 20;
            int payloadSize = 120;
            int staggerMs = 10;

            bool isLoopbackMode = false;
            string protocol = "udp";
            int loopbackRoomId = 100;
            int loopbackClientId = 9999;

            for (int i = 0; i < args.Length; i++)
            {
                if ((args[i] == "--server" || args[i] == "-s") && i + 1 < args.Length)
                {
                    serverIp = args[i + 1];
                }
                else if ((args[i] == "--port" || args[i] == "-p") && i + 1 < args.Length)
                {
                    int.TryParse(args[i + 1], out serverPort);
                }
                else if ((args[i] == "--clients" || args[i] == "-c") && i + 1 < args.Length)
                {
                    int.TryParse(args[i + 1], out totalClients);
                }
                else if ((args[i] == "--rooms" || args[i] == "-r") && i + 1 < args.Length)
                {
                    int.TryParse(args[i + 1], out totalRooms);
                }
                else if ((args[i] == "--duration" || args[i] == "-d") && i + 1 < args.Length)
                {
                    int.TryParse(args[i + 1], out durationSeconds);
                }
                else if ((args[i] == "--packet-interval" || args[i] == "-i") && i + 1 < args.Length)
                {
                    int.TryParse(args[i + 1], out packetIntervalMs);
                }
                else if ((args[i] == "--payload-size" || args[i] == "-size") && i + 1 < args.Length)
                {
                    int.TryParse(args[i + 1], out payloadSize);
                }
                else if ((args[i] == "--stagger" || args[i] == "-g") && i + 1 < args.Length)
                {
                    int.TryParse(args[i + 1], out staggerMs);
                }
                else if (args[i] == "--loopback")
                {
                    isLoopbackMode = true;
                }
                else if ((args[i] == "--protocol" || args[i] == "-proto") && i + 1 < args.Length)
                {
                    protocol = args[i + 1];
                }
                else if (args[i] == "--room" && i + 1 < args.Length)
                {
                    int.TryParse(args[i + 1], out loopbackRoomId);
                }
                else if (args[i] == "--clientid" && i + 1 < args.Length)
                {
                    int.TryParse(args[i + 1], out loopbackClientId);
                }
            }

            if (isLoopbackMode)
            {
                // In loopback mode, if port was not overridden, use the default port for that protocol
                if (serverPort == 50005) // default UDP port
                {
                    if (protocol.Equals("tcp", StringComparison.OrdinalIgnoreCase))
                        serverPort = 50006;
                    else if (protocol.Equals("ws", StringComparison.OrdinalIgnoreCase) || protocol.Equals("websocket", StringComparison.OrdinalIgnoreCase))
                        serverPort = 50007;
                }

                Console.WriteLine("==================================================");
                Console.WriteLine("          LOOPBACK VOICE TEST CLIENT              ");
                Console.WriteLine("==================================================");
                Console.WriteLine($"Server:             {serverIp}:{serverPort}");
                Console.WriteLine($"Protocol:           {protocol.ToUpper()}");
                Console.WriteLine($"Client ID:          {loopbackClientId}");
                Console.WriteLine($"Room ID:            {loopbackRoomId}");
                Console.WriteLine($"Duration:           {durationSeconds} seconds");
                Console.WriteLine("--------------------------------------------------");

                var loopbackClient = new LoopbackVoiceClient(
                    clientId: loopbackClientId,
                    roomId: loopbackRoomId,
                    serverIp: serverIp,
                    serverPort: serverPort,
                    protocol: protocol
                );

                var loopbackCts = new CancellationTokenSource();
                Console.CancelKeyPress += (s, e) =>
                {
                    e.Cancel = true;
                    loopbackCts.Cancel();
                };

                bool started = await loopbackClient.StartAsync(loopbackCts.Token);
                if (!started)
                {
                    Console.WriteLine("[Loopback] Failed to start loopback client.");
                    return;
                }

                Console.WriteLine("[Loopback] Running loopback. Press Ctrl+C to stop.");
                try
                {
                    await Task.Delay(durationSeconds * 1000, loopbackCts.Token);
                }
                catch (TaskCanceledException)
                {
                }
                finally
                {
                    Console.WriteLine("[Loopback] Stopping loopback client...");
                    loopbackClient.Stop();
                    Console.WriteLine("[Loopback] Stopped.");
                }
                return;
            }

            Console.WriteLine("==================================================");
            Console.WriteLine("        MOCK CLIENT VOICE LOAD SIMULATOR          ");
            Console.WriteLine("==================================================");
            Console.WriteLine($"Server:             {serverIp}:{serverPort}");
            Console.WriteLine($"Total Clients:      {totalClients}");
            Console.WriteLine($"Total Rooms:        {totalRooms}");
            Console.WriteLine($"Duration:           {durationSeconds} seconds");
            Console.WriteLine($"Packet Interval:    {packetIntervalMs} ms");
            Console.WriteLine($"Payload Size:       {payloadSize} bytes");
            Console.WriteLine($"Startup Stagger:    {staggerMs} ms");
            Console.WriteLine("--------------------------------------------------");
            Console.WriteLine("Connecting clients...");

            var globalHistogram = new LatencyHistogram();
            var clients = new List<MockVoiceClient>();
            var cts = new CancellationTokenSource();

            int connectedCount = 0;
            int failedCount = 0;

            for (int i = 0; i < totalClients; i++)
            {
                int clientId = 1000 + i;
                int roomId = 100 + (i % totalRooms);

                var client = new MockVoiceClient(
                    clientId: clientId,
                    roomId: roomId,
                    serverIp: serverIp,
                    serverPort: serverPort,
                    packetIntervalMs: packetIntervalMs,
                    payloadSize: payloadSize,
                    globalHistogram: globalHistogram,
                    globalCounters: _globalCounters
                );

                clients.Add(client);

                if (await client.StartAsync(cts.Token))
                {
                    connectedCount++;
                }
                else
                {
                    failedCount++;
                }

                if (staggerMs > 0 && i < totalClients - 1)
                {
                    await Task.Delay(staggerMs);
                }
            }

            Console.WriteLine($"Startup finished: {connectedCount} clients connected, {failedCount} failed.");
            await Task.Delay(1000);

            if (!Console.IsOutputRedirected)
            {
                Console.Clear();
            }
            var simulationStopwatch = Stopwatch.StartNew();
            var lastReportStopwatch = Stopwatch.StartNew();

            long prevSent = 0;
            long prevRecv = 0;

            while (simulationStopwatch.Elapsed.TotalSeconds < durationSeconds && !cts.IsCancellationRequested)
            {
                await Task.Delay(1000);

                double elapsedSec = lastReportStopwatch.Elapsed.TotalSeconds;
                lastReportStopwatch.Restart();

                long sentNow = Volatile.Read(ref _globalCounters.GlobalPacketsSent);
                long recvNow = Volatile.Read(ref _globalCounters.GlobalPacketsReceived);

                double ppsSent = (sentNow - prevSent) / elapsedSec;
                double ppsRecv = (recvNow - prevRecv) / elapsedSec;

                prevSent = sentNow;
                prevRecv = recvNow;

                // Calculate reliability from trackers
                long totalExpected = 0;
                long totalLost = 0;
                long totalOutOfOrder = 0;
                long totalDuplicates = 0;

                foreach (var client in clients)
                {
                    foreach (var trackerKvp in client.PeerTrackers)
                    {
                        var tracker = trackerKvp.Value;
                        totalExpected += tracker.ExpectedCount;
                        totalLost += tracker.LostCount;
                        totalOutOfOrder += tracker.OutOfOrderCount;
                        totalDuplicates += tracker.DuplicateCount;
                    }
                }

                // If lost count is negative (can happen transiently due to sequence windows shifting), clamp to 0
                if (totalLost < 0) totalLost = 0;

                double lossRate = totalExpected > 0 ? (double)totalLost / totalExpected * 100.0 : 0.0;

                double avgLat = globalHistogram.GetAverage();
                double p50 = globalHistogram.GetPercentile(50);
                double p90 = globalHistogram.GetPercentile(90);
                double p95 = globalHistogram.GetPercentile(95);
                double p99 = globalHistogram.GetPercentile(99);

                if (!Console.IsOutputRedirected)
                {
                    Console.SetCursorPosition(0, 0);
                }
                else
                {
                    Console.WriteLine($"\n--- Telemetry Report [Elapsed: {simulationStopwatch.Elapsed:hh\\:mm\\:ss}] ---");
                }
                Console.WriteLine("======================================================================");
                Console.WriteLine("MOCK CLIENT LOAD SIMULATOR - RUNNING");
                Console.WriteLine("======================================================================");
                Console.WriteLine($"Duration:         {simulationStopwatch.Elapsed:hh\\:mm\\:ss} / {TimeSpan.FromSeconds(durationSeconds):hh\\:mm\\:ss} | Active: {connectedCount}/{totalClients} (Rooms: {totalRooms})");
                Console.WriteLine("----------------------------------------------------------------------");
                Console.WriteLine("TRAFFIC STATISTICS:");
                Console.WriteLine($"  Packets Sent:     {sentNow:N0} ({ppsSent:F0} pps) | Bytes Sent:     {Volatile.Read(ref _globalCounters.GlobalBytesSent) / (1024.0 * 1024.0):F2} MB");
                Console.WriteLine($"  Packets Received: {recvNow:N0} ({ppsRecv:F0} pps) | Bytes Received: {Volatile.Read(ref _globalCounters.GlobalBytesReceived) / (1024.0 * 1024.0):F2} MB");
                Console.WriteLine("----------------------------------------------------------------------");
                Console.WriteLine("LATENCY TELEMETRY (One-Way):");
                Console.WriteLine($"  Average Latency:  {avgLat:F1} ms");
                Console.WriteLine($"  Percentiles:      P50: {p50:F1} ms | P90: {p90:F1} ms | P95: {p95:F1} ms | P99: {p99:F1} ms");
                Console.WriteLine("----------------------------------------------------------------------");
                Console.WriteLine("RELIABILITY TELEMETRY:");
                Console.WriteLine($"  Packet Loss Rate: {lossRate:F3}% ({totalLost:N0} lost / {totalExpected:N0} expected)");
                Console.WriteLine($"  Out-of-Order:     {totalOutOfOrder:N0} packets");
                Console.WriteLine($"  Duplicates:       {totalDuplicates:N0} packets");
                Console.WriteLine("======================================================================");
            }

            Console.WriteLine("\n[Simulator] Stopping simulation...");
            cts.Cancel();

            foreach (var client in clients)
            {
                client.Stop();
            }

            // Print final summary
            if (!Console.IsOutputRedirected)
            {
                Console.Clear();
            }
            long finalSent = Volatile.Read(ref _globalCounters.GlobalPacketsSent);
            long finalRecv = Volatile.Read(ref _globalCounters.GlobalPacketsReceived);

            long finalExpected = 0;
            long finalLost = 0;
            long finalOutOfOrder = 0;
            long finalDuplicates = 0;

            foreach (var client in clients)
            {
                foreach (var trackerKvp in client.PeerTrackers)
                {
                    var tracker = trackerKvp.Value;
                    finalExpected += tracker.ExpectedCount;
                    finalLost += tracker.LostCount;
                    finalOutOfOrder += tracker.OutOfOrderCount;
                    finalDuplicates += tracker.DuplicateCount;
                }
            }
            if (finalLost < 0) finalLost = 0;
            double finalLossRate = finalExpected > 0 ? (double)finalLost / finalExpected * 100.0 : 0.0;

            Console.WriteLine("======================================================================");
            Console.WriteLine("MOCK CLIENT LOAD SIMULATOR - SIMULATION FINISHED");
            Console.WriteLine("======================================================================");
            Console.WriteLine($"Total Duration:     {simulationStopwatch.Elapsed:hh\\:mm\\:ss}");
            Console.WriteLine($"Active Clients:     {connectedCount}/{totalClients} connected");
            Console.WriteLine("----------------------------------------------------------------------");
            Console.WriteLine("TRAFFIC SUMMARY:");
            Console.WriteLine($"  Total Packets Sent:     {finalSent:N0} ({Volatile.Read(ref _globalCounters.GlobalBytesSent) / (1024.0 * 1024.0):F2} MB)");
            Console.WriteLine($"  Total Packets Received: {finalRecv:N0} ({Volatile.Read(ref _globalCounters.GlobalBytesReceived) / (1024.0 * 1024.0):F2} MB)");
            Console.WriteLine("----------------------------------------------------------------------");
            Console.WriteLine("LATENCY SUMMARY (One-Way):");
            Console.WriteLine($"  Average Latency:        {globalHistogram.GetAverage():F1} ms");
            Console.WriteLine($"  Percentiles:            P50: {globalHistogram.GetPercentile(50):F1} ms | P90: {globalHistogram.GetPercentile(90):F1} ms | P95: {globalHistogram.GetPercentile(95):F1} ms | P99: {globalHistogram.GetPercentile(99):F1} ms");
            Console.WriteLine("----------------------------------------------------------------------");
            Console.WriteLine("RELIABILITY SUMMARY:");
            Console.WriteLine($"  Packet Loss Rate:       {finalLossRate:F3}% ({finalLost:N0} lost / {finalExpected:N0} expected)");
            Console.WriteLine($"  Out-of-Order Packets:   {finalOutOfOrder:N0}");
            Console.WriteLine($"  Duplicate Packets:      {finalDuplicates:N0}");
            Console.WriteLine("======================================================================");
        }
    }
}
