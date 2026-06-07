# Handoff Report — Explorer 3 (Mock Client and Load Simulator Designer)

## 1. Observation
We observed the following definitions in the workspace files:
* **Packet Layout**: In `PROJECT.md` (lines 29-39) and `.agents/sub_orch_ms3_server/SCOPE.md` (lines 19-32), the Client ↔ Server packet protocol is defined as:
  ```
  Bytes 0-3:   Packet Type (int32)
  Bytes 4-7:   Room ID (int32)
  Bytes 8-11:  Client ID (int32)
  Bytes 12-15: Sequence Number (uint32)
  Bytes 16-23: Send Timestamp (int64, milliseconds)
  Bytes 24-27: Payload length (int32)
  Bytes 28+:   Opus-encoded audio payload / handshake metadata
  ```
* **Project Specifications**: `.agents/sub_orch_ms3_server/SCOPE.md` (lines 7-10) describes the Mock Client:
  > Mock Client (`MockClient/`): A .NET 8.0 Console Application used to simulate multiple client connections, send dummy audio streams, measure latency/packet loss, and perform load/scalability testing.
* **Test Architecture**: `TEST_INFRA.md` (lines 16-22) specifies that `MockClient/` serves as a client simulation library and CLI tools, which will be integrated into the test runner in `Tests/`.

## 2. Logic Chain
1. Since the `MockClient` must simulate multiple concurrent clients, and the server relies on unique UDP endpoints (IP + Port) to identify clients in rooms, each simulated client must use its own ephemeral UDP socket.
2. To scale to hundreds or thousands of clients on a single test machine without hitting OS thread exhaustion, a task-based asynchronous programming model (`async/await`) using IOCP-multiplexed sockets (`Socket.ReceiveFromAsync` and `Socket.SendToAsync`) is required instead of spawning a dedicated thread per client.
3. Audio packets must be sent exactly every 20ms. However, standard OS timer resolutions on Windows default to ~15.6ms, meaning `Task.Delay(20)` yields highly jittery delay periods. To ensure exact 20ms intervals (50 pps), a hybrid delay + spin-wait timer (using `Stopwatch`) must be used.
4. One-way transit time is critical for audio, but local clocks may drift between machine endpoints. Measuring this accurately requires clock offset normalization. A 3-way NTP-style handshake exchange of timestamps ($T_0, S_0, S_1, T_1$) allows calculating the clock offset ($\theta$) and the correct one-way transit latency.
5. In a high-throughput UDP packet stream, packet loss, duplicate, and out-of-order packet tracking must run with zero heap allocations to prevent GC latency spikes. A 64-bit sliding window bitmask provides a low-overhead, lock-free method to track packet arrival history per stream.
6. A lock-free histogram utilizing atomic bucket counters allows high-frequency latency tracking without synchronization locks, and percentiles can be calculated in $O(1)$ time.

## 3. Caveats
* **Single-Machine Simulation Bias**: If the mock client and server are run on the same physical machine, network-level delay and jitter will be near-zero. Simulated latency will mostly reflect server scheduling and queuing. E2E physical network simulation requires running the Mock Client on a separate machine or using network simulation tools (like `tc-netem` on Linux or Clumsy on Windows).
* **NTP Error Margins**: The clock offset calculation assumes bidirectional symmetry in network routing time. While generally true on local networks, substantial routing asymmetry can cause a minor error in clock offset.

## 4. Conclusion
We have provided a comprehensive mock client architecture, connection sequence, high-precision timing mechanism, NTP-based telemetry synchronization, and zero-allocation packet trackers. These recommendations are written to `.agents/teamwork_preview_explorer_ms3_3/analysis.md` and are ready for implementation.

## 5. Verification Method
To verify the implementation of the Mock Client and the Server:
1. **Compilation**: Compile the entire solution in Release mode:
   ```powershell
   dotnet build -c Release
   ```
2. **Launch Server**: Start the server:
   ```powershell
   dotnet run --project Server --port 5000
   ```
3. **Execution**: Run the Mock Client simulation CLI:
   ```powershell
   dotnet run --project MockClient --server 127.0.0.1 --port 5000 --clients 50 --rooms 5 --duration 60
   ```
4. **Assertions**:
   * Verify that 100% of the 50 clients connect and complete the handshake.
   * Verify that the CLI stats dashboard displays dynamic, real-time metrics.
   * Verify that memory usage of the Server process remains stable, indicating no leaks.
   * Verify packet loss is $< 0.1\%$ and P95 latency is $< 5\text{ ms}$ on loopback.
