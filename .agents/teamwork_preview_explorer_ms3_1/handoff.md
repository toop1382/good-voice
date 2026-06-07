# Handoff Report: Socket and Protocol Design for Network Transport & Room Server

## 1. Observation
- The project solution `voice chat.sln` was examined and found to be an empty configuration:
  ```
  Microsoft Visual Studio Solution File, Format Version 12.00
  Global
  	GlobalSection(SolutionConfigurationPlatforms) = preSolution
  		Debug|Any CPU = Debug|Any CPU
  		Release|Any CPU = Release|Any CPU
  	EndGlobalSection
  EndGlobal
  ```
- The network packet layout is specified in `PROJECT.md` at lines 29-39:
  ```
  30: ### Client ↔ Server Packets
  31: - Audio packets must be lightweight.
  32: - Format:
  33:   - Bytes 0-3: Packet Type (e.g., 1=Handshake, 2=RoomJoin, 3=Audio)
  34:   - Bytes 4-7: Room ID (int32)
  35:   - Bytes 8-11: Client ID (int32)
  36:   - Bytes 12-15: Sequence Number (uint32)
  37:   - Bytes 16-23: Send Timestamp (int64, milliseconds)
  38:   - Bytes 24-27: Payload length (int32)
  39:   - Bytes 28+: Opus-encoded audio payload
  ```
- The architectural components are specified in `.agents/sub_orch_ms3_server/SCOPE.md` at lines 4-8:
  ```
  4: - **Server Module (`Server/`)**: A .NET 8.0 Console Application.
  5:   - `UdpVoiceServer`: Manages the socket binding, asynchronous packet receiving loop, and packet parsing/routing.
  6:   - `RoomManager`: Coordinates voice rooms (`VoiceRoom` objects), client sessions, room joins, and broadcasting.
  7:   - `ClientSession`: Holds remote endpoint, client ID, room association, and last activity timestamp (for heartbeat/pruning).
  ```

## 2. Logic Chain
1. Since the Unity Client needs to deserialize packets and the .NET 8.0 Console Server / Mock Client need the same logic, standardizing on a shared class library targeting `.NET Standard 2.1` prevents code duplication and layout mismatch between the C# code in Unity and the server.
2. High-performance socket server loops require avoiding allocations on the hot path (packet arrival). Under high load, `UdpClient.ReceiveAsync` allocates a new `byte[]` and a `UdpReceiveResult` object per packet, causing garbage collection overhead. Utilizing raw `Socket.ReceiveFromAsync` with `Memory<byte>` rents buffers from `ArrayPool<byte>` to achieve zero-allocation receives.
3. Decoupling packet receiving from processing using a lock-free queue (`System.Threading.Channels`) ensures that blocking logic (like room list traversal or socket errors) doesn't delay subsequent socket receives, preventing OS buffer overflows.
4. Using `System.Buffers.Binary.BinaryPrimitives` ensures endian-safe deserialization/serialization, protecting against architecture mismatch (e.g., x86/ARM64 cross-communication).
5. Implementing packet length checks, max-size boundaries, source endpoint validation, and zombie session cleanup guarantees resiliency against malicious packets, session hijacking, socket crashes, and client resource leaks.

## 3. Caveats
- **Opus Integration Details**: The actual integration of the native Opus codec wrapper (Milestone 1) is assumed to take place in the client and mock-client; this analysis assumes the payload bytes are already Opus-encoded and handles them as opaque slices of memory.
- **KCP Protocol Layer**: While KCP is listed as a potential transport protocol in `PROJECT.md`, raw UDP socket transport is recommended first to validate base functionality. Adding a KCP reliability layer can be done on top of the designed UDP listener loop.
- **Network Byte Order**: The layout does not specify whether to use Big Endian (network byte order) or Little Endian. This design recommends Little Endian explicitly for all fields to match native representation on current systems, but the implementation could easily be adjusted to Big Endian if needed, as long as it is done consistently via `BinaryPrimitives`.

## 4. Conclusion
We recommend:
- Structuring the solution with three subprojects (`Server`, `MockClient`, `Shared`).
- Implementing a decoupled UDP server loop using raw `Socket` and `Channel<InboundPacket>`.
- Using a `readonly struct` called `VoicePacketHeader` with explicit, endianness-aware serialization/deserialization.
- Using `ArrayPool<byte>.Shared` and `stackalloc` to guarantee a zero-heap-allocation hot path.
- Applying strict validations (minimum header size, payload length matching packet size, client endpoint verification).

## 5. Verification Method
1. **Tests Inspection**: Inspect the `Tests/` project's test files verifying packet serialization and validation edge cases.
2. **Mock Client Simulation**: Once the Server is implemented, use the `MockClient` to send malformed packets (e.g., truncated packet headers, mismatched payload lengths, spoofed Client IDs) and verify that the server discards them without throwing unhandled exceptions or leaking rented buffers.
3. **Continuous Running**: Verify that `dotnet test` or the custom test runner passes all network transport verification scenarios.
