# Milestone 3 Analysis: UDP Socket Server & Binary Serialization Design

This report provides the design, architecture, and memory optimization guidelines for the low-latency UDP socket server and custom binary packet serialization for the Voice Chat System (Milestone 3).

---

## 1. Executive Summary
The proposed network transport architecture leverages raw .NET 8.0 `Socket` async APIs combined with `System.Buffers.Binary.BinaryPrimitives` and `System.Buffers.ArrayPool<byte>` to achieve a zero-allocation, high-throughput, low-latency packet processing pipeline. By avoiding `UdpClient` and implementing explicit validation checks, the server remains highly resilient under heavy concurrency and adversarial workloads.

---

## 2. Project Solution Structure (`voice chat.sln`)

The current `voice chat.sln` is configuration-only and does not contain project references. To implement Milestone 3, we recommend updating the solution to include three main projects: the **Server**, the **Mock Client**, and a **Shared Common Library**. A shared library prevents duplication of serialization/deserialization logic and ensures layout consistency across both the Server, Mock Client, and potentially the Unity Client.

### Recommended Directory Layout
```
voice chat/
├── voice chat.sln
├── Shared/
│   ├── Shared.csproj
│   ├── Models/
│   │   └── VoicePacket.cs
│   └── Serialization/
│       └── PacketSerializer.cs
├── Server/
│   ├── Server.csproj
│   ├── Program.cs
│   ├── Network/
│   │   ├── UdpVoiceServer.cs
│   │   └── ClientSession.cs
│   └── Rooms/
│       ├── RoomManager.cs
│       └── VoiceRoom.cs
├── MockClient/
│   ├── MockClient.csproj
│   ├── Program.cs
│   └── Network/
│       └── MockVoiceClient.cs
└── Tests/
    ├── Tests.csproj
    └── NetworkTests/
        └── PacketSerializerTests.cs
```

### Project File Definitions (.csproj)

#### `Shared/Shared.csproj`
Targeting `.NET Standard 2.1` ensures that the shared serialization logic can be consumed by both the .NET 8.0 Console Server/MockClient and the Unity Client (which natively supports .NET Standard 2.1 profiles).
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>netstandard2.1</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <LangVersion>latest</LangVersion>
  </PropertyGroup>
</Project>
```

#### `Server/Server.csproj`
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  
  <ItemGroup>
    <ProjectReference Include="..\Shared\Shared.csproj" />
  </ItemGroup>
</Project>
```

#### `MockClient/MockClient.csproj`
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\Shared\Shared.csproj" />
  </ItemGroup>
</Project>
```

---

## 3. Asynchronous UDP Listener Loop Design

### Comparison of .NET APIs
1. **`UdpClient.ReceiveAsync` (Not Recommended)**: Internally allocates a `UdpReceiveResult` object and a new `byte[]` array for every single incoming packet. In a high-throughput voice chat server processing thousands of packets per second (with frames every 20ms per client), this creates significant GC pressure and garbage collection pauses, which degrades voice quality.
2. **`Socket.ReceiveFromAsync` with `Memory<byte>` (Recommended)**: An async API that receives data directly into a preallocated or pooled slice of memory. Combined with `ValueTask`, it achieves zero-allocation packet reception.
3. **`Socket.ReceiveFromAsync` with `SocketAsyncEventArgs` (Alternative)**: The absolute highest-performance pattern. Reuses event argument objects and is ideal for extremely high-concurrency connections. However, the `Memory<byte>` task-based async loop in .NET 8 is highly optimized, cleaner to read, and sufficient for the workload target.

### Decoupled Processing Architecture
To prevent slow network operations or room broadcasting from blocking the socket reader loop, we propose a decoupled architecture using a lock-free queue (`System.Threading.Channels`). The reader loop pushes received byte buffers to the channel, and a pool of background worker tasks processes, validates, and broadcasts them.

```
                  +--------------------------------+
                  |  Socket.ReceiveFromAsync Loop  |
                  +---------------+----------------+
                                  |
                   ArrayPool.Rent | Buffer
                                  v
                  +---------------+----------------+
                  |      System.Threading.         |
                  |     Channels.Channel<T>        | (Lock-free, Zero-allocation queue)
                  +---------------+----------------+
                                  |
                                  | Dispatch (Worker Pool)
                                  v
                  +---------------+----------------+
                  |     Worker Task 1, 2, ... N    | (Processes, broadcasts, returns buffer)
                  +--------------------------------+
```

### C# Prototype for `UdpVoiceServer`
```csharp
using System;
using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Server.Network
{
    public class UdpVoiceServer
    {
        private readonly int _port;
        private readonly Socket _socket;
        private readonly Channel<InboundPacket> _packetChannel;
        private const int MaxPacketSize = 2048; // Safe upper bound for UDP/Ethernet MTU
        
        public UdpVoiceServer(int port)
        {
            _port = port;
            _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            
            // Optimize OS buffer to prevent drops during spikes
            _socket.ReceiveBufferSize = 1024 * 1024; // 1 MB
            _socket.SendBufferSize = 1024 * 1024;    // 1 MB

            // Configure channel: SingleReader = false (multi-worker), SingleWriter = true (single receive loop)
            var channelOptions = new SingleConsumerUnboundedChannelOptions<InboundPacket>
            {
                SingleWriter = true,
                AllowSynchronousContinuations = false
            };
            _packetChannel = Channel.CreateUnbounded<InboundPacket>(channelOptions);
        }

        public void Start(CancellationToken cancellationToken)
        {
            _socket.Bind(new IPEndPoint(IPAddress.Any, _port));
            
            // Start the single receive loop
            _ = Task.Run(() => RunReceiveLoopAsync(cancellationToken), cancellationToken);

            // Start multiple worker processing loops (e.g., matching CPU core count)
            int workerCount = Environment.ProcessorCount;
            for (int i = 0; i < workerCount; i++)
            {
                _ = Task.Run(() => RunWorkerLoopAsync(cancellationToken), cancellationToken);
            }
        }

        private async Task RunReceiveLoopAsync(CancellationToken cancellationToken)
        {
            EndPoint remoteEP = new IPEndPoint(IPAddress.Any, 0);

            while (!cancellationToken.IsCancellationRequested)
            {
                // Rent a buffer from the Shared pool
                byte[] rawBuffer = ArrayPool<byte>.Shared.Rent(MaxPacketSize);
                Memory<byte> memory = new Memory<byte>(rawBuffer);

                try
                {
                    // Zero-allocation async receive
                    SocketReceiveFromResult result = await _socket.ReceiveFromAsync(
                        memory, 
                        SocketFlags.None, 
                        remoteEP, 
                        cancellationToken
                    );

                    var inboundPacket = new InboundPacket(rawBuffer, result.ReceivedBytes, result.RemoteEndPoint);
                    
                    // Enqueue to worker queue without blocking
                    if (!_packetChannel.Writer.TryWrite(inboundPacket))
                    {
                        // Fallback in case of closure
                        ArrayPool<byte>.Shared.Return(rawBuffer);
                    }
                }
                catch (SocketException)
                {
                    // Handle socket closed or temporary errors
                    ArrayPool<byte>.Shared.Return(rawBuffer);
                }
                catch (Exception)
                {
                    ArrayPool<byte>.Shared.Return(rawBuffer);
                }
            }
        }

        private async Task RunWorkerLoopAsync(CancellationToken cancellationToken)
        {
            var reader = _packetChannel.Reader;
            
            while (await reader.WaitToReadAsync(cancellationToken))
            {
                while (reader.TryRead(out var packet))
                {
                    try
                    {
                        ProcessIncomingPacket(packet);
                    }
                    finally
                    {
                        // MUST guarantee buffer return to prevent memory leaks
                        ArrayPool<byte>.Shared.Return(packet.RawBuffer);
                    }
                }
            }
        }

        private void ProcessIncomingPacket(in InboundPacket packet)
        {
            ReadOnlySpan<byte> packetSpan = packet.RawBuffer.AsSpan(0, packet.BytesReceived);
            
            // Validate & Route packet (Deser, room logic, broadcast...)
            // (See next sections for details)
        }
    }

    public readonly struct InboundPacket
    {
        public byte[] RawBuffer { get; }
        public int BytesReceived { get; }
        public EndPoint RemoteEndPoint { get; }

        public InboundPacket(byte[] rawBuffer, int bytesReceived, EndPoint remoteEndPoint)
        {
            RawBuffer = rawBuffer;
            BytesReceived = bytesReceived;
            RemoteEndPoint = remoteEndPoint;
        }
    }
}
```

---

## 4. Binary Packet Layout & Serialization Design

### Packet Mappings
The custom binary packet follows the byte ranges specified in `PROJECT.md` and `SCOPE.md`:

| Byte Range | Field | C# DataType | Purpose |
|---|---|---|---|
| **0 - 3** | Packet Type | `int32` | Protocol State (`1`=Handshake, `2`=RoomJoin, `3`=Audio) |
| **4 - 7** | Room ID | `int32` | Identification of the Voice Room |
| **8 - 11** | Client ID | `int32` | Identification of the client session |
| **12 - 15** | Sequence Number | `uint32` | Monotonically increasing ID for duplicate/out-of-order detection |
| **16 - 23** | Send Timestamp | `int64` | Time of creation in milliseconds |
| **24 - 27** | Payload Length | `int32` | Length ($N$) of the Opus payload |
| **28 - (28+$N$-1)** | Payload | `byte[]` | Opus-encoded audio payload or handshake/metadata |

Header size is **fixed at 28 bytes**.

### Memory Layout & Type Design
We use a `readonly struct` to represent the header to avoid allocation. 
To guarantee cross-platform support (such as Unity running on a mobile client interacting with a .NET Linux/Windows server), we explicitly serialize to **Little Endian** using `System.Buffers.Binary.BinaryPrimitives`.

```csharp
using System;
using System.Buffers.Binary;

namespace Shared.Models
{
    public readonly struct VoicePacketHeader
    {
        public const int HeaderSize = 28;

        public int PacketType { get; }
        public int RoomId { get; }
        public int ClientId { get; }
        public uint SequenceNumber { get; }
        public long SendTimestamp { get; }
        public int PayloadLength { get; }

        public VoicePacketHeader(int packetType, int roomId, int clientId, uint sequenceNumber, long sendTimestamp, int payloadLength)
        {
            PacketType = packetType;
            RoomId = roomId;
            ClientId = clientId;
            SequenceNumber = sequenceNumber;
            SendTimestamp = sendTimestamp;
            PayloadLength = payloadLength;
        }
    }
}
```

### Serialization Implementation

```csharp
using System;
using System.Buffers.Binary;
using Shared.Models;

namespace Shared.Serialization
{
    public static class PacketSerializer
    {
        /// <summary>
        /// Attempts to deserialize a header from a raw byte buffer.
        /// </summary>
        public static bool TryDeserializeHeader(ReadOnlySpan<byte> buffer, out VoicePacketHeader header)
        {
            header = default;
            if (buffer.Length < VoicePacketHeader.HeaderSize)
            {
                return false;
            }

            int packetType   = BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(0, 4));
            int roomId       = BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(4, 4));
            int clientId     = BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(8, 4));
            uint seqNumber   = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(12, 4));
            long timestamp   = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(16, 8));
            int payloadLen   = BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(24, 4));

            header = new VoicePacketHeader(packetType, roomId, clientId, seqNumber, timestamp, payloadLen);
            return true;
        }

        /// <summary>
        /// Attempts to serialize a packet header into the provided target buffer.
        /// </summary>
        public static bool TrySerializeHeader(in VoicePacketHeader header, Span<byte> target)
        {
            if (target.Length < VoicePacketHeader.HeaderSize)
            {
                return false;
            }

            BinaryPrimitives.WriteInt32LittleEndian(target.Slice(0, 4), header.PacketType);
            BinaryPrimitives.WriteInt32LittleEndian(target.Slice(4, 4), header.RoomId);
            BinaryPrimitives.WriteInt32LittleEndian(target.Slice(8, 4), header.ClientId);
            BinaryPrimitives.WriteUInt32LittleEndian(target.Slice(12, 4), header.SequenceNumber);
            BinaryPrimitives.WriteInt64LittleEndian(target.Slice(16, 8), header.SendTimestamp);
            BinaryPrimitives.WriteInt32LittleEndian(target.Slice(24, 4), header.PayloadLength);

            return true;
        }
    }
}
```

---

## 5. Memory Optimization Techniques

1. **ArrayPool Slice Tracking**: `ArrayPool<byte>.Shared.Rent(size)` returns an array that is **at least** the requested size, but it can be larger. Therefore, never use the `Length` property of the returned array directly for serialization or sockets. Always use `Span<byte>` or `Memory<byte>` slices configured to the exact packet size:
   ```csharp
   byte[] rented = ArrayPool<byte>.Shared.Rent(VoicePacketHeader.HeaderSize + payloadLength);
   Span<byte> exactBuffer = rented.AsSpan(0, VoicePacketHeader.HeaderSize + payloadLength);
   ```
2. **`stackalloc` for Handshakes**: For small responses or handshake acknowledgements where packet size is fixed and small (e.g. 28 bytes header, 0 bytes payload), allocate the buffer directly on the stack to bypass both the Heap and the `ArrayPool`:
   ```csharp
   Span<byte> handshakeAckBuffer = stackalloc byte[VoicePacketHeader.HeaderSize];
   PacketSerializer.TrySerializeHeader(new VoicePacketHeader(1, roomId, clientId, seq, time, 0), handshakeAckBuffer);
   socket.SendTo(handshakeAckBuffer, remoteEP);
   ```
3. **Optimized Broadcasting with contiguous copies**: In UDP broadcasting, we need to send the same packet payload with minor header variations (or the exact same packet) to multiple remote endpoints. 
   - Instead of allocating a new array per target endpoint, format a single contiguous byte array containing the header and payload.
   - Reuse this single buffer for sequential `SendToAsync` calls to each client in the room:
     ```csharp
     // Broadcast same buffer to N clients
     foreach (var clientSession in room.Clients)
     {
         if (clientSession.ClientId != senderId)
         {
             await socket.SendToAsync(contiguousMemoryBuffer, SocketFlags.None, clientSession.EndPoint);
         }
     }
     ```
   - Note: Using scatter-gather IO (`Socket.SendToAsync(IList<ArraySegment<byte>>...)`) is **not recommended** for small audio payloads because it forces the JIT to allocate list nodes and track multiple references, which performs worse than a fast, contiguous `Span.CopyTo` memory operation.

---

## 6. Network Edge Cases, Validation, and Robustness

### Critical Boundary Validations
Before processing any received datagram, the server must perform the following validation logic:

1. **Underflow Protection**: Reject any received UDP packet smaller than `28 bytes` (the minimum size required to contain a header).
2. **Payload Size Mismatch**: 
   - A malformed or malicious packet might set `PayloadLength` to a huge value, or a value larger than the actual received packet size minus header size:
     ```csharp
     int actualPayloadSize = receivedBytes - VoicePacketHeader.HeaderSize;
     if (header.PayloadLength < 0 || header.PayloadLength != actualPayloadSize)
     {
         // Corrupt or truncated packet, drop it immediately
         return;
     }
     ```
3. **Maximum Size Boundaries**: Opus audio packets rarely exceed `1275 bytes`. Set a hard limit on `PayloadLength` (e.g., 1024 or 1500 bytes) to prevent memory exhaust denial-of-service vectors. Discard any packets exceeding this threshold.

### Session Security & Protection
1. **Source Endpoint IP Spoofing**: Since UDP is connectionless, anyone can forge packets using a random client's ID. 
   - Keep a record of the client's `EndPoint` (IP and Port) upon successful `Handshake` and `RoomJoin`.
   - On every audio packet received, verify that `packet.RemoteEndPoint` strictly matches the registered endpoint for the packet's `ClientId`. If there is a mismatch, drop the packet and log a security warning.
2. **Idempotence**: A client may resend `Handshake` or `RoomJoin` packets due to network jitter or retries. The room manager must handle these requests idempotently (updating the endpoint if needed, but not allocating new resources or corrupting session history).

### Operational Resiliency
1. **SocketException Propagation**: The `Socket.SendToAsync` and `Socket.ReceiveFromAsync` calls will throw `SocketException` if a destination client's IP is unreachable (generating ICMP Port Unreachable errors). 
   - Ensure that `RunReceiveLoopAsync` handles `SocketException` gracefully inside a `try/catch` and continues the loop. A single bad client destination must never cause the listener loop to crash.
2. **Zombie Client Session Pruning**: Clients may disconnect abruptly (e.g. process termination, signal loss) without sending explicit leave requests.
   - Maintain a `LastActivityTimestamp` on all `ClientSession` objects.
   - Run a periodic background pruning task (e.g., every 5-10 seconds) that removes client sessions whose last activity exceeds a threshold (e.g., 15 seconds), freeing resources and notifying other users in the room.
3. **Out-of-Order Packets (Jitter)**: 
   - Track the highest sequence number received per client. 
   - If a packet arrives with a sequence number lower than the highest received minus a buffer threshold (jitter window), discard it as it represents an old late-arrival frame that would only introduce stutter if decoded/played.
