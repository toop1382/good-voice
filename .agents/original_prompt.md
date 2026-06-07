## 2026-06-06T20:39:13Z

Low latency, scalable, and optimized voice chat system with a .NET server and a Unity client. The client supports Windows and Android platforms using native libraries and Opus encoding.

Working directory: i:/projects/voice chat
Integrity mode: development

## Requirements

### R1. Unity Client (Windows & Android Support)
- **Voice Recording (Android)**: Implement a native Java class (`.java` file or compiled AAR plugin) that uses Android's `AudioRecord` API to capture microphone input with minimal latency. Bridge this to Unity via JNI / `AndroidJavaObject`.
- **Voice Recording (Windows)**: Implement native audio capture on Windows (e.g., using WASAPI via a native DLL or a lightweight C# wrapper around Windows MMDevice/WASAPI APIs) to minimize capture latency compared to standard Unity `Microphone`.
- **Opus Encoding/Decoding**: Integrate native Opus codec libraries for both Windows (x86_64 DLL) and Android (arm64-v8a/armeabi-v7a shared library `.so`). Audio frames must be encoded to Opus packets on the recording thread and decoded back to PCM on the playback thread.
- **Playback**: Play received audio streams through a low-latency Unity custom AudioClip/AudioSource or native audio pipeline.

### R2. Scalable .NET Voice Chat Server
- **Server Architecture**: A high-performance, asynchronous .NET console application.
- **Transport Protocol**: Use low-latency UDP or KCP/WebRTC-like protocol to transport audio packets. Frame sequencing and packet loss handling (such as forward error correction or simple packet drops) must be optimized for audio.
- **Room/Session Management**: Support multiple voice chat rooms. Broadcast encoded audio packets to all other clients in the same room.

### R3. Latency Profiling & Diagnostics
- **Profiling Metrics**: Track and report round-trip time (RTT), recording/capture latency, encoding latency, network transit time, decoding latency, and jitter/buffering delay.
- **Diagnostics UI**: Provide a simple Unity UI panel showing real-time stats (latency, packet loss, bandwidth, audio level, connected users).

## Acceptance Criteria

### Latency and Performance
- [ ] End-to-end latency (mouth-to-ear delay) is under 150ms on a local network.
- [ ] Voice streaming uses Opus encoding/decoding without blocking the Unity main thread (no frame drops or game lag).
- [ ] Audio capture and playback are clear, free from major static, distortion, or persistent robotic clicking.

### Platform Support & Build
- [ ] Client builds and runs successfully on both Windows Standalone (x86_64) and Android (APK).
- [ ] Server builds and runs successfully under .NET 8.0/9.0.

### Verification
- [ ] Programmatic mock client script or test project included to simulate multiple voice connections and verify server scalability/packet throughput.
