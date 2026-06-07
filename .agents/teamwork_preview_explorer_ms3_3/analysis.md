# Milestone 3: Mock Client & Load Simulator Design Analysis

## Executive Summary
This report recommends the design and implementation details for the **Mock Client load generator and verification system** for Milestone 3 (Network Transport & Room Server). The goal of the Mock Client (`MockClient`) is to simulate high-density room-based voice traffic to evaluate the .NET UDP voice server's throughput, scalability, latency, and stability under load.

### Key Recommendations
1. **Asynchronous Architecture**: Use task-based asynchronous I/O (`async/await`) leveraging .NET 8's socket APIs (`Socket.ReceiveFromAsync` and `Socket.SendToAsync`) to simulate thousands of concurrent clients on a single machine with minimal thread overhead.
2. **Precision Traffic Generation**: Employ a **hybrid delay + spin-wait timer** for each simulated client to achieve exact 20ms audio frame intervals (50 packets/sec) without falling victim to OS timer resolution limitations or CPU hogging.
3. **NTP-like Clock Synchronization**: Execute a 3-way clock-offset sync during the Handshake phase to enable precise end-to-end (one-way) latency calculations across different network nodes.
4. **Lock-Free Diagnostics**: Utilize a **Sliding Window Bitmask** for zero-allocation packet loss/reordering detection, combined with a **Lock-Free Histogram** for low-overhead, real-time percentile latency calculations.
5. **Robust Verification Plan**: Execute multi-phase stress testing targeting room broadcasting bottlenecks ($O(N^2)$ workload), connection scaling ("thundering herd"), and continuous execution to guarantee socket stability.

---

## 1. Console Application (`MockClient`) Design

The Mock Client will be implemented as a .NET 8.0 Console Application located in `MockClient/`. It acts as both a standalone CLI load testing tool and a reference library that can be imported by automated E2E test projects (located in `Tests/`).

### CLI Parameter Interface
The application should support the following CLI arguments (leveraging `System.CommandLine` or a simple custom parser):

| Option | Short | Type | Default | Description |
| :--- | :--- | :--- | :--- | :--- |
| `--server` | `-s` | `string` | `127.0.0.1` | The target server IP address. |
| `--port` | `-p` | `int` | `5000` | The target server UDP port. |
| `--clients` | `-c` | `int` | `10` | The total number of simulated clients. |
| `--rooms` | `-r` | `int` | `2` | The number of voice rooms to distribute clients into. |
| `--duration` | `-d` | `int` | `60` | Simulation runtime in seconds. |
| `--packet-interval` | `-i` | `int` | `20` | Audio packet send interval in milliseconds. |
| `--payload-size` | `-size`| `int` | `120` | Size of the dummy Opus audio payload in bytes. |
| `--stagger` | `-g` | `int` | `10` | Start-up stagger delay per client in milliseconds. |

### Concurrency and Threading Model
To simulate up to thousands of clients on a single machine, a **thread-per-client model is highly inefficient** and will fail due to OS thread exhaustion and context switching overhead. 

Instead, the Mock Client should utilize .NET's high-performance task-based asynchronous programming model:
* **Asynchronous Ephemeral Sockets**: Each simulated client instantiates its own `Socket` configured for UDP (`SocketType.Dgram`, `ProtocolType.Udp`) and bound to port `0` (allowing the OS to allocate an ephemeral port). This simulates independent endpoints.
* **Dual-Task Client Loop**: Each client spawns exactly two long-running tasks:
  1. **Send Loop Task**: Uses a high-precision periodic trigger to transmit UDP audio packets.
  2. **Receive Loop Task**: A continuous asynchronous read loop listening for broadcast packets from the server.
* **IOCP Multiplexing**: Under the hood, .NET multiplexes the socket tasks onto a small number of thread pool threads using Windows I/O Completion Ports (IOCP). This ensures near-zero overhead for idle or waiting tasks, maximizing scaling limits.

---

## 2. Traffic Generation & Audio Simulation

### Room Distribution Logic
Clients are distributed across rooms to simulate different density profiles. By default, client instances are distributed round-robin:
$$\text{RoomId} = \text{BaseRoomId} + (i \bmod R)$$
where $i$ is the client index, and $R$ is the total rooms. This guarantees a uniform distribution. 

To test edge-case broadcasting scaling, the CLI can also support a **cluster profile** (e.g. all clients in a single room) to maximize room broadcast workload ($O(N^2)$ packets).

### Connection State Machine
To coordinate execution and capture failures, each `SimulatedClient` executes a sequential state machine:

```
[Disconnected] 
      │
      ▼  (Send Handshake, Start Timeout Timer)
[Handshaking] 
      │
      ├─► (Timeout / Retry Exhausted) ──► [Failed]
      ▼  (Received Handshake Ack + Sync Clocks)
[Handshaked]
      │
      ▼  (Send RoomJoin)
[Streaming] ◄───► (Periodic Audio Send & UDP Packet Receive Loop)
      │
      ▼  (Simulation End / Cancel Token)
[Disconnected / Cleaned Up]
```

1. **Phase 1: Handshake (Type 1)**: Client sends packet with Type `1`. Starts a timeout of 1000ms. If no response is received, it retries up to 3 times. If all retries fail, it flags the client as `Failed`.
2. **Phase 2: Clock Sync**: The Handshake acknowledgment is used to synchronize the client's clock offset with the server (see Telemetry section).
3. **Phase 3: Room Join (Type 2)**: Client sends RoomJoin packet with its target `Room ID` and moves to the active state.
4. **Phase 4: Audio Streaming (Type 3)**: Starts the parallel Send/Receive tasks.

### High-Precision Audio Interval Timing
Standard .NET timer structures (`Task.Delay`, `System.Threading.Timer`) depend on the Windows OS system clock resolution (which defaults to 15.6ms). A `Task.Delay(20)` can drift significantly between 15ms and 31ms, causing substantial jitter in the simulated packet stream that does not represent real client capture behavior.

To achieve precise 20ms intervals without spinning a thread at 100% CPU, we recommend a **hybrid spin-delay scheduler**:
* Use `Task.Delay` for the majority of the wait time (e.g., if wait time > 4ms).
* Use a high-resolution `System.Diagnostics.Stopwatch` to spin-wait for the remaining fractional milliseconds.

```csharp
public async Task RunPreciseSendLoopAsync(Func<Task> sendPacketAction, int intervalMs, CancellationToken ct)
{
    var stopwatch = Stopwatch.StartNew();
    long intervalTicks = intervalMs * TimeSpan.TicksPerMillisecond;
    long nextTick = stopwatch.ElapsedTicks;

    while (!ct.IsCancellationRequested)
    {
        nextTick += intervalTicks;
        long currentTicks = stopwatch.ElapsedTicks;
        long waitTicks = nextTick - currentTicks;

        if (waitTicks > 0)
        {
            // Use Task.Delay for the coarse wait to yield the thread
            int delayMs = (int)(waitTicks / TimeSpan.TicksPerMillisecond);
            if (delayMs > 2)
            {
                await Task.Delay(delayMs - 1, ct);
            }

            // Spin-wait for the fine-grained remaining ticks
            while (stopwatch.ElapsedTicks < nextTick)
            {
                Thread.SpinWait(1);
            }
        }

        await sendPacketAction();
    }
}
```

---

## 3. Diagnostics & Metrics Gathering

### NTP-like Clock Synchronization
In audio streaming, measuring end-to-end one-way latency is critical. However, local machine clocks may drift or disagree. To solve this, the client and server should execute a simplified NTP synchronization during the handshake phase:
1. Client records local timestamp $T_0$ and sends a Handshake (Type 1) packet.
2. Server receives the packet at server time $S_0$, and immediately before transmitting the Handshake Ack, records server time $S_1$.
3. Server writes $S_0$ and $S_1$ as 64-bit integers into the handshake acknowledgment payload.
4. Client receives the Ack at local time $T_1$.
5. The Client calculates:
   * **Round-Trip Time (RTT)**: 
     $$\text{RTT} = (T_1 - T_0) - (S_1 - S_0)$$
   * **Client-to-Server Clock Offset ($\theta$)**: 
     $$\theta = \frac{(S_0 - T_0) + (S_1 - T_1)}{2}$$
6. For every subsequent audio packet sent, the client records its local send timestamp $T_s$.
7. When another client receives this packet at local time $T_r$, it normalizes both times using their respective clock offsets to find the true server-normalized one-way latency:
   $$\text{One-Way Latency} = (T_r + \theta_{\text{receiver}}) - (T_s + \theta_{\text{sender}})$$
   *Note: If all simulated clients run in the same process on a single machine, $\theta_{\text{receiver}} \approx \theta_{\text{sender}}$, reducing the formula to $T_r - T_s$. The NTP sync ensures the code is production-ready for multi-machine tests.*

### Zero-Allocation Sliding Window Packet Tracking
UDP is unreliable: packets can be lost, duplicated, or reordered. To detect these conditions per stream (each peer client's audio stream in the room) with zero heap allocations, we recommend a **64-bit circular bitmask sliding window**:

* **State**: Track `MaxSeq` (highest sequence number seen), `MinSeq` (initial sequence number), and `_bitmask` (a `ulong` where bit `0` represents `MaxSeq`, bit `1` represents `MaxSeq - 1`, etc.).
* **Duplicate Detection**: If a packet arrives with sequence $S \le \text{MaxSeq}$ and the bit at offset $\text{MaxSeq} - S$ is already set, it is marked as a **Duplicate**.
* **Late/Out-of-Order Arrival**: If the bit is not set, it is set to `1`, and marked as **Out-of-Order** (which corrects the loss count since this packet has now arrived).
* **Packet Loss Calculation**: At any timestamp, the expected packet count is:
  $$\text{Expected} = \text{MaxSeq} - \text{MinSeq} + 1$$
  $$\text{Lost} = \text{Expected} - \text{UniqueReceived}$$

```csharp
public class PacketTracker
{
    private uint _maxSeq;
    private uint _minSeq;
    private ulong _bitmask;
    private bool _initialized;

    public int ReceivedCount { get; private set; }
    public int OutOfOrderCount { get; private set; }
    public int DuplicateCount { get; private set; }
    public int TooLateCount { get; private set; }

    public int ExpectedCount => _initialized ? (int)(_maxSeq - _minSeq + 1) : 0;
    public int LostCount => ExpectedCount - ReceivedCount;
    public double LossRate => ExpectedCount > 0 ? (double)LostCount / ExpectedCount : 0.0;

    public void RecordPacket(uint seq)
    {
        if (!_initialized)
        {
            _maxSeq = seq;
            _minSeq = seq;
            _bitmask = 1UL;
            _initialized = true;
            ReceivedCount = 1;
            return;
        }

        if (seq > _maxSeq)
        {
            uint diff = seq - _maxSeq;
            if (diff < 64)
            {
                _bitmask = (_bitmask << (int)diff) | 1UL;
            }
            else
            {
                _bitmask = 1UL; // Window jumped too far, reset mask
            }
            _maxSeq = seq;
            ReceivedCount++;
        }
        else if (seq < _minSeq)
        {
            _minSeq = seq;
            ReceivedCount++;
            OutOfOrderCount++;
        }
        else
        {
            uint diff = _maxSeq - seq;
            if (diff < 64)
            {
                ulong bit = 1UL << (int)diff;
                if ((_bitmask & bit) != 0)
                {
                    DuplicateCount++;
                }
                else
                {
                    _bitmask |= bit;
                    ReceivedCount++;
                    OutOfOrderCount++;
                }
            }
            else
            {
                // Too old to track in the 64-bit window
                TooLateCount++;
                ReceivedCount++;
                OutOfOrderCount++;
            }
        }
    }
}
```

### Lock-Free Metrics Collector (Histogram)
To collect latency telemetry from thousands of concurrent read loops without locks or synchronized list allocations, we recommend an **atomic bucketed histogram**:
* Maintain an array of atomic counters (`long[]`) representing latency buckets:
  * Buckets `0` to `200` represent latencies from `0ms` to `200ms` in `1ms` increments.
  * Bucket `201` represents the overflow (> 200ms).
* When a packet is received, calculate latency $L$, clamp to $[0, 201]$, and increment:
  `Interlocked.Increment(ref _latencyBuckets[Math.Clamp((int)L, 0, 201)]);`
* **Stats calculation**: Percentiles (50th, 90th, 95th, 99th) are computed in $O(1)$ time by accumulating the bucket counts sequentially until the target fraction of total samples is reached.

### Console UI Dashboard
Every second, a `StatsReporter` task clears the console screen (or uses ANSI escape codes `\x1b[H` to move the cursor to the top) and prints a structured, dynamic dashboard:

```
======================================================================
MOCK CLIENT LOAD SIMULATOR - RUNNING
======================================================================
Duration: 00:00:15 / 00:01:00 | Active Clients: 100/100 (Rooms: 5)
----------------------------------------------------------------------
TRAFFIC STATISTICS:
  Packets Sent:     75,000 (1,250 pps) | Bytes Sent:     9.00 MB (1.20 Mbps)
  Packets Received: 675,000 (11,250 pps) | Bytes Received: 81.00 MB (10.80 Mbps)
----------------------------------------------------------------------
LATENCY TELEMETRY:
  Average Latency:  2.3 ms  | Jitter:  0.8 ms
  Percentiles:      P50: 1.8 ms | P90: 3.2 ms | P95: 4.5 ms | P99: 12.1 ms
----------------------------------------------------------------------
RELIABILITY TELEMETRY:
  Packet Loss Rate: 0.04% (270 packets lost)
  Out-of-Order:     12 packets (0.00%)
  Duplicates:       0 packets (0.00%)
======================================================================
```

---

## 4. Step-by-Step Verification Plan

The verification plan ensures the voice chat server is scalable, performant, and stable.

### Step 1: Compilation and Setup
Build the entire solution in Release mode to enable compiler optimizations:
```powershell
# Navigate to the workspace root
dotnet build -c Release
```

### Step 2: Running the Server
Launch the Server process. By default, it binds to UDP port 5000:
```powershell
# Run the Server project
dotnet run --project Server/bin/Release/net8.0/Server.dll --port 5000
```

### Step 3: Baseline Throughput Test
Run the Mock Client with a low load to verify correctness:
```powershell
# Simulate 10 clients in 2 rooms for 30 seconds
dotnet run --project MockClient/bin/Release/net8.0/MockClient.dll --server 127.0.0.1 --port 5000 --clients 10 --rooms 2 --duration 30
```
* **Expected Result**: 
  * Handshake completion rate is 100%.
  * Packets Sent: $10 \text{ clients} \times 50 \text{ pps} \times 30 \text{ sec} = 15,000$ packets.
  * Packets Received: $2 \text{ rooms} \times (5 \text{ clients} \times 4 \text{ peers} \times 50 \text{ pps}) \times 30 \text{ sec} = 60,000$ packets.
  * Packet loss is 0.0% (on local loopback).
  * 95th percentile latency is under 5ms.

### Step 4: Scalability & Broadcast Stress Test
Verify the server under high broadcasting pressure. We simulate 100 clients clustered into a single room:
```powershell
# 100 clients in 1 room for 60 seconds (O(N^2) broadcast workload)
dotnet run --project MockClient/bin/Release/net8.0/MockClient.dll --server 127.0.0.1 --port 5000 --clients 100 --rooms 1 --duration 60
```
* **Metrics to watch**:
  * **Server CPU Utilization**: Verify the server utilizes multi-core routing without locking the UDP socket thread.
  * **Packet Throughput**: Server should broadcast $100 \times 99 \times 50 = 495,000$ packets/second.
  * **Dropped Packets**: Check if the client reports high packet loss. If loss exceeds 1%, the server's socket send buffers or thread pooling is bottlenecked.
  * **Sizing adjustments**: If packet loss occurs, recommend expanding OS socket buffers in the Server:
    ```csharp
    socket.SendBufferSize = 1024 * 1024 * 8; // 8MB Send Buffer
    socket.ReceiveBufferSize = 1024 * 1024 * 8; // 8MB Receive Buffer
    ```

### Step 5: Thundering Herd Connection Test
Verify server connection queuing under sudden spike load. Start 500 clients simultaneously without start-up stagger:
```powershell
# 500 clients with 0 stagger delay
dotnet run --project MockClient/bin/Release/net8.0/MockClient.dll --server 127.0.0.1 --port 5000 --clients 500 --stagger 0 --duration 30
```
* **Expected Result**: 
  * Server must queue and process the 500 concurrent handshakes without dropping connection requests.
  * If some clients timeout, the handshake retry logic should successfully establish the connection on the 2nd or 3rd attempt.

### Step 6: Long-Duration Stability Test
Run the simulation over an extended period to check for memory leaks, garbage collection (GC) pauses, and thread pool exhaustion.
```powershell
# Run 50 clients for 1 hour (3600 seconds)
dotnet run --project MockClient/bin/Release/net8.0/MockClient.dll --server 127.0.0.1 --port 5000 --clients 50 --duration 3600
```
* **Expected Result**:
  * **Memory Footprint**: Server memory (Private Bytes) should remain flat (e.g., using `dotnet-counters` or task manager). Any upward slope indicates room/client allocation leaks.
  * **GC Pauses**: Latency spikes (P99) should remain low. Frequent spikes indicate heap allocation churn (e.g., allocating `byte[]` for every received packet instead of using `ArrayPool<byte>`).
  * **Pruning and Disconnect Cleanup**: After the simulation completes, inspect the Server logs or run a small follow-up client connection to verify the server successfully pruned the 50 inactive client sessions and cleaned up the room dictionary.

---

## 5. Summary Matrix of Test Assertions

To automate the E2E verification, the test runner should assert the following thresholds:

| Test Case | Metric | Asserted Threshold | Failure Mode |
| :--- | :--- | :--- | :--- |
| **All Tests** | Connection Success | 100% clients connected | Socket binding error, Handshake timeout |
| **Baseline Load** | E2E Latency (P95) | $< 10\text{ ms}$ | High OS timer scheduler drift |
| **Baseline Load** | Packet Loss Rate | $< 0.1\%$ | Buffer overflow on socket |
| **Stress Load (100 Clients)** | E2E Latency (P99) | $< 50\text{ ms}$ | Lock contention, synchronous broadcast loop |
| **Stress Load (100 Clients)** | Packet Loss Rate | $< 1.0\%$ | Socket send queue overflowing |
| **Stability (1 Hour)** | Memory Growth | $< 5\text{ MB}$ variation | Memory leak in room dictionary or session maps |
| **Stability (1 Hour)** | Max Jitter | $< 15\text{ ms}$ | Heavy GC collections (blocking threads) |
