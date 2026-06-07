# Handoff Report: Room Management & Broadcasting Design

This handoff report summarizes the findings, recommendations, and logic chain for the Room/Session Management and Broadcasting architecture for Milestone 3.

---

## 1. Observation
The following file paths, structures, and specifications were investigated:
1. **`i:/projects/voice chat/PROJECT.md`** (Lines 29-39):
   ```
   ### Client ↔ Server Packets
   - Audio packets must be lightweight.
   - Format:
     - Bytes 0-3: Packet Type (e.g., 1=Handshake, 2=RoomJoin, 3=Audio)
     - Bytes 4-7: Room ID (int32)
     - Bytes 8-11: Client ID (int32)
     - Bytes 12-15: Sequence Number (uint32)
     - Bytes 16-23: Send Timestamp (int64, milliseconds)
     - Bytes 24-27: Payload length (int32)
     - Bytes 28+: Opus-encoded audio payload
   ```
2. **`i:/projects/voice chat/.agents/sub_orch_ms3_server/SCOPE.md`** (Lines 4-10, 20-31):
   - Confirmed class roles: `UdpVoiceServer`, `RoomManager`, `VoiceRoom`, `ClientSession`.
   - Confirmed the 28-byte packet header structure containing the packet types: `1` (Handshake), `2` (RoomJoin), `3` (Audio).
3. **`i:/projects/voice chat/voice chat.sln`** (Lines 1-9):
   - Confirmed the solution file exists but has no active project definitions yet, meaning the codebase is at the boilerplate/design phase.
4. **`i:/projects/voice chat/TEST_INFRA.md`** (Lines 8-15):
   - Confirmed test requirements including Room Session Management (F1), Audio Packet Broadcasting (F2), and Latency & Diagnostic Metrics (F4).

---

## 2. Logic Chain
1. **Low-Latency / Zero Allocation Requirement**: Since audio packets are streamed at high frequency (approx. 50 packets/sec per client), any heap allocations on the hot path will trigger C# garbage collection (GC) sweeps, introducing latency spikes.
2. **Struct-based Enumeration**: Therefore, we designed `VoiceRoom` to iterate over its `ConcurrentDictionary` using `foreach (var kvp in _clients)`. Because this compiles down to a struct-based enumerator rather than a heap-allocated `IEnumerable` reference, it results in **zero heap allocation**.
3. **Zero-Copy Broadcast**: Since the server does not modify packet headers (it preserves sender ID, timestamp, and sequence number), the server can broadcast by passing the original packet byte array directly to `Socket.SendTo` without allocating new arrays or performing array copies.
4. **Fast O(1) Lookups**: Every packet contains the `ClientId` in bytes 8-11. The server can extract this in O(1) time using `BinaryPrimitives.ReadInt32LittleEndian` and retrieve the corresponding `ClientSession` using a `ConcurrentDictionary<int, ClientSession>` map, bypassing endpoint lookups and avoiding costly string parsing.
5. **Port-Roaming Support**: If a client's port changes (NAT roaming), mapping the session by `ClientId` allows the server to update the registered endpoint dynamically upon receiving a valid Handshake, rather than breaking the connection.
6. **Thread-Safe Heartbeat / Pruning**: Pruning inactive clients concurrently with active streaming introduces race conditions. To resolve this, `RoomManager.UnregisterClient` verifies the timeout and performs a double-check rollback on the session map: if `LastActivityTicks` was updated just before deletion, the deletion is rolled back.

---

## 3. Caveats
- No implementation has been committed yet as this is a read-only architectural investigation.
- Real-world performance under high-concurrency packet loss needs to be verified using the load generator simulator (`MockClient`) as specified in the testing plan.
- The design assumes UDP socket buffer sizing (e.g., 4MB) will be configured correctly on the underlying host OS to avoid OS-level buffer exhaustion under load.

---

## 4. Conclusion
We recommend implementing:
1. `ClientSession` with atomic `LastActivityTicks` updates (`Interlocked`).
2. `VoiceRoom` using `ConcurrentDictionary` to store clients and a lock-free, zero-allocation broadcasting loop using struct enumerators.
3. `RoomManager` managing mapping collections between client IDs and rooms, supporting NAT port-roaming.
4. A non-blocking packet routing loop utilizing `BinaryPrimitives` and `ReadOnlySpan<byte>`.
5. A `SessionPruningService` utilizing a background `PeriodicTimer` (every 5 seconds) and a double-check rollback mechanism.

---

## 5. Verification Method
1. **Inspection**: Verify that `analysis.md` in the working directory `i:/projects/voice chat/.agents/teamwork_preview_explorer_ms3_2/` outlines the detailed class definitions and routing logic.
2. **Implementation Verification**: Once the implementer writes the code:
   - Compile the server: `dotnet build` from the workspace root.
   - Run the E2E test harness from the `Tests/` directory.
   - Run the `MockClient` simulation with 50 concurrent connections to verify that no memory is allocated on the hot path (using dotMemory or CLR profile counters for Gen-0 GC collections).
