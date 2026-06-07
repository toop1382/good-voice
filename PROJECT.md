# Project: Low-Latency Voice Chat System

## Architecture
- **Client**: Unity-based client. Audio capture uses platform-native APIs (WASAPI on Windows, `AudioRecord` via JNI on Android) to minimize latency. PCM audio is encoded using the native Opus library and transmitted via a low-latency network protocol. Received Opus packets are decoded to PCM and played back via a low-latency audio component.
- **Server**: .NET Console application handling room-based audio packet broadcasting. Uses UDP/KCP transport protocol for low-latency transport.
- **Network Protocol**: Custom packet framing containing Room ID, Client ID, Sequence Number, Timestamp, and encoded Opus payload.

## Code Layout
- `Server/`: .NET Console Application for the Voice Chat Server.
- `Client/`: Unity Project folder.
  - `Assets/Plugins/Windows/`: Windows Opus DLL, WASAPI DLL (if native).
  - `Assets/Plugins/Android/`: Android Opus `.so` (arm64-v8a, armeabi-v7a), Android JNI AAR/Java files.
  - `Assets/Scripts/Codec/`: Opus encoding/decoding wrappers.
  - `Assets/Scripts/Audio/`: WASAPI/Android audio capture and custom playback scripts.
  - `Assets/Scripts/Network/`: UDP/KCP network client.
  - `Assets/Scripts/Diagnostics/`: Profiling and Diagnostics UI.
- `MockClient/`: .NET client simulator for load and scalability testing.
- `Tests/`: E2E test scripts and test runner.

## Milestones
| # | Name | Scope | Dependencies | Status | Conv ID |
|---|------|-------|-------------|--------|---------|
| 1 | Native Opus Codec Wrapper | Integrate native Opus libraries (Windows x86_64, Android arm64/armeabi) and write C# wrapper | None | IN_PROGRESS | fec73b90-f3d7-45e1-9229-a03f3657c5c2 |
| 2 | Native Audio Recording & Playback | Implement native WASAPI capture on Windows, Android AudioRecord JNI on Android, and low-latency playback | M1 | PLANNED | |
| 3 | Network Transport & Room Server | Implement low-latency UDP/KCP server & client transport with room management | None | IN_PROGRESS | 98e3da8d-a700-42a9-807e-a06ca0c4705d |
| 4 | Client-Server Integration & UI | Connect capture/encoding/net/decoding/playback pipeline with diagnostics UI | M2, M3 | PLANNED | |
| 5 | E2E Testing & Hardening | Pass all E2E tests, then run white-box adversarial testing | M4, E2E Test Suite | PLANNED | |

## Interface Contracts
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

### Codec API
- `IOpusEncoder`: `int Encode(short[] pcm, byte[] output)`
- `IOpusDecoder`: `int Decode(byte[] packet, short[] output)`

### Audio Capture API
- `IAudioRecorder`: `event Action<short[]> OnAudioFrameCaptured; void StartRecording(); void StopRecording();`
