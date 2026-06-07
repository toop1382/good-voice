# Low-Latency Voice Chat — Unity + .NET

A production-grade, cross-platform (Windows + Android) voice chat system with:
- **Opus** audio encoding (via native DLL/SO)
- **Native audio capture** (WASAPI on Windows, `AudioRecord` JNI on Android)
- **UDP-based .NET server** with multi-worker CPU-scaled packet routing
- **Real-time latency diagnostics** overlay in Unity
- **MockClient** load simulator for scalability testing

---

## Architecture

```
[Unity Client]                   [.NET Server]
 IAudioRecorder (native)
   ├── WasapiRecorder (Win)       UdpVoiceServer
   └── AndroidRecorderBridge        ├── Receive Loop (1 thread)
         └── AndroidVoiceRecorder   └── Worker Pool (CPU × threads)
                                          └── PacketRouter
 OpusEncoder (float PCM→bytes)              ├── Handshake → RTT ack
 UdpVoiceClient (UDP send/recv)             ├── RoomJoin → ack
 OpusDecoder (bytes→float PCM)              └── Audio → Broadcast
 AudioPlaybackManager                  RoomManager / VoiceRoom
 DiagnosticsCollector              SessionPruningService
 DiagnosticsUI (F1 overlay)
```

### Packet Format (28-byte header + Opus payload)
| Offset | Size | Field |
|--------|------|-------|
| 0 | 4 | PacketType (1=Handshake, 2=RoomJoin, 3=Audio) |
| 4 | 4 | RoomId |
| 8 | 4 | ClientId |
| 12 | 4 | SequenceNumber |
| 16 | 8 | SendTimestamp (ms since epoch) |
| 24 | 4 | PayloadLength |
| 28+ | N | Opus-encoded audio payload |

---

## Projects

| Project | Description |
|---------|-------------|
| `Server/` | .NET 8 async UDP voice server |
| `Shared/` | Packet header + serializer (shared by server & MockClient) |
| `MockClient/` | Multi-client load simulator with latency profiling |
| `Tests/` | xUnit integration tests |
| `Client/Assets/Scripts/` | Unity C# scripts (audio, codec, network, diagnostics) |
| `Client/Assets/Plugins/Windows/` | Native DLL sources (WASAPI + Opus wrapper) |
| `Client/Assets/Plugins/Android/` | Java AudioRecord plugin |

---

## Quick Start

### 1. Run the Server
**Option A — One-Click (Windows):**
Double-click the [start_server.bat](file:///i:/projects/voice%20chat/start_server.bat) file in the root folder. This compiles and launches the voice server, hosts the interactive dashboard, and automatically opens the dashboard panel in your default browser at `http://localhost:5000/`.

**Option B — Linux systemd Service (Auto-Restart Daemon):**
To run the server permanently in the background on a Linux server and ensure it automatically restarts if it exits or crashes:
```bash
chmod +x install_service.sh
./install_service.sh
```
This compiles the server in Release mode, deploys it to `/opt/voice-chat/`, and registers it under systemd as `voice-chat.service` with `Restart=always` enabled.

**Option C — CLI (Windows/Linux/macOS):**
```powershell
dotnet run --project Server -c Release -- --port 50005
```

### 2. Run the Load Simulator
```powershell
# 10 clients, 2 rooms, 60 seconds
dotnet run --project MockClient -c Release -- --server 127.0.0.1 --port 50005 --clients 10 --rooms 2 --duration 60
```

### 3. Run the Loopback Test Client (Echo Client)
The loopback client joins a specified room, receives incoming voice packets from other clients, and immediately echoes them back. This allows you to verify your local microphone and audio playback path directly.

It supports UDP, TCP, and WebSocket protocols (resolves default ports automatically if not specified):
```powershell
# UDP loopback in room 100
dotnet run --project MockClient -c Release -- --loopback --protocol udp --room 100 --clientid 9999

# TCP loopback in room 100
dotnet run --project MockClient -c Release -- --loopback --protocol tcp --room 100 --clientid 9999

# WebSocket loopback in room 100
dotnet run --project MockClient -c Release -- --loopback --protocol ws --room 100 --clientid 9999
```

### 3. Build Native Plugins

#### Windows WASAPI DLL (`VoiceCapture.dll`)
```powershell
cd Client/Assets/Plugins/Windows
cmake -B build -DCMAKE_BUILD_TYPE=Release
cmake --build build --config Release
# Copy VoiceCapture.dll to Client/Assets/Plugins/Windows/
```

#### Opus Unity Wrapper (`UnityOpus.dll` / `libunityopus.so`)

**Option A — vcpkg (recommended):**
```powershell
cmake -B build-opus -DUSE_VCPKG=ON -DCMAKE_TOOLCHAIN_FILE="$env:VCPKG_ROOT/scripts/buildsystems/vcpkg.cmake" OpusCMakeLists.txt
cmake --build build-opus --config Release
```

**Option B — from Opus source:**
```powershell
# Download https://opus-codec.org/release/stable/opus-1.5.2.tar.gz and extract
cmake -B build-opus -DOPUS_SOURCE_DIR=./opus-1.5.2 OpusCMakeLists.txt
cmake --build build-opus --config Release
```

#### Android `.so` (cross-compile via NDK)
```bash
# From Client/Assets/Plugins/Windows/
cmake -B build-android -DCMAKE_TOOLCHAIN_FILE=$NDK/build/cmake/android.toolchain.cmake \
      -DANDROID_ABI=arm64-v8a -DANDROID_PLATFORM=android-26 \
      -DOPUS_SOURCE_DIR=./opus-1.5.2 OpusCMakeLists.txt
cmake --build build-android
# Place libunityopus.so → Client/Assets/Plugins/Android/libs/arm64-v8a/
```

---

## Unity Setup

1. Open `Client/` as a Unity project (Unity 2022.3+ recommended)
2. Create a persistent empty GameObject named `VoiceSystem`
3. Add these MonoBehaviours:
   - `UnityMainThreadDispatcher`
   - `VoiceNetworkManager` (configure `ServerHost`, `ServerPort`, `ClientId`, `RoomId`)
   - `DiagnosticsUI` (drag `VoiceNetworkManager` reference in)
4. Press **F1** in-game to toggle the diagnostics overlay
5. In **Player Settings → Android**, enable `INTERNET` permission

---

## Diagnostics (F1 Overlay)
- **State**: connection state (Disconnected / Connecting / Connected / InRoom)
- **RTT**: round-trip time (green <50ms / yellow <150ms / red >150ms)
- **Jitter**: inter-arrival variance
- **Packet Loss**: % of dropped audio packets
- **Encode Avg**: average Opus encode time per frame
- **TX/RX**: network bandwidth in kbps
- **Mic**: real-time RMS microphone level bar

---

## MockClient Options
```
  --server   / -s      Server IP or hostname   [default: 127.0.0.1]
  --port     / -p      Server port             [default: 50005]
  --clients  / -c      Number of mock clients  [default: 10]
  --rooms    / -r      Number of rooms         [default: 2]
  --duration / -d      Test duration (seconds) [default: 60]
  --packet-interval    Audio send interval ms  [default: 20]
  --payload-size       Fake payload bytes      [default: 120]
  --stagger            Client startup stagger  [default: 10]
  --loopback           Enable loopback echo client mode
  --protocol / -proto  Protocol: udp, tcp, ws  [default: udp]
  --room               Room ID for loopback    [default: 100]
  --clientid           Client ID for loopback  [default: 9999]
```

---

## Audio Pipeline Timing Budget (target <150ms E2E)
| Stage | Target |
|-------|--------|
| Capture (WASAPI/AudioRecord) | ~5-10ms |
| Opus encode (20ms frames) | ~1-2ms |
| UDP send | <1ms |
| Network transit (LAN) | <5ms |
| Server broadcast | <1ms |
| UDP receive | <1ms |
| Opus decode | ~1ms |
| Audio playback buffer | ~20-40ms |
| **Total** | **~35-60ms** on LAN |

---

## License
MIT
