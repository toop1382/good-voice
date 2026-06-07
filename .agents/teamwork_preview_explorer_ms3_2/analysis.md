# Milestone 3: Room/Session Management and Broadcasting Design Analysis

This report outlines the recommended design and implementation details for **Room/Session Management** and **Broadcasting** for the low-latency .NET Voice Chat Server (Milestone 3).

---

## 1. Executive Summary
To achieve ultra-low latency and support high client concurrency, the server's Room and Session management must satisfy three core conditions:
1. **Zero-heap allocations in the hot path** (packet routing and broadcasting) to avoid garbage collection (GC) pause spikes.
2. **Lock-free read operations** during broadcasting using thread-safe, non-blocking collections.
3. **Robust synchronization** when client sessions are added, joined, or pruned concurrently.

This design introduces a high-performance architecture using `ConcurrentDictionary`, `ClientSession` with atomic `Interlocked` status updates, a lock-free room broadcasting routine utilizing struct enumerators, and a thread-safe background pruning worker based on .NET 8.0 `PeriodicTimer` with double-check race-condition protection.

---

## 2. Core Class Designs
The server room management is modeled via three main classes: `ClientSession`, `VoiceRoom`, and `RoomManager`.

### 2.1. `ClientSession`
Represents an active connection. It maps a logical client ID to a physical UDP network endpoint.
- **`LastActivityTicks`**: A 64-bit timestamp updated atomically on every received packet to prevent torn reads/writes on 32-bit platforms or under multi-threaded conditions.

```csharp
using System;
using System.Net;
using System.Threading;

namespace VoiceServer.Core
{
    public class ClientSession
    {
        public int ClientId { get; }
        
        // The physical UDP endpoint can change dynamically (NAT port roaming)
        private volatile IPEndPoint _endPoint;
        public IPEndPoint EndPoint
        {
            get => _endPoint;
            set => _endPoint = value;
        }

        private volatile int _roomId;
        public int RoomId
        {
            get => _roomId;
            set => _roomId = value;
        }

        private long _lastActivityTicks;
        public long LastActivityTicks
        {
            get => Interlocked.Read(ref _lastActivityTicks);
            set => Interlocked.Exchange(ref _lastActivityTicks, value);
        }

        public ClientSession(int clientId, IPEndPoint endPoint)
        {
            ClientId = clientId;
            _endPoint = endPoint ?? throw new ArgumentNullException(nameof(endPoint));
            _roomId = 0; // 0 indicates the client is not in any room
            _lastActivityTicks = DateTime.UtcNow.Ticks;
        }
    }
}
```

### 2.2. `VoiceRoom`
Represents a collection of client sessions currently listening to each other.
- **Heap-Allocation Minimization**: The room enumerates its clients directly via the `ConcurrentDictionary` struct enumerator, avoiding the allocations associated with `.Values` or `.Keys` copies.

```csharp
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;

namespace VoiceServer.Core
{
    public class VoiceRoom
    {
        public int RoomId { get; }
        private readonly ConcurrentDictionary<int, ClientSession> _clients = new();

        public VoiceRoom(int roomId)
        {
            RoomId = roomId;
        }

        // Property for reporting (allocates under the hood, use only in slow paths like diagnostics)
        public ICollection<ClientSession> Clients => _clients.Values;

        public int ClientCount => _clients.Count;

        public bool TryAdd(ClientSession session)
        {
            return _clients.TryAdd(session.ClientId, session);
        }

        public bool TryRemove(int clientId, out ClientSession session)
        {
            return _clients.TryRemove(clientId, out session);
        }

        public bool Contains(int clientId)
        {
            return _clients.ContainsKey(clientId);
        }

        /// <summary>
        /// Low-latency, allocation-free broadcast using the struct enumerator.
        /// </summary>
        public void Broadcast(byte[] packetBuffer, int length, int senderClientId, Socket serverSocket)
        {
            // Direct foreach on ConcurrentDictionary avoids heap allocations because it uses the custom struct Enumerator
            foreach (var kvp in _clients)
            {
                ClientSession client = kvp.Value;
                if (client.ClientId != senderClientId)
                {
                    try
                    {
                        // Direct synchronous SendTo is lock-free and returns immediately for UDP
                        serverSocket.SendTo(packetBuffer, 0, length, SocketFlags.None, client.EndPoint);
                    }
                    catch (SocketException)
                    {
                        // Network send failure to one client must not disrupt broadcasting to others
                        // Failing endpoints will eventually time out and get pruned
                    }
                }
            }
        }
    }
}
```

### 2.3. `RoomManager`
Coordinates room creation, client session joins, and teardowns.
- **Deadlock-Free Operations**: Avoids nested locks or complex state machines.
- **Automatic Room Cleanup**: Automatically removes rooms from the directory when the last participant departs to prevent memory leaks.

```csharp
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;

namespace VoiceServer.Core
{
    public class RoomManager
    {
        private readonly ConcurrentDictionary<int, VoiceRoom> _rooms = new();
        private readonly ConcurrentDictionary<int, ClientSession> _sessions = new();
        private readonly ConcurrentDictionary<IPEndPoint, ClientSession> _endpointSessions = new();

        public VoiceRoom GetOrCreateRoom(int roomId)
        {
            return _rooms.GetOrAdd(roomId, id => new VoiceRoom(id));
        }

        public bool TryGetRoom(int roomId, out VoiceRoom room)
        {
            return _rooms.TryGetValue(roomId, out room);
        }

        public ClientSession RegisterClient(int clientId, IPEndPoint endPoint)
        {
            // Thread-safe lock-free dictionary check
            if (_sessions.TryGetValue(clientId, out var session))
            {
                var oldEndPoint = session.EndPoint;
                if (!oldEndPoint.Equals(endPoint))
                {
                    // Endpoint changed (NAT roaming), update mappings safely
                    session.EndPoint = endPoint;
                    _endpointSessions.TryRemove(oldEndPoint, out _);
                    _endpointSessions[endPoint] = session;
                }
                session.LastActivityTicks = DateTime.UtcNow.Ticks;
                return session;
            }
            else
            {
                var newSession = new ClientSession(clientId, endPoint);
                if (_sessions.TryAdd(clientId, newSession))
                {
                    _endpointSessions[endPoint] = newSession;
                    return newSession;
                }
                // Recurse to resolve concurrent register race conditions
                return RegisterClient(clientId, endPoint);
            }
        }

        public bool JoinRoom(int clientId, int roomId, out VoiceRoom joinedRoom)
        {
            joinedRoom = null;
            if (!_sessions.TryGetValue(clientId, out var session))
            {
                return false;
            }

            // Remove from current room first if already joined
            if (session.RoomId != 0)
            {
                LeaveRoom(clientId);
            }

            joinedRoom = GetOrCreateRoom(roomId);
            if (joinedRoom.TryAdd(session))
            {
                session.RoomId = roomId;
                return true;
            }

            return false;
        }

        public bool LeaveRoom(int clientId)
        {
            if (!_sessions.TryGetValue(clientId, out var session))
            {
                return false;
            }

            int currentRoomId = session.RoomId;
            if (currentRoomId == 0)
            {
                return false; // Not in any room
            }

            if (_rooms.TryGetValue(currentRoomId, out var room))
            {
                room.TryRemove(clientId, out _);
                session.RoomId = 0;

                // Clean up empty rooms
                if (room.ClientCount == 0)
                {
                    _rooms.TryRemove(currentRoomId, out _);
                }
                return true;
            }

            return false;
        }

        public void UnregisterClient(int clientId, long currentTicks, long thresholdTicks)
        {
            if (_sessions.TryGetValue(clientId, out var session))
            {
                // Verify that the client is indeed timed out and hasn't sent a packet concurrently
                if (currentTicks - session.LastActivityTicks > thresholdTicks)
                {
                    if (_sessions.TryRemove(clientId, out var removedSession))
                    {
                        // Double-check race condition: client sent packet right before removal
                        if (currentTicks - removedSession.LastActivityTicks <= thresholdTicks)
                        {
                            _sessions.TryAdd(clientId, removedSession); // Rollback
                            return;
                        }

                        // Tear down session and free room slots
                        LeaveRoom(clientId);
                        _endpointSessions.TryRemove(removedSession.EndPoint, out _);
                    }
                }
            }
        }

        public bool TryGetSession(int clientId, out ClientSession session)
        {
            return _sessions.TryGetValue(clientId, out session);
        }

        public bool TryGetSessionByEndPoint(IPEndPoint endPoint, out ClientSession session)
        {
            return _endpointSessions.TryGetValue(endPoint, out session);
        }

        public ICollection<ClientSession> GetAllSessions()
        {
            return _sessions.Values;
        }
    }
}
```

---

## 3. Thread-Safe Collections Formulation
To support thousands of concurrent operations, the server avoids global application locks.

| Collection | Key Type | Value Type | Purpose | Concurrency Strategy |
|---|---|---|---|---|
| `_rooms` | `int` (Room ID) | `VoiceRoom` | Active room lookups | `ConcurrentDictionary` (Fine-grained locking for writes, lock-free for reads) |
| `_sessions` | `int` (Client ID) | `ClientSession` | Logical session management | `ConcurrentDictionary` (Fully lock-free reads on the packet hot path) |
| `_endpointSessions` | `IPEndPoint` | `ClientSession` | Physical connection mapping | `ConcurrentDictionary` (Used for connection checks, bypasses client headers when validating endpoint) |

### Key Optimization Details
1. **Direct Dictionary Iteration**: 
   Iterating directly over the `ConcurrentDictionary` using `foreach` utilizes its built-in struct enumerator. This operates entirely on the stack and incurs **zero heap allocation**.
2. **Read Path Lock-Free Isolation**: 
   The hot path (audio streaming) only reads from `_sessions` and `_rooms`. Reading from a `ConcurrentDictionary` is a non-blocking, lock-free operation in .NET, ensuring that broadcasting thread throughput remains bounded only by UDP network stack speed and CPU cache efficiency.
3. **Write Isolation**:
   Writes (joining, leaving, handshaking) are rare relative to audio packet streaming (once per room join vs. 50 packets per client per second). The dictionary's internal bucket locks isolate these writes, guaranteeing they do not block concurrent audio stream lookups.

---

## 4. Connection Handling and Packet Routing
The packet header consists of a 28-byte prefix containing protocol metadata, followed by the variable payload.

### 4.1. Packet Types and Routing State Machine
- **Handshake (Type 1)**: Client registers with its Client ID. The server creates or updates the connection endpoint.
- **RoomJoin (Type 2)**: Client requests room association. The server inserts the client into the room's participant collection.
- **Audio (Type 3)**: Core broadcasting payload. The server validates that the sender's endpoint matches its registered session, checks the room association, and broadcasts the frame.

```
       Client                             Server Receive Loop                     RoomManager / VoiceRoom
         |                                         |                                         |
         |------------- 1. Handshake ------------->|                                         |
         |                                         |-- Register/Update Session (O(1)) ------>|
         |<------------ Acknowledge (Type 1) ------|                                         |
         |                                         |                                         |
         |------------- 2. RoomJoin -------------->|                                         |
         |                                         |-- RoomManager.JoinRoom(RoomID) -------->|
         |<------------ Acknowledge (Type 2) ------|                                         |
         |                                         |                                         |
         |------------- 3. Audio Packet ---------->|                                         |
         |                                         |-- Validate Session & Endpoint --------->|
         |                                         |-- VoiceRoom.Broadcast ----------------->|
         |                                         |                               |         |
         |                                         |                               |-- Send to Client B --|
         |                                         |                               |-- Send to Client C --|
```

### 4.2. C# Packet Parser & Router Implementation
Using `ReadOnlySpan<byte>` and `BinaryPrimitives` allows buffer parsing without intermediate allocations or string conversions.

```csharp
using System;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace VoiceServer.Core
{
    public class PacketRouter
    {
        private readonly RoomManager _roomManager;

        public PacketRouter(RoomManager roomManager)
        {
            _roomManager = roomManager;
        }

        public void RoutePacket(byte[] buffer, int length, IPEndPoint senderEndPoint, Socket serverSocket)
        {
            if (length < 28) return; // Header must be at least 28 bytes

            // Read header fields via ReadOnlySpan (lock-free & allocation-free)
            ReadOnlySpan<byte> header = buffer.AsSpan(0, 28);
            int packetType = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(0, 4));
            int roomId = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(4, 4));
            int clientId = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(8, 4));
            uint sequenceNumber = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(12, 4));
            long sendTimestamp = BinaryPrimitives.ReadInt64LittleEndian(header.Slice(16, 8));
            int payloadLength = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(24, 4));

            if (length < 28 + payloadLength) return; // Discard corrupt packets

            switch (packetType)
            {
                case 1:
                    HandleHandshake(clientId, senderEndPoint, serverSocket);
                    break;
                case 2:
                    HandleRoomJoin(clientId, roomId, senderEndPoint, serverSocket);
                    break;
                case 3:
                    HandleAudio(clientId, roomId, buffer, length, senderEndPoint, serverSocket);
                    break;
            }
        }

        private void HandleHandshake(int clientId, IPEndPoint senderEndPoint, Socket serverSocket)
        {
            var session = _roomManager.RegisterClient(clientId, senderEndPoint);

            byte[] ack = new byte[28];
            BinaryPrimitives.WriteInt32LittleEndian(ack.AsSpan(0, 4), 1);
            BinaryPrimitives.WriteInt32LittleEndian(ack.AsSpan(4, 4), 0);
            BinaryPrimitives.WriteInt32LittleEndian(ack.AsSpan(8, 4), clientId);
            BinaryPrimitives.WriteUInt32LittleEndian(ack.AsSpan(12, 4), 0);
            BinaryPrimitives.WriteInt64LittleEndian(ack.AsSpan(16, 8), DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond);
            BinaryPrimitives.WriteInt32LittleEndian(ack.AsSpan(24, 4), 0);

            serverSocket.SendTo(ack, 0, ack.Length, SocketFlags.None, senderEndPoint);
        }

        private void HandleRoomJoin(int clientId, int roomId, IPEndPoint senderEndPoint, Socket serverSocket)
        {
            if (!_roomManager.TryGetSession(clientId, out var session) || !session.EndPoint.Equals(senderEndPoint))
            {
                return; // Session unauthorized or endpoint changed without handshake
            }

            session.LastActivityTicks = DateTime.UtcNow.Ticks;
            bool success = _roomManager.JoinRoom(clientId, roomId, out _);

            byte[] ack = new byte[28];
            BinaryPrimitives.WriteInt32LittleEndian(ack.AsSpan(0, 4), 2);
            BinaryPrimitives.WriteInt32LittleEndian(ack.AsSpan(4, 4), roomId);
            BinaryPrimitives.WriteInt32LittleEndian(ack.AsSpan(8, 4), clientId);
            BinaryPrimitives.WriteUInt32LittleEndian(ack.AsSpan(12, 4), 0);
            BinaryPrimitives.WriteInt64LittleEndian(ack.AsSpan(16, 8), DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond);
            BinaryPrimitives.WriteInt32LittleEndian(ack.AsSpan(24, 4), success ? 1 : 0); // 1 = success, 0 = fail

            serverSocket.SendTo(ack, 0, ack.Length, SocketFlags.None, senderEndPoint);
        }

        private void HandleAudio(int clientId, int roomId, byte[] buffer, int length, IPEndPoint senderEndPoint, Socket serverSocket)
        {
            if (!_roomManager.TryGetSession(clientId, out var session) || !session.EndPoint.Equals(senderEndPoint))
            {
                return; // Unauthorized endpoint, drop packet
            }

            if (session.RoomId != roomId)
            {
                return; // Mismatched room, drop packet
            }

            // Update heartbeat time
            session.LastActivityTicks = DateTime.UtcNow.Ticks;

            // Route audio packet
            if (_roomManager.TryGetRoom(roomId, out var room))
            {
                room.Broadcast(buffer, length, clientId, serverSocket);
            }
        }
    }
}
```

---

## 5. Broadcast Mechanism & Optimization
The broadcast loop handles high throughput, and must avoid delaying packets.

### 5.1. Lock-Free Execution & Concurrency
- Because `ConcurrentDictionary` allows lock-free read operations and struct enumeration, iterating the list of client endpoints in a room does not require acquiring a lock.
- If a client joins or leaves during broadcasting, the enumerator continues without crashing. It will broadcast to the remaining clients while bypassing the leaving client. In real-time voice applications, losing a single packet during a join/leave transition is expected and handled client-side.

### 5.2. Zero-Allocation Buffer Pass-Through
- **No Buffer Copies**: The incoming packet buffer is read into a pre-allocated array (or rented from `System.Buffers.ArrayPool<byte>`). The exact same buffer is passed to `Socket.SendTo`. 
- **Zero Header Modifications**: The server does not modify the packet header (it retains the original sender's ID, timestamp, and sequence number). This allows the receiver to calculate jitter and network latency, and permits the server to broadcast without copying or mutating the packet bytes.

### 5.3. Socket-Level Latency Tuning
To ensure UDP packet delivery without buffer congestion:
- **Configure Socket Buffers**: Set `Socket.SendBufferSize` and `Socket.ReceiveBufferSize` to high values (e.g., 4MB). This prevents packet drops at the OS level during traffic bursts.
- **Synchronous Send Mode**: For UDP transport, `Socket.SendTo` simply pushes data into the kernel send queue and returns immediately. Unlike TCP, it is non-blocking. This makes synchronous socket calls more CPU-efficient and lower latency than task-based asynchronous writes (`SendToAsync`), which incur queueing and async state-machine overhead.

---

## 6. Heartbeat and Client Pruning Mechanics
To prevent memory leaks from abandoned sessions, the server implements an automated pruning system.

### 6.1. Heartbeat Protocol
- Clients must send a keepalive packet (e.g., Handshake Type 1) at least every 3 seconds if they are not actively speaking.
- Every packet received from a client updates the `LastActivityTicks` timestamp.

### 6.2. Periodic Pruning Loop
The `SessionPruningService` runs as a background task. It scans all registered sessions every 5 seconds, checking if they have exceeded the 10-second inactivity limit.

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;

namespace VoiceServer.Core
{
    public class SessionPruningService
    {
        private readonly RoomManager _roomManager;
        private readonly TimeSpan _checkInterval = TimeSpan.FromSeconds(5);
        private readonly TimeSpan _timeoutThreshold = TimeSpan.FromSeconds(10);
        private CancellationTokenSource _cts;

        public SessionPruningService(RoomManager roomManager)
        {
            _roomManager = roomManager;
        }

        public void Start()
        {
            _cts = new CancellationTokenSource();
            Task.Run(() => PruneLoopAsync(_cts.Token));
        }

        public void Stop()
        {
            _cts?.Cancel();
        }

        private async Task PruneLoopAsync(CancellationToken cancellationToken)
        {
            using var timer = new PeriodicTimer(_checkInterval);
            try
            {
                while (await timer.WaitForNextTickAsync(cancellationToken))
                {
                    PruneInactiveClients();
                }
            }
            catch (OperationCanceledException)
            {
                // Graceful cancellation on server shutdown
            }
        }

        private void PruneInactiveClients()
        {
            long currentTicks = DateTime.UtcNow.Ticks;
            long thresholdTicks = _timeoutThreshold.Ticks;

            // Retrieve a snapshot of sessions
            var sessions = _roomManager.GetAllSessions();

            foreach (var session in sessions)
            {
                long elapsedTicks = currentTicks - session.LastActivityTicks;
                if (elapsedTicks > thresholdTicks)
                {
                    // Unregister with safety checks (rollback if packet received during prune check)
                    _roomManager.UnregisterClient(session.ClientId, currentTicks, thresholdTicks);
                }
            }
        }
    }
}
```

---

## 7. Verification Plan
To verify the room management and broadcasting components under Milestone 3:
1. **Mock Client Simulation**:
   - Spawn multiple mock clients in separate tasks that perform Handshake (Type 1), RoomJoin (Type 2), and start sending Type 3 Audio packets.
   - Assert that the server broadcasts each client's audio packets to other clients in the same room, and that no audio packets cross room boundaries.
2. **Port Roaming Verification**:
   - Connect a mock client, join a room, change its UDP socket local port (simulating NAT roaming), and send a new Handshake packet.
   - Assert that the server updates the client's endpoint map without losing the client's room association, and that subsequent broadcasts reach the client on the new port.
3. **Heartbeat and Pruning Test**:
   - Connect a client, stop transmitting all packets, and wait 12 seconds.
   - Assert that the client's session is removed from `RoomManager` collections, that the client is removed from the room, and that empty rooms are automatically garbage-collected.
4. **Memory Allocation Profiling**:
   - Execute a 60-second broadcast simulation with 50 clients in a room sending 50 packets/sec.
   - Monitor the GC Allocations. The Gen-0 GC collections should remain flat or near-zero, proving that the hot path avoids heap allocations.
