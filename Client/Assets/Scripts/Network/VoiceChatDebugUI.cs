using System;
using UnityEngine;
using Client.Network;
using Client.Diagnostics;

namespace Client.Network
{
    /// <summary>
    /// Premium runtime Debug HUD overlay for mobile and editor testing.
    /// Programmatically overlays a styled dark HUD box with:
    ///   - Connection settings (IP, Port, Protocol, Client ID, Room ID)
    ///   - Connection actions (Connect/Disconnect, Join, Mute)
    ///   - Dynamic status indicator with color coding
    ///   - Real-time telemetry (RTT, Jitter, Packet Loss, Bandwidth)
    ///   - Visual audio level meter
    /// </summary>
    [RequireComponent(typeof(VoiceNetworkManager))]
    public class VoiceChatDebugUI : MonoBehaviour
    {
        private VoiceNetworkManager _manager;

        // UI state variables (initialized from manager in Start)
        private string _serverHost;
        private string _serverPortStr;
        private string _clientIdStr;
        private string _roomIdStr;
        private int _protocolIndex; // 0 = UDP, 1 = TCP, 2 = WebSocket

        // Styling textures
        private Texture2D _panelTex;
        private Texture2D _buttonNormalTex;
        private Texture2D _buttonHoverTex;
        private Texture2D _buttonActiveTex;
        private Texture2D _inputTex;
        private Texture2D _meterBgTex;
        private Texture2D _meterFillTex;

        // Custom GUIStyles
        private GUIStyle _panelStyle;
        private GUIStyle _titleStyle;
        private GUIStyle _labelStyle;
        private GUIStyle _inputStyle;
        private GUIStyle _buttonStyle;
        private GUIStyle _statusStyle;
        private GUIStyle _toggleStyle;
        private bool _stylesInitialized;

        private void Start()
        {
            _manager = GetComponent<VoiceNetworkManager>();
            _serverHost = _manager.ServerHost;
            _serverPortStr = _manager.ServerPort.ToString();
            _clientIdStr = _manager.ClientId.ToString();
            _roomIdStr = _manager.RoomId.ToString();
            _protocolIndex = (int)_manager.Protocol;

            InitTextures();
        }

        private void InitTextures()
        {
            // Glassmorphic dark theme color palette
            _panelTex = CreateColorTexture(new Color(0.08f, 0.08f, 0.1f, 0.92f));
            _buttonNormalTex = CreateColorTexture(new Color(0.18f, 0.18f, 0.24f, 1.0f));
            _buttonHoverTex = CreateColorTexture(new Color(0.24f, 0.24f, 0.32f, 1.0f));
            _buttonActiveTex = CreateColorTexture(new Color(0.12f, 0.5f, 0.9f, 1.0f)); // Premium Blue
            _inputTex = CreateColorTexture(new Color(0.04f, 0.04f, 0.06f, 1.0f));
            _meterBgTex = CreateColorTexture(new Color(0.15f, 0.15f, 0.18f, 1.0f));
            _meterFillTex = CreateColorTexture(new Color(0.0f, 0.8f, 0.4f, 1.0f)); // Premium Green
        }

        private void InitializeStyles()
        {
            if (_stylesInitialized) return;

            // Panel style
            _panelStyle = new GUIStyle();
            _panelStyle.normal.background = _panelTex;
            _panelStyle.padding = new RectOffset(16, 16, 16, 16);

            // Title style (clean sans-serif style)
            _titleStyle = new GUIStyle();
            _titleStyle.normal.textColor = Color.white;
            _titleStyle.fontSize = 18;
            _titleStyle.fontStyle = FontStyle.Bold;
            _titleStyle.alignment = TextAnchor.MiddleCenter;
            _titleStyle.margin = new RectOffset(0, 0, 0, 12);

            // Label style
            _labelStyle = new GUIStyle();
            _labelStyle.normal.textColor = new Color(0.7f, 0.7f, 0.75f, 1.0f);
            _labelStyle.fontSize = 13;
            _labelStyle.alignment = TextAnchor.MiddleLeft;

            // Input style
            _inputStyle = new GUIStyle(GUI.skin.textField);
            _inputStyle.normal.background = _inputTex;
            _inputStyle.normal.textColor = Color.white;
            _inputStyle.focused.background = _inputTex;
            _inputStyle.focused.textColor = Color.white;
            _inputStyle.fontSize = 13;
            _inputStyle.padding = new RectOffset(6, 6, 4, 4);

            // Button style
            _buttonStyle = new GUIStyle(GUI.skin.button);
            _buttonStyle.normal.background = _buttonNormalTex;
            _buttonStyle.normal.textColor = Color.white;
            _buttonStyle.hover.background = _buttonHoverTex;
            _buttonStyle.hover.textColor = Color.white;
            _buttonStyle.active.background = _buttonActiveTex;
            _buttonStyle.active.textColor = Color.white;
            _buttonStyle.fontSize = 13;
            _buttonStyle.fontStyle = FontStyle.Bold;
            _buttonStyle.padding = new RectOffset(10, 10, 8, 8);

            // Status label style
            _statusStyle = new GUIStyle();
            _statusStyle.fontSize = 13;
            _statusStyle.fontStyle = FontStyle.Bold;
            _statusStyle.alignment = TextAnchor.MiddleLeft;

            // Toggle style
            _toggleStyle = new GUIStyle(GUI.skin.toggle);
            _toggleStyle.normal.textColor = Color.white;
            _toggleStyle.fontSize = 13;

            _stylesInitialized = true;
        }

        private void OnGUI()
        {
            InitializeStyles();

            // Calculate responsive scale factor relative to 1280x720 virtual resolution
            float scale = Screen.width / 1280f;
            if (scale < 0.7f) scale = 0.7f; // clamp minimum scale

            Matrix4x4 origMatrix = GUI.matrix;
            Vector3 scaleVector = new Vector3(scale, scale, 1.0f);
            GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, scaleVector);

            // Dimensions in virtual pixel space
            float width = 360f;
            float height = 520f;
            float x = (1280f - width) - 20f; // 20px padding from right side
            float y = 20f;

            GUILayout.BeginArea(new Rect(x, y, width, height), _panelStyle);

            // Title
            GUILayout.Label("VOICE CHAT CONTROL PANEL", _titleStyle);

            // --- Server Connection Form ---
            GUILayout.BeginHorizontal();
            GUILayout.Label("Host IP:", _labelStyle, GUILayout.Width(70));
            _serverHost = GUILayout.TextField(_serverHost, _inputStyle);
            GUILayout.EndHorizontal();

            GUILayout.Space(4);

            GUILayout.BeginHorizontal();
            GUILayout.Label("Port:", _labelStyle, GUILayout.Width(70));
            _serverPortStr = GUILayout.TextField(_serverPortStr, _inputStyle);
            GUILayout.EndHorizontal();

            GUILayout.Space(4);

            GUILayout.BeginHorizontal();
            GUILayout.Label("Protocol:", _labelStyle, GUILayout.Width(70));
            string[] protos = new string[] { "UDP", "TCP", "WebSocket" };
            int newProtoIndex = GUILayout.SelectionGrid(_protocolIndex, protos, 3, _buttonStyle);
            if (newProtoIndex != _protocolIndex)
            {
                _protocolIndex = newProtoIndex;
                // Update default port in input field automatically if port was 0 or protocol port
                int currentPort;
                int.TryParse(_serverPortStr, out currentPort);
                if (currentPort == 0 || currentPort == VoiceTransportFactory.DefaultPort((VoiceProtocol)_protocolIndex) || currentPort == 50005 || currentPort == 50006 || currentPort == 50007)
                {
                    _serverPortStr = VoiceTransportFactory.DefaultPort((VoiceProtocol)_protocolIndex).ToString();
                }
            }
            GUILayout.EndHorizontal();

            GUILayout.Space(4);

            GUILayout.BeginHorizontal();
            GUILayout.Label("Client ID:", _labelStyle, GUILayout.Width(70));
            _clientIdStr = GUILayout.TextField(_clientIdStr, _inputStyle);
            GUILayout.EndHorizontal();

            GUILayout.Space(4);

            GUILayout.BeginHorizontal();
            GUILayout.Label("Room ID:", _labelStyle, GUILayout.Width(70));
            _roomIdStr = GUILayout.TextField(_roomIdStr, _inputStyle);
            GUILayout.EndHorizontal();

            GUILayout.Space(12);

            // --- Connect / Disconnect Action Buttons ---
            GUILayout.BeginHorizontal();

            bool isConnected = _manager.CurrentState == VoiceNetworkManager.State.Connected || _manager.CurrentState == VoiceNetworkManager.State.InRoom;
            bool isConnecting = _manager.CurrentState == VoiceNetworkManager.State.Connecting;

            if (isConnected || isConnecting)
            {
                if (GUILayout.Button("DISCONNECT", _buttonStyle))
                {
                    _manager.Disconnect();
                }
            }
            else
            {
                if (GUILayout.Button("CONNECT & JOIN", _buttonStyle))
                {
                    // Update manager values from input fields
                    _manager.ServerHost = _serverHost;
                    int.TryParse(_serverPortStr, out _manager.ServerPort);
                    int.TryParse(_clientIdStr, out _manager.ClientId);
                    int.TryParse(_roomIdStr, out _manager.RoomId);
                    _manager.Protocol = (VoiceProtocol)_protocolIndex;

                    // Trigger connection
                    _ = _manager.ConnectAndJoinAsync();
                }
            }

            GUILayout.EndHorizontal();

            GUILayout.Space(12);

            // --- Divider line ---
            DrawDivider();

            GUILayout.Space(8);

            // --- Connection Status Block ---
            GUILayout.BeginHorizontal();
            GUILayout.Label("Status:", _labelStyle, GUILayout.Width(70));
            Color statusColor = Color.red;
            string statusText = "● DISCONNECTED";
            if (_manager.CurrentState == VoiceNetworkManager.State.InRoom)
            {
                statusColor = new Color(0.0f, 0.8f, 0.4f, 1.0f); // bright green
                statusText = "● IN ROOM " + _manager.RoomId;
            }
            else if (_manager.CurrentState == VoiceNetworkManager.State.Connected)
            {
                statusColor = new Color(0.12f, 0.5f, 0.9f, 1.0f); // blue
                statusText = "● CONNECTED (LOBBY)";
            }
            else if (_manager.CurrentState == VoiceNetworkManager.State.Connecting)
            {
                statusColor = new Color(0.9f, 0.5f, 0.12f, 1.0f); // orange
                statusText = "● CONNECTING...";
            }
            _statusStyle.normal.textColor = statusColor;
            GUILayout.Label(statusText, _statusStyle);
            GUILayout.EndHorizontal();

            GUILayout.Space(4);

            GUILayout.BeginHorizontal();
            GUILayout.Label("Protocol:", _labelStyle, GUILayout.Width(70));
            _statusStyle.normal.textColor = Color.white;
            GUILayout.Label(_manager.ActiveProtocol, _statusStyle);
            GUILayout.EndHorizontal();

            GUILayout.Space(8);

            // --- Real-time Network Telemetry ---
            DiagnosticsSnapshot snap = _manager.Diagnostics != null ? _manager.Diagnostics.GetSnapshot() : new DiagnosticsSnapshot();

            GUILayout.BeginHorizontal();
            GUILayout.Label("Latency:", _labelStyle, GUILayout.Width(100));
            GUILayout.Label(snap.RttMs > 0 ? $"{snap.RttMs:F1} ms (Avg: {snap.AvgRttMs:F1} ms)" : "-- ms", _inputStyle);
            GUILayout.EndHorizontal();

            GUILayout.Space(2);

            GUILayout.BeginHorizontal();
            GUILayout.Label("Jitter:", _labelStyle, GUILayout.Width(100));
            GUILayout.Label(snap.AvgJitterMs > 0 ? $"{snap.AvgJitterMs:F1} ms" : "-- ms", _inputStyle);
            GUILayout.EndHorizontal();

            GUILayout.Space(2);

            GUILayout.BeginHorizontal();
            GUILayout.Label("Packet Loss:", _labelStyle, GUILayout.Width(100));
            GUILayout.Label($"{snap.PacketLossPercent:F2} %", _inputStyle);
            GUILayout.EndHorizontal();

            GUILayout.Space(2);

            GUILayout.BeginHorizontal();
            GUILayout.Label("Bandwidth:", _labelStyle, GUILayout.Width(100));
            GUILayout.Label($"Tx: {snap.SendKbps:F1} kbps  Rx: {snap.RecvKbps:F1} kbps", _inputStyle);
            GUILayout.EndHorizontal();

            GUILayout.Space(12);

            // --- Microphone Control & Level Meter ---
            DrawDivider();

            GUILayout.Space(8);

            // Microphone Level Visual Bar
            GUILayout.BeginHorizontal();
            GUILayout.Label("Mic Level:", _labelStyle, GUILayout.Width(70));
            float volumeLevel = snap.InputLevelRms;
            // Draw custom progress bar
            Rect meterRect = GUILayoutUtility.GetRect(180, 16);
            DrawLevelMeter(meterRect, volumeLevel);
            GUILayout.EndHorizontal();

            GUILayout.EndArea();

            GUI.matrix = origMatrix;
        }

        private void DrawDivider()
        {
            Rect rect = GUILayoutUtility.GetRect(10, 1);
            GUI.Box(rect, "", new GUIStyle { normal = { background = CreateColorTexture(new Color(0.2f, 0.2f, 0.25f, 0.4f)) } });
        }

        private void DrawLevelMeter(Rect rect, float value)
        {
            // Draw background
            GUI.DrawTexture(rect, _meterBgTex);

            // Clamp value between 0 and 1
            value = Mathf.Clamp01(value * 4.0f); // Multiply by 4.0f to scale sensitivity of RMS level visually

            if (value > 0.001f)
            {
                Rect fillRect = new Rect(rect.x, rect.y, rect.width * value, rect.height);
                GUI.DrawTexture(fillRect, _meterFillTex);
            }
        }

        private Texture2D CreateColorTexture(Color color)
        {
            Texture2D tex = new Texture2D(1, 1);
            tex.SetPixel(0, 0, color);
            tex.Apply();
            return tex;
        }

        private void OnDestroy()
        {
            if (_panelTex != null) Destroy(_panelTex);
            if (_buttonNormalTex != null) Destroy(_buttonNormalTex);
            if (_buttonHoverTex != null) Destroy(_buttonHoverTex);
            if (_buttonActiveTex != null) Destroy(_buttonActiveTex);
            if (_inputTex != null) Destroy(_inputTex);
            if (_meterBgTex != null) Destroy(_meterBgTex);
            if (_meterFillTex != null) Destroy(_meterFillTex);
        }
    }
}
