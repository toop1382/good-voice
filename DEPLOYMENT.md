# GoodVoice Deployment Guide

This guide details how to deploy the server and how to use the Unity client package.

## 1. Deploying the Voice Server

The voice server is a cross-platform .NET 8 application. It exposes several ports for low-latency audio transport and management.

### Default Ports
- **50005 (UDP):** UDP transport (lowest latency, recommended)
- **50006 (TCP):** TCP transport (fallback for restrictive firewalls)
- **50007 (TCP):** WebSocket transport (for WebGL support)
- **5000 (TCP):** HTTP Dashboard / Web UI

### Option A: Docker (Recommended)
You can deploy the server easily using Docker and the provided `docker-compose.yml`.

1. Install Docker and Docker Compose on your server.
2. Clone this repository on the server.
3. Run the following command in the repository root:
   ```bash
   docker-compose up -d
   ```
4. Check the logs to ensure the server started correctly:
   ```bash
   docker-compose logs -f
   ```

### Option B: Native Linux Service (systemd)
If you prefer running the server directly on a Linux machine without Docker:

1. Install the .NET 8 SDK or Runtime on your Linux machine.
2. Clone this repository.
3. Run the installation script:
   ```bash
   chmod +x install_service.sh
   sudo ./install_service.sh
   ```
This compiles the server in Release mode, deploys it to `/opt/voice-chat/`, and sets up a `systemd` service (`voice-chat.service`) to keep it running and restart it on failure.

### Option C: Windows CLI / Local Testing
For local testing or running on a Windows server:
1. Ensure the .NET 8 SDK is installed.
2. Open a terminal in the repository root and run:
   ```bat
   start_server.bat
   ```
Alternatively, using the CLI:
```powershell
dotnet run --project Server -c Release
```

---

## 2. Using the Client in Unity (UPM)

The Unity client is structured as a Unity Package. By adding the repository root `package.json` file, it can be seamlessly imported via the Unity Package Manager (UPM).

### Installing via Git URL
1. Open your Unity project (Unity 2021.3+ required).
2. Open the **Package Manager** (`Window > Package Manager`).
3. Click the **+** button in the top left and select **Add package from git URL...**
4. Paste the Git URL of this repository (e.g., `https://github.com/your-username/voice-chat.git`) and click **Add**.

Unity will download the package, and the `GoodVoice Chat` assets will appear in your `Packages` folder rather than directly inside your `Assets` folder, keeping your project clean.

### Component Setup
1. In your scene, create an empty GameObject (e.g., `VoiceSystem`).
2. Add the `VoiceNetworkManager` component to it.
3. Configure the `ServerHost` to point to your deployed server IP (e.g., `123.45.67.89`) and select the preferred `Protocol`.
4. (Optional) Set `UserMetadata` in the Inspector or via code (e.g., `{"name": "Player1"}`) to share metadata with others in the room. This metadata will be broadcasted to clients when they join or when you join via `OnUserJoinedRoom`.
5. If `ClientId` is left as `0`, it will be automatically generated upon `Awake()`.

For more detailed API usage and integration steps, please see `ClientIntegrationGuide.md`.
