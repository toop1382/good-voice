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

        private void Start()
        {
            _collector = VoiceManager != null
                ? GetDiagnosticsFromManager()
                : null;
        }

        private DiagnosticsCollector GetDiagnosticsFromManager()
        {
            // VoiceNetworkManager exposes diagnostics via property
            var field = VoiceManager.GetType()
                .GetField("_diagnostics",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            return field?.GetValue(VoiceManager) as DiagnosticsCollector;
        }

        private void Update()
        {
            if (Input.GetKeyDown(ToggleKey))
                ShowPanel = !ShowPanel;

            _tickTimer += Time.deltaTime;
            if (_tickTimer >= 1f)
            {
                _tickTimer = 0f;
                _collector?.Tick();
                if (_collector != null)
                    _snapshot = _collector.GetSnapshot();
            }
        }

        private void InitStyles()
        {
            if (_stylesInit) return;
            _stylesInit = true;

            _panelStyle = new GUIStyle(GUI.skin.box)
            {
                padding = new RectOffset(12, 12, 10, 10)
            };
            _panelStyle.normal.background = MakeTex(2, 2, new Color(0.05f, 0.05f, 0.1f, 0.85f));

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

            float w = 280f, h = 220f;
            float x = Screen.width - w - 10;
            float y = 10;

            GUI.BeginGroup(new Rect(x, y, w, h), _panelStyle);

            float ly = 10;
            float lh = 22;

            GUI.Label(new Rect(10, ly, w - 20, lh), "📡 VOICE CHAT DIAGNOSTICS", _titleStyle);
            ly += lh + 4;

            DrawSeparator(ly - 4, w);

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
            GUI.Box(new Rect(75, ly + 4, 160, 14), "");
            GUI.Box(new Rect(75, ly + 4, barWidth, 14), "");
            ly += lh;

            GUI.Label(new Rect(10, ly, w - 20, 14),
                $"[{ToggleKey}] Toggle   |   UDP voice chat", _labelStyle);

            GUI.EndGroup();
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
