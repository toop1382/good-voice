# Voice Chat Client Integration Guide

This guide details how to integrate and use the Voice Chat Client in your Unity project. The client supports low-latency voice streaming, multiple rooms, muting, runtime protocol switching, and advanced audio effects (including Android hardware Echo/Noise cancellation).

---

## 1. Setting Up the Component in Unity

1. **Attach the Component**: Create an empty `GameObject` in your scene (e.g., named `VoiceChatManager`) and attach the `VoiceNetworkManager` component to it.
2. **UnityMainThreadDispatcher**: The manager automatically instantiates a `UnityMainThreadDispatcher` in the scene if one is not present. This guarantees that network event callbacks are executed safely on the Unity main thread.

---

## 2. Inspector Settings

Configure the `VoiceNetworkManager` fields in the Unity Inspector:

| Group | Property | Type | Description |
| :--- | :--- | :--- | :--- |
| **Transport** | `Protocol` | Enum | The protocol to use: `UDP` (recommended for lowest latency), `TCP` (reliable), or `WebSocket` (for WebGL builds). |
| | `ServerHost` | string | IP address or hostname of the voice server (e.g., `127.0.0.1`). |
| | `ServerPort` | int | Server port. Set to `0` to use the protocol default (`5000` for UDP, `5001` for TCP, `5002` for WebSocket). |
| **Session** | `ClientId` | int | Unique integer ID representing this local client. |
| | `RoomId` | int | Initial room number to join upon connecting (default is `1`). |
| | `ConnectOnStart`| bool | If enabled, the client automatically connects and joins the initial room when the scene starts. Set to `false` to handle connection manually from code. |
| **Audio** | `SampleRate` | int | Audio sampling rate in Hz (default `48000`). |
| | `Channels` | int | Number of channels: `1` (mono, recommended for chat) or `2` (stereo). |
| | `FrameSizeInSamples` | int | Opus frame size: `480` samples represents `10ms` of audio at `48kHz`. |
| **Android Settings** | `EnableAndroidAEC`| bool | Enable Android native Acoustic Echo Cancellation (if supported by the device). |
| | `EnableAndroidNS` | bool | Enable Android native Noise Suppression (if supported by the device). |
| | `EnableAndroidAGC` | bool | Enable Android native Automatic Gain Control (if supported by the device). |

---

## 3. C# API & Code Examples

The `VoiceNetworkManager` exposes clean C# methods and events. All callbacks are **automatically marshalled to the Unity Main Thread**, so it is completely safe to update UI elements or instantiate GameObjects directly inside the event handlers.

### Event Subscriptions

Use these events to monitor connection status and room membership:

```csharp
using UnityEngine;
using Client.Network;

public class VoiceChatController : MonoBehaviour
{
    [SerializeField] private VoiceNetworkManager voiceManager;

    private void OnEnable()
    {
        if (voiceManager != null)
        {
            voiceManager.OnConnectSuccess += HandleConnectSuccess;
            voiceManager.OnConnectionFailed += HandleConnectionFailed;
            voiceManager.OnDisconnected += HandleDisconnected;
            voiceManager.OnJoinRoomSuccess += HandleJoinRoomSuccess;
            voiceManager.OnJoinRoomFailed += HandleJoinRoomFailed;
        }
    }

    private void OnDisable()
    {
        if (voiceManager != null)
        {
            voiceManager.OnConnectSuccess -= HandleConnectSuccess;
            voiceManager.OnConnectionFailed -= HandleConnectionFailed;
            voiceManager.OnDisconnected -= HandleDisconnected;
            voiceManager.OnJoinRoomSuccess -= HandleJoinRoomSuccess;
            voiceManager.OnJoinRoomFailed -= HandleJoinRoomFailed;
        }
    }

    private void HandleConnectSuccess()
    {
        Debug.Log("Connected to the voice server successfully!");
        // Safe to modify UI directly
        statusText.text = "Connected";
    }

    private void HandleConnectionFailed()
    {
        Debug.LogError("Failed to connect to the voice server.");
        statusText.text = "Connection Failed";
    }

    private void HandleDisconnected()
    {
        Debug.LogWarning("Disconnected from the voice server.");
        statusText.text = "Disconnected";
    }

    private void HandleJoinRoomSuccess(int roomId)
    {
        Debug.Log($"Successfully joined voice room {roomId}");
        roomText.text = $"Room: {roomId}";
    }

    private void HandleJoinRoomFailed(int roomId)
    {
        Debug.LogError($"Failed to join voice room {roomId}");
    }
}
```

### Methods Reference

#### 1. Connecting Manually
If you set `ConnectOnStart = false` in the Inspector, you can trigger the connection manually:
```csharp
// Connect to server using Host/Port and join the default RoomId
await voiceManager.ConnectAndJoinAsync();
```

#### 2. Joining and Switching Rooms
To switch voice rooms at runtime:
```csharp
// Join room 42
voiceManager.JoinRoom(42);
```

#### 3. Leaving the Current Room
To stop broadcasting audio and listening to others without disconnecting from the socket:
```csharp
// Leaves current room and joins silent room 0
voiceManager.LeaveRoom();
```

#### 4. Disconnecting
To shut down the audio capture, encoders, decoders, and release socket connections:
```csharp
voiceManager.Disconnect();
```

#### 5. Muting/Unmuting Microphone
To temporarily mute or unmute the local user:
```csharp
// Mute microphone
voiceManager.SetMuted(true);

// Unmute microphone
voiceManager.SetMuted(false);
```

#### 6. Switch Protocol at Runtime
To disconnect from the current protocol and reconnect with a new one:
```csharp
// Switch to TCP protocol on port 5001
await voiceManager.SwitchProtocolAsync(VoiceProtocol.TCP, 5001);
```
