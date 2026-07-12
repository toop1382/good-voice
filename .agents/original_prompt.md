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

## 2026-06-12T10:29:13Z

A Blazor-based web dashboard using MySQL for managing voice chat applications, with support for self-hosted Docker deployment, and integration of authentication into the Unity voice chat client and the room-based audio server.

Working directory: i:/projects/voice chat
Integrity mode: development

## Requirements

### R1. Blazor Web Dashboard & MySQL Database
- Build a Blazor Web App that serves as a dashboard for registering users, logging in, and managing applications (creating apps, viewing application keys/secrets).
- Configure the dashboard to use a MySQL database to store user accounts, application records, and sessions.
- Expose secure REST API endpoints (or Minimal APIs) for user authentication (registration/login) returning a session token (e.g., JWT).
- Expose an API endpoint for validating session tokens, which will be called by the Voice Chat Room Server.

### R2. Unity Client & MockClient Authentication Integration
- Implement/update the Unity client (inside `Client/` directory) and the `.NET` client simulator (`MockClient/` directory) to authenticate with the Blazor Web App API.
- After logging in, clients must obtain a session token.
- Pass this session token as part of the connection payload to the Voice Chat Room Server.

### R3. Voice Chat Room Server Token Validation
- Update the room-based audio server (`Server/` directory) to extract the session token from the connection handshake/payload.
- Validate the token by calling the Blazor Web App's validation endpoint.
- Accept the connection if the token is valid, and reject it if the token is invalid or expired.

### R4. Self-Hosted Docker Deployment
- Provide a `docker-compose.yml` (and necessary `Dockerfile`s) at the project root to launch the entire system:
  1. A MySQL database container.
  2. The Blazor Web Dashboard container.
  3. The Voice Chat Room Server container.
- Ensure all services can communicate with each other using standard Docker networking, and configuration is managed via environment variables.

## Acceptance Criteria

### Dashboard & API
- [ ] Dashboard supports secure user registration and login.
- [ ] Registered users can log in, create applications, and obtain app keys.
- [ ] Dashboard exposes an HTTP API for user authentication and token verification.
- [ ] User and app data is stored and persisted in a MySQL database.

### Authentication Flow & Server
- [ ] Voice Chat Room Server calls the Blazor API to validate tokens for incoming client connections.
- [ ] Voice Chat Room Server rejects connections that have invalid, missing, or expired tokens.
- [ ] Unity client can log in, receive a token, and connect successfully to the server.
- [ ] MockClient is updated to authenticate, retrieve a token, and successfully connect/interact with the server.

### Docker Deployment
- [ ] A single `docker-compose up` command successfully spins up MySQL, the Blazor dashboard, and the Voice Chat Room Server.
- [ ] All components are configurable via environment variables in the Docker Compose file.

## Verification Plan

### Automated/Integration Tests
- Provide a test script or a custom test runner in the `Tests/` or `MockClient/` directory that:
  1. Programmatically registers a user and logs in to the Blazor API to receive a token.
  2. Creates an application on the dashboard.
  3. Connects a mock client to the Voice Chat Server using the valid token and verifies successful connection.
  4. Attempts to connect a mock client to the Voice Chat Server with an invalid/expired token and verifies connection rejection.
