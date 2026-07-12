using UnityEditor;
using UnityEngine;
using Client.Network;
using Client.Diagnostics;

namespace Client.Editor
{
    /// <summary>
    /// Editor helper to quickly setup the Voice Chat system in the active scene.
    /// Exposes a menu item under Tools -> GoodVoice -> Setup Voice Chat.
    /// </summary>
    public static class VoiceChatSetup
    {
        [MenuItem("Tools/GoodVoice/Setup Voice Chat")]
        public static void Setup()
        {
            // Check if VoiceSystem already exists in the active scene
            var existing = GameObject.Find("VoiceSystem");
            if (existing != null)
            {
                Selection.activeGameObject = existing;
                Debug.LogWarning("[GoodVoice] VoiceSystem GameObject already exists in the scene.");
                return;
            }

            // Create VoiceSystem root GameObject
            var go = new GameObject("VoiceSystem");

            // Ensure UnityMainThreadDispatcher is in the scene
            var dispatcher = Object.FindObjectOfType<UnityMainThreadDispatcher>();
            if (dispatcher == null)
            {
                var dispatcherGo = new GameObject("UnityMainThreadDispatcher");
                dispatcherGo.AddComponent<UnityMainThreadDispatcher>();
                Undo.RegisterCreatedObjectUndo(dispatcherGo, "Create UnityMainThreadDispatcher");
                Debug.Log("[GoodVoice] Created UnityMainThreadDispatcher.");
            }

            // Add VoiceNetworkManager and configure defaults
            var manager = go.AddComponent<VoiceNetworkManager>();
            manager.ServerHost = "127.0.0.1";
            manager.ServerPort = 0; // Default resolves per-protocol
            manager.ClientId = 0;
            manager.RoomId = 1;
            manager.ConnectOnStart = false; // Give users manual control by default
            manager.AutoAttachDebugUI = true;

            // Add DiagnosticsUI and hook reference
            var diagUI = go.AddComponent<DiagnosticsUI>();
            diagUI.VoiceManager = manager;
            diagUI.ShowPanel = true;

            // Register undo behavior in Unity Editor
            Undo.RegisterCreatedObjectUndo(go, "Setup Voice Chat");

            // Select the created object
            Selection.activeGameObject = go;

            Debug.Log("[GoodVoice] Voice Chat setup successfully! Select the 'VoiceSystem' GameObject to configure ports/IDs.");
        }
    }
}
