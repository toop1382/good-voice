using Client.Diagnostics;
using Client.Network;
using UnityEngine;

namespace Client
{
    /// <summary>
    /// Unity IMGUI-based real-time diagnostics overlay for the voice chat system.
    /// Shows RTT, jitter, packet loss, encode latency, bandwidth, and mic level.
    /// Toggle with F1 key or by setting ShowPanel = true.
    /// </summary>
    public class DiagnosticsUI : MonoBehaviour
    {
        [Header("References")]
        public VoiceNetworkManager VoiceManager;

        [Header("Display")]
        public bool ShowPanel = true;
        public KeyCode ToggleKey = KeyCode.F1;

        private DiagnosticsCollector _collector;
        private GUIStyle _panelStyle;
        private GUIStyle _labelStyle;
        private GUIStyle _titleStyle;
        private bool _stylesInit;
        private float _tickTimer;

        // Smoothed values for display
        private DiagnosticsSnapshot _snapshot;

        private Rect _windowRect = new Rect(Screen.width - 290, 10, 280, 240);

        private void Start()
        {
            _collector = VoiceManager != null
                ? GetDiagnosticsFromManager()
                : null;

            // Set initial window position
            _windowRect = new Rect(Screen.width - 290, 10, 280, 240);
        }

        private DiagnosticsCollector GetDiagnosticsFromManager()
        {
            return VoiceManager != null ? VoiceManager.Diagnostics : null;
        }

        private void Update()
        {
            if (Input.GetKeyDown(ToggleKey))
                ShowPanel = !ShowPanel;

            _tickTimer += Time.deltaTime;
            if (_tickTimer >= 1f)
            {
                _tickTimer = 0f;
                if (_collector != null)
                    _snapshot = _collector.GetSnapshot();
            }
        }

        private void InitStyles()
        {
            if (_stylesInit) return;
            _stylesInit = true;

            _panelStyle = new GUIStyle(GUI.skin.window)
            {
                padding = new RectOffset(12, 12, 16, 10)
            };
            _panelStyle.normal.background = MakeTex(2, 2, new Color(0.05f, 0.05f, 0.1f, 0.85f));
            _panelStyle.onNormal.background = _panelStyle.normal.background;

            _titleStyle = new GUIStyle(GUI.skin.label)
            {
                fontStyle = FontStyle.Bold,
                fontSize = 14,
                normal = { textColor = new Color(0.4f, 0.8f, 1f) }
            };

            _labelStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 12,
                normal = { textColor = Color.white }
            };
        }

        private void OnGUI()
        {
            if (!ShowPanel) return;
            InitStyles();

            // Keep window within screen bounds
            if (_windowRect.x + _windowRect.width > Screen.width)
                _windowRect.x = Screen.width - _windowRect.width - 10;
            if (_windowRect.y + _windowRect.height > Screen.height)
                _windowRect.y = Screen.height - _windowRect.height - 10;

            _windowRect = GUI.Window(101, _windowRect, DrawDiagnosticsWindow, "📡 VOICE CHAT DIAGNOSTICS", _panelStyle);
        }

        private void DrawDiagnosticsWindow(int windowId)
        {
            float w = _windowRect.width;
            float ly = 24;
            float lh = 20;

            // Connection state
            string state = VoiceManager != null ? VoiceManager.CurrentState.ToString() : "N/A";
            Color stateColor = state == "InRoom" ? Color.green : state == "Connecting" ? Color.yellow : Color.red;
            DrawColorLabel(10, ly, w - 20, lh, $"State:         {state}", stateColor); ly += lh;

            // RTT
            Color rttColor = _snapshot.AvgRttMs < 50 ? Color.green
                           : _snapshot.AvgRttMs < 150 ? Color.yellow : Color.red;
            DrawColorLabel(10, ly, w - 20, lh, $"RTT:           {_snapshot.AvgRttMs:F1} ms (cur: {_snapshot.RttMs:F1})", rttColor); ly += lh;

            // Jitter
            Color jitterColor = _snapshot.AvgJitterMs < 20 ? Color.green
                              : _snapshot.AvgJitterMs < 50 ? Color.yellow : Color.red;
            DrawColorLabel(10, ly, w - 20, lh, $"Jitter:        {_snapshot.AvgJitterMs:F1} ms", jitterColor); ly += lh;

            // Packet loss
            Color lossColor = _snapshot.PacketLossPercent < 1 ? Color.green
                            : _snapshot.PacketLossPercent < 5 ? Color.yellow : Color.red;
            DrawColorLabel(10, ly, w - 20, lh, $"Packet Loss:   {_snapshot.PacketLossPercent:F1}%", lossColor); ly += lh;

            // Encode
            DrawColorLabel(10, ly, w - 20, lh, $"Encode Avg:    {_snapshot.AvgEncodeMs:F2} ms", Color.white); ly += lh;

            // Bandwidth
            DrawColorLabel(10, ly, w - 20, lh, $"TX:            {_snapshot.SendKbps:F1} kbps", Color.cyan); ly += lh;
            DrawColorLabel(10, ly, w - 20, lh, $"RX:            {_snapshot.RecvKbps:F1} kbps", Color.cyan); ly += lh;

            // Mic level
            int barWidth = (int)((_snapshot.InputLevelRms * 160f));
            GUI.Label(new Rect(10, ly, 70, lh), "Mic:", _labelStyle);
            GUI.Box(new Rect(75, ly + 2, 160, 14), "");
            if (barWidth > 0)
            {
                GUI.Box(new Rect(75, ly + 2, Mathf.Min(barWidth, 160), 14), "");
            }
            ly += lh + 6;

            GUI.Label(new Rect(10, ly, w - 20, 14),
                $"[{ToggleKey}] Toggle Panel", _labelStyle);

            // Drag window behavior (drag title bar)
            GUI.DragWindow(new Rect(0, 0, 10000, 24));
        }

        private void DrawColorLabel(float x, float y, float w, float h, string text, Color color)
        {
            _labelStyle.normal.textColor = color;
            GUI.Label(new Rect(x, y, w, h), text, _labelStyle);
            _labelStyle.normal.textColor = Color.white;
        }

        private void DrawSeparator(float y, float w)
        {
            GUI.Box(new Rect(5, y, w - 10, 1), "");
        }

        private static Texture2D MakeTex(int w, int h, Color col)
        {
            var pix = new Color[w * h];
            for (int i = 0; i < pix.Length; i++) pix[i] = col;
            var tex = new Texture2D(w, h);
            tex.SetPixels(pix);
            tex.Apply();
            return tex;
        }
    }
}
