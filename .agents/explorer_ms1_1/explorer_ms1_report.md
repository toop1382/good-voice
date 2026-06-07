# E2E Testing Milestone 1: Explorer Report

This report outlines the dotnet environment, plan, architecture, and exact code design for the MockClient library/app and E2E Test project for the Low-Latency Voice Chat System.

---

## 1. System Environment Inspection

The workspace environment was inspected to determine the available .NET SDKs and runtime environment.

### Command Output: `dotnet --list-sdks`
```
8.0.401 [C:\Program Files\dotnet\sdk]
10.0.100 [C:\Program Files\dotnet\sdk]
```

### Assessment
* The machine has **.NET 8.0** and **.NET 10.0** SDKs installed.
* We select **.NET 8.0** as the target framework (`net8.0`) since it is the current Long-Term Support (LTS) release aligned with the server project requirements.

---

## 2. Project and Directory Layout

We propose the following directory structure in the root directory `i:/projects/voice chat`:

```
i:/projects/voice chat/
├── voice chat.sln               # Solution file including Server, MockClient, and Tests
├── PROJECT.md                   # System specifications and interface contracts
├── TEST_INFRA.md                # E2E test philosophy and Feature inventory
├── Server/                      # Server module console app (Milestone 3)
├── MockClient/                  # MockClient CLI and library
│   ├── MockClient.csproj        # .NET 8.0 console/library project definition
│   ├── Program.cs               # CLI entrypoint for standalone load testing
│   ├── Models/
│   │   └── VoicePacket.cs       # Packet framing & serialization
│   ├── Network/
│   │   └── MockClientUdpSocket.cs# Custom UDP socket logic & degradation
│   ├── Diagnostics/
│   │   └── MetricTracker.cs     # Sequence, RTT, jitter, and loss tracking
│   └── MockClient.cs            # Mock client facade and control API
└── Tests/                       # E2E Test Suite
    ├── Tests.csproj             # xUnit test project referencing MockClient
    ├── Fixtures/
    │   └── ServerFixture.cs     # Lifecycle fixture to manage the Server process
    └── Scenarios/
        ├── StandardChatSessionTests.cs # Standard chat room tests
        ├── MultiRoomBroadcastingTests.cs# Room isolation tests
        ├── HighLoadRoomTests.cs         # Scalability and concurrent client tests
        └── NetworkDegradationTests.cs   # Jitter, packet loss, and latency tolerance tests
```

---

## 3. Interface & Framing Protocol Specification

The custom packet framing format is defined as a fixed **28-byte header** followed by a variable-length Opus-encoded payload. To ensure low-latency and allocation-free networking, we utilize C# `ReadOnlySpan<byte>` and `Span<byte>` along with endian-aware `System.Buffers.Binary.BinaryPrimitives`.

### Binary Header Structure
* **Bytes 0-3 (int32)**: `PacketType` (1 = Handshake, 2 = RoomJoin, 3 = Audio)
* **Bytes 4-7 (int32)**: `RoomId`
* **Bytes 8-11 (int32)**: `ClientId`
* **Bytes 12-15 (uint32)**: `SequenceNumber`
* **Bytes 16-23 (int64)**: `SendTimestamp` (POSIX milliseconds)
* **Bytes 24-27 (int32)**: `PayloadLength`
* **Bytes 28+**: Encoded payload data

---

## 4. Project File Contents (.csproj)

### `MockClient/MockClient.csproj`
```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>

</Project>
```

### `Tests/Tests.csproj`
```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.9.0" />
    <PackageReference Include="xunit" Version="2.6.6" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.5.7">
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
      <PrivateAssets>all</PrivateAssets>
    </PackageReference>
    <PackageReference Include="coverlet.collector" Version="6.0.0">
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
      <PrivateAssets>all</PrivateAssets>
    </PackageReference>
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\MockClient\MockClient.csproj" />
  </ItemGroup>

</Project>
```

---

## 5. Architectural & Implementation Designs

### A. Packet Serialization: `VoicePacket.cs`
Provides high-efficiency serialization without allocating heap memory, converting packets to/from byte spans.

```csharp
namespace MockClient.Models;

using System;
using System.Buffers.Binary;

public enum PacketType : int
{
    Handshake = 1,
    RoomJoin = 2,
    Audio = 3
}

public struct VoicePacket
{
    public PacketType Type { get; set; }
    public int RoomId { get; set; }
    public int ClientId { get; set; }
    public uint SequenceNumber { get; set; }
    public long SendTimestamp { get; set; }
    public int PayloadLength { get; set; }
    public byte[] Payload { get; set; }

    public const int HeaderSize = 28;

    public void Serialize(Span<byte> destination)
    {
        if (destination.Length < HeaderSize + PayloadLength)
            throw new ArgumentException("Destination buffer too small.");

        BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(0, 4), (int)Type);
        BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(4, 4), RoomId);
        BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(8, 4), ClientId);
        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(12, 4), SequenceNumber);
        BinaryPrimitives.WriteInt64LittleEndian(destination.Slice(16, 8), SendTimestamp);
        BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(24, 4), PayloadLength);
        
        if (PayloadLength > 0 && Payload != null)
        {
            Payload.AsSpan(0, PayloadLength).CopyTo(destination.Slice(HeaderSize, PayloadLength));
        }
    }

    public static VoicePacket Deserialize(ReadOnlySpan<byte> source)
    {
        if (source.Length < HeaderSize)
            throw new ArgumentException("Source buffer smaller than header size.");

        var packet = new VoicePacket();
        packet.Type = (PacketType)BinaryPrimitives.ReadInt32LittleEndian(source.Slice(0, 4));
        packet.RoomId = BinaryPrimitives.ReadInt32LittleEndian(source.Slice(4, 4));
        packet.ClientId = BinaryPrimitives.ReadInt32LittleEndian(source.Slice(8, 4));
        packet.SequenceNumber = BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(12, 4));
        packet.SendTimestamp = BinaryPrimitives.ReadInt64LittleEndian(source.Slice(16, 8));
        packet.PayloadLength = BinaryPrimitives.ReadInt32LittleEndian(source.Slice(24, 4));

        if (packet.PayloadLength > 0)
        {
            if (source.Length < HeaderSize + packet.PayloadLength)
                throw new ArgumentException("Source buffer does not contain full payload.");
            packet.Payload = source.Slice(HeaderSize, packet.PayloadLength).ToArray();
        }
        else
        {
            packet.Payload = Array.Empty<byte>();
        }

        return packet;
    }
}
```

### B. Network Diagnostic Calculations: `MetricTracker.cs`
Implements math and algorithms to calculate packet metrics on the receiver.

1. **Jitter Calculation**: Follows RFC 3550 (RTP).
   * Transit time $D_i = R_i - S_i$ (where $R_i$ is local receipt time and $S_i$ is packet send timestamp).
   * Transit variance $d_i = D_i - D_{i-1}$.
   * Smoothed jitter $J_i = J_{i-1} + (|d_i| - J_{i-1}) / 16$.
2. **Packet Loss**:
   * Expected packets $E = \text{Sequence}_{\text{max}} - \text{Sequence}_{\text{min}} + 1$.
   * Lost Packets $L = E - \text{UniqueReceived}$.
   * Loss rate $\% = (L / E) * 100$.
3. **Out-of-order & Duplicates**:
   * Packets arriving with sequence number $S < S_{\text{last}}$ increment the out-of-order count.
   * Duplicate packets are checked via a unique sequence set.

```csharp
namespace MockClient.Diagnostics;

using System;
using System.Collections.Generic;
using MockClient.Models;

public class MetricTracker
{
    private readonly object _lock = new object();
    private uint? _lastSequenceNumber;
    private int _receivedPacketsCount;
    private int _outOfOrderCount;
    private int _duplicateCount;
    
    private double _averageRtt;
    private int _rttSampleCount;

    private double _jitter;
    private double? _lastTransitTime;

    private uint _minSequenceReceived = uint.MaxValue;
    private uint _maxSequenceReceived = uint.MinValue;
    private readonly HashSet<uint> _receivedSequences = new HashSet<uint>();

    public void RecordPacketReceived(uint sequenceNumber, long sendTimestamp, long receiveTimestamp)
    {
        lock (_lock)
        {
            _receivedPacketsCount++;

            if (sequenceNumber < _minSequenceReceived) _minSequenceReceived = sequenceNumber;
            if (sequenceNumber > _maxSequenceReceived) _maxSequenceReceived = sequenceNumber;

            bool isNew = _receivedSequences.Add(sequenceNumber);
            if (!isNew)
            {
                _duplicateCount++;
                return;
            }

            if (_lastSequenceNumber.HasValue && sequenceNumber < _lastSequenceNumber.Value)
            {
                _outOfOrderCount++;
            }
            _lastSequenceNumber = sequenceNumber;

            // Jitter calculation (RFC 3550)
            double currentTransitTime = receiveTimestamp - sendTimestamp;
            if (_lastTransitTime.HasValue)
            {
                double diff = Math.Abs(currentTransitTime - _lastTransitTime.Value);
                _jitter = _jitter + (diff - _jitter) / 16.0;
            }
            _lastTransitTime = currentTransitTime;
        }
    }

    public void RecordRttSample(long rttMs)
    {
        lock (_lock)
        {
            _rttSampleCount++;
            _averageRtt = _averageRtt + (rttMs - _averageRtt) / _rttSampleCount;
        }
    }

    public ClientMetrics GetMetrics()
    {
        lock (_lock)
        {
            double packetLossRate = 0;
            int lostPackets = 0;
            int totalExpected = 0;

            if (_receivedSequences.Count > 0)
            {
                totalExpected = (int)(_maxSequenceReceived - _minSequenceReceived + 1);
                lostPackets = totalExpected - _receivedSequences.Count;
                if (lostPackets < 0) lostPackets = 0;
                packetLossRate = totalExpected > 0 ? (double)lostPackets / totalExpected * 100.0 : 0;
            }

            return new ClientMetrics
            {
                ReceivedPackets = _receivedPacketsCount,
                DuplicatePackets = _duplicateCount,
                OutOfOrderPackets = _outOfOrderCount,
                LostPackets = lostPackets,
                PacketLossRate = packetLossRate,
                AverageRttMs = _averageRtt,
                JitterMs = _jitter
            };
        }
    }
}

public struct ClientMetrics
{
    public int ReceivedPackets { get; set; }
    public int DuplicatePackets { get; set; }
    public int OutOfOrderPackets { get; set; }
    public int LostPackets { get; set; }
    public double PacketLossRate { get; set; }
    public double AverageRttMs { get; set; }
    public double JitterMs { get; set; }
}
```

### C. UDP Communication and Network Simulation: `MockClientUdpSocket.cs`
Manages the socket receive loop using `ReceiveFromAsync`. Supports simulated network degradation directly within the code (packet drops and artificial latencies), bypassing OS-level tools.

```csharp
namespace MockClient.Network;

using System;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using MockClient.Models;

public class DegradationConfig
{
    public double PacketLossRate { get; set; } // 0.0 to 1.0 (e.g. 0.10 for 10% packet drop)
    public int ArtificialDelayMs { get; set; }  // Extra delay added to sends in milliseconds
}

public class MockClientUdpSocket : IDisposable
{
    private readonly Socket _socket;
    private readonly IPEndPoint _serverEndPoint;
    private readonly byte[] _receiveBuffer;
    private bool _isRunning;
    private Task? _receiveTask;
    private readonly Random _random = new Random();

    public DegradationConfig? Degradation { get; set; }

    public event Action<VoicePacket, IPEndPoint>? OnPacketReceived;
    public event Action<Exception>? OnError;

    public MockClientUdpSocket(IPEndPoint serverEndPoint)
    {
        _serverEndPoint = serverEndPoint;
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        _socket.Bind(new IPEndPoint(IPAddress.Any, 0));
        _receiveBuffer = new byte[65535];
    }

    public void Start()
    {
        if (_isRunning) return;
        _isRunning = true;
        _receiveTask = Task.Run(ReceiveLoopAsync);
    }

    public async Task SendPacketAsync(VoicePacket packet)
    {
        // Simulate packet loss on send
        if (Degradation != null && Degradation.PacketLossRate > 0)
        {
            if (_random.NextDouble() < Degradation.PacketLossRate)
            {
                // Drop packet silently
                return;
            }
        }

        // Simulate latency delay
        if (Degradation != null && Degradation.ArtificialDelayMs > 0)
        {
            await Task.Delay(Degradation.ArtificialDelayMs);
        }

        var buffer = new byte[VoicePacket.HeaderSize + packet.PayloadLength];
        packet.Serialize(buffer);
        await _socket.SendToAsync(new ArraySegment<byte>(buffer), SocketFlags.None, _serverEndPoint);
    }

    private async Task ReceiveLoopAsync()
    {
        var remoteEP = (EndPoint)new IPEndPoint(IPAddress.Any, 0);
        while (_isRunning)
        {
            try
            {
                var result = await _socket.ReceiveFromAsync(new ArraySegment<byte>(_receiveBuffer), SocketFlags.None, remoteEP);
                
                // Simulate loss on receipt
                if (Degradation != null && Degradation.PacketLossRate > 0)
                {
                    if (_random.NextDouble() < Degradation.PacketLossRate)
                    {
                        continue; // Drop packet
                    }
                }

                if (result.ReceivedBytes >= VoicePacket.HeaderSize)
                {
                    var receivedSpan = _receiveBuffer.AsSpan(0, result.ReceivedBytes);
                    var packet = VoicePacket.Deserialize(receivedSpan);
                    OnPacketReceived?.Invoke(packet, (IPEndPoint)result.RemoteEndPoint);
                }
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.OperationAborted)
            {
                break;
            }
            catch (Exception ex)
            {
                if (_isRunning)
                {
                    OnError?.Invoke(ex);
                }
            }
        }
    }

    public void Dispose()
    {
        _isRunning = false;
        _socket.Dispose();
    }
}
```

### D. Mock Client Wrapper: `MockClient.cs`
Ties together network and telemetry tracking. Exposes asynchronous control APIs.

```csharp
namespace MockClient;

using System;
using System.Diagnostics;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using MockClient.Models;
using MockClient.Network;
using MockClient.Diagnostics;

public class MockClient : IDisposable
{
    private readonly int _clientId;
    private readonly MockClientUdpSocket _socket;
    private readonly MetricTracker _tracker;
    private int _roomId;
    private uint _sequenceCounter;
    private TaskCompletionSource<bool>? _handshakeTcs;
    private CancellationTokenSource? _streamingCts;

    public int ClientId => _clientId;
    public int RoomId => _roomId;
    public MetricTracker Tracker => _tracker;
    
    public DegradationConfig? Degradation
    {
        get => _socket.Degradation;
        set => _socket.Degradation = value;
    }

    public MockClient(int clientId, IPEndPoint serverEndPoint)
    {
        _clientId = clientId;
        _socket = new MockClientUdpSocket(serverEndPoint);
        _tracker = new MetricTracker();
        _socket.OnPacketReceived += HandlePacketReceived;
    }

    public async Task<bool> ConnectAsync(int maxRetries = 3, int timeoutMs = 1500)
    {
        _socket.Start();
        
        for (int retry = 0; retry < maxRetries; retry++)
        {
            _handshakeTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var stopwatch = Stopwatch.StartNew();

            var handshakePacket = new VoicePacket
            {
                Type = PacketType.Handshake,
                RoomId = 0,
                ClientId = _clientId,
                SequenceNumber = 0,
                SendTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                PayloadLength = 0,
                Payload = Array.Empty<byte>()
            };

            await _socket.SendPacketAsync(handshakePacket);

            var delayTask = Task.Delay(timeoutMs);
            var completedTask = await Task.WhenAny(_handshakeTcs.Task, delayTask);

            if (completedTask == _handshakeTcs.Task && await _handshakeTcs.Task)
            {
                stopwatch.Stop();
                _tracker.RecordRttSample(stopwatch.ElapsedMilliseconds);
                return true;
            }
        }

        return false;
    }

    public async Task JoinRoomAsync(int roomId)
    {
        _roomId = roomId;
        var joinPacket = new VoicePacket
        {
            Type = PacketType.RoomJoin,
            RoomId = roomId,
            ClientId = _clientId,
            SequenceNumber = ++_sequenceCounter,
            SendTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            PayloadLength = 0,
            Payload = Array.Empty<byte>()
        };
        await _socket.SendPacketAsync(joinPacket);
    }

    public void StartStreamingAudio(int intervalMs = 20, int payloadSize = 120)
    {
        _streamingCts?.Cancel();
        _streamingCts = new CancellationTokenSource();
        var token = _streamingCts.Token;

        Task.Run(async () =>
        {
            var random = new Random();
            var dummyPayload = new byte[payloadSize];
            random.NextBytes(dummyPayload);

            while (!token.IsCancellationRequested)
            {
                try
                {
                    var audioPacket = new VoicePacket
                    {
                        Type = PacketType.Audio,
                        RoomId = _roomId,
                        ClientId = _clientId,
                        SequenceNumber = ++_sequenceCounter,
                        SendTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        PayloadLength = payloadSize,
                        Payload = dummyPayload
                    };

                    await _socket.SendPacketAsync(audioPacket);
                    await Task.Delay(intervalMs, token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    // Ignore transient network errors during streaming
                }
            }
        }, token);
    }

    public void StopStreamingAudio()
    {
        _streamingCts?.Cancel();
    }

    private void HandlePacketReceived(VoicePacket packet, IPEndPoint sender)
    {
        long receiveTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        if (packet.Type == PacketType.Handshake)
        {
            if (packet.ClientId == _clientId)
            {
                _handshakeTcs?.TrySetResult(true);
            }
        }
        else if (packet.Type == PacketType.Audio)
        {
            _tracker.RecordPacketReceived(packet.SequenceNumber, packet.SendTimestamp, receiveTimestamp);
        }
    }

    public void Dispose()
    {
        _streamingCts?.Cancel();
        _socket.Dispose();
    }
}
```

---

## 6. E2E Test Runner & Fixture Design

To run automated E2E tests, the tests must spin up the server executable on a background process, perform client workflows, verify performance metrics, and terminate the server cleanly.

### Server Process Lifetime Management: `ServerFixture.cs`
An xUnit fixture that manages building and starting the Server console app before tests, and killing it on completion.

```csharp
namespace Tests.Fixtures;

using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Threading;

public class ServerFixture : IDisposable
{
    public Process? ServerProcess { get; private set; }
    public IPEndPoint ServerEndPoint { get; } = new IPEndPoint(IPAddress.Loopback, 5000);

    public ServerFixture()
    {
        // Locate server project path relative to output directory (Tests/bin/Debug/net8.0)
        string serverProjDir = Path.Combine(AppContext.BaseDirectory, "../../../../Server");
        string serverExeName = "Server.exe"; // Windows executable

        string serverPath = Path.Combine(serverProjDir, "bin/Debug/net8.0", serverExeName);

        if (!File.Exists(serverPath))
        {
            // Build the server project programmatically to ensure the executable exists
            var buildInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"build \"{Path.Combine(serverProjDir, "Server.csproj")}\" -c Debug",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true
            };
            using var buildProcess = Process.Start(buildInfo);
            buildProcess?.WaitForExit();
        }

        if (!File.Exists(serverPath))
        {
            throw new FileNotFoundException($"Server executable not found at {serverPath}. Compile the Server project first.");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = serverPath,
            Arguments = "--port 5000",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        ServerProcess = Process.Start(startInfo) ?? throw new Exception("Failed to start server process.");
        
        // Suppress buffer hangs by reading streams
        ServerProcess.OutputDataReceived += (s, e) => { };
        ServerProcess.ErrorDataReceived += (s, e) => { };
        ServerProcess.BeginOutputReadLine();
        ServerProcess.BeginErrorReadLine();

        // Give socket binding time
        Thread.Sleep(1000);
    }

    public void Dispose()
    {
        if (ServerProcess != null && !ServerProcess.HasExited)
        {
            try
            {
                ServerProcess.Kill();
                ServerProcess.WaitForExit(3000);
            }
            catch
            {
                // Suppress shutdown exceptions
            }
            finally
            {
                ServerProcess.Dispose();
            }
        }
    }
}
```

---

## 7. Sample Scenario Case

### Standard Chat Session: `StandardChatSessionTests.cs`
Verifies basic connect, join room, and audio routing. Asserts packet delivery and minimal local latency (RTT < 10ms, packet loss = 0%).

```csharp
namespace Tests.Scenarios;

using System.Threading.Tasks;
using Tests.Fixtures;
using MockClient;
using Xunit;

public class StandardChatSessionTests : IClassFixture<ServerFixture>
{
    private readonly ServerFixture _fixture;

    public StandardChatSessionTests(ServerFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task TwoClients_InSameRoom_ShouldBroadcastAudioSuccessfully()
    {
        // Arrange
        int roomId = 101;
        using var client1 = new MockClient(1, _fixture.ServerEndPoint);
        using var client2 = new MockClient(2, _fixture.ServerEndPoint);

        // Act & Assert (Connect both)
        bool c1Connected = await client1.ConnectAsync();
        bool c2Connected = await client2.ConnectAsync();
        Assert.True(c1Connected, "Client 1 handshake failed.");
        Assert.True(c2Connected, "Client 2 handshake failed.");

        // Join the same room
        await client1.JoinRoomAsync(roomId);
        await client2.JoinRoomAsync(roomId);
        await Task.Delay(200); // Wait for room join processing

        // Client 1 streams dummy audio
        client1.StartStreamingAudio(intervalMs: 20, payloadSize: 120);
        await Task.Delay(2000); // Stream for 2 seconds
        client1.StopStreamingAudio();

        // Assert metrics on receiver
        var client2Metrics = client2.Tracker.GetMetrics();
        
        Assert.True(client2Metrics.ReceivedPackets > 80, $"Expected > 80 packets, received: {client2Metrics.ReceivedPackets}");
        Assert.Equal(0, client2Metrics.LostPackets);
        Assert.Equal(0.0, client2Metrics.PacketLossRate);
        Assert.True(client2Metrics.AverageRttMs < 50.0, $"Average RTT too high: {client2Metrics.AverageRttMs}ms");
        Assert.True(client2Metrics.JitterMs < 5.0, $"Jitter too high: {client2Metrics.JitterMs}ms");
    }
}
```

---

## 8. Verification Strategy & Commands

To verify environment compatibility and execute tests:
1. **Restore dependencies**:
   ```bash
   dotnet restore "voice chat.sln"
   ```
2. **Compile workspace**:
   ```bash
   dotnet build "voice chat.sln" -c Debug
   ```
3. **Execute E2E Tests**:
   ```bash
   dotnet test "Tests/Tests.csproj" --logger "console;verbosity=normal"
   ```
4. **Inspect CLI manual runner**:
   ```bash
   dotnet run --project "MockClient/MockClient.csproj" -- --client-id 123 --room-id 555 --server 127.0.0.1:5000 --duration 15
   ```
