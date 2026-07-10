using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Client.Audio;
using Client.Codec;
using Client.Diagnostics;
using UnityEngine;

namespace Client.Network
{
    /// <summary>
    /// Unity MonoBehaviour that drives the full voice chat pipeline.
    /// Supports UDP, TCP, and WebSocket via the IVoiceTransport abstraction.
    ///
    /// Inspector setup:
    ///   1. Set Protocol (UDP / TCP / WebSocket)
    ///   2. Set ServerHost + ServerPort (or leave port = 0 to use the per-protocol default)
    ///   3. Set unique ClientId and RoomId
    ///   4. Attach to a persistent GameObject
    /// </summary>
    public class VoiceNetworkManager : MonoBehaviour
    {
        public enum State { Disconnected, Connecting, Connected, InRoom }

        // ── Inspector ─────────────────────────────────────────────────────
        [Header("Transport")]
        [Tooltip("Voice protocol: UDP (lowest latency), TCP (reliable), WebSocket (browser/WebGL)")]
        public VoiceProtocol Protocol = VoiceProtocol.UDP;

        [Tooltip("Server hostname or IP address")]
        public string ServerHost = "127.0.0.1";

        [Tooltip("Server port. Set to 0 to use the default port for the selected protocol.")]
        public int ServerPort = 0;

        [Header("Session")]
        [Tooltip("Unique numeric client ID for this Unity instance")]
        public int ClientId = 1;
        [Tooltip("Voice room to join on connect")]
        public int RoomId = 1;
        [Tooltip("Automatically connect and join the room on Start")]
        public bool ConnectOnStart = true;
        [Tooltip("Automatically attach the runtime debug UI (F1 overlay)")]
        public bool AutoAttachDebugUI = true;

        [Header("Audio")]
        public SamplingFrequency SampleRate = SamplingFrequency.Frequency_48000;
        public int Channels = 1;
        [Tooltip("Opus frame size in samples: 480 = 10ms at 48kHz")]
        public int FrameSizeInSamples = 480;

        [Header("Android Audio Settings")]
        [Tooltip("Enable Acoustic Echo Cancellation (AEC) on Android (if supported by device)")]
        public bool EnableAndroidAEC = true;
        [Tooltip("Enable Noise Suppression (NS) on Android (if supported by device)")]
        public bool EnableAndroidNS = true;
        [Tooltip("Enable Automatic Gain Control (AGC) on Android (if supported by device)")]
        public bool EnableAndroidAGC = true;

        // ── Public state ──────────────────────────────────────────────────
        public State CurrentState { get; private set; } = State.Disconnected;
        public float LastRttMs    { get; private set; }
        public string ActiveProtocol => _transport?.Protocol ?? "None";
        public DiagnosticsCollector Diagnostics => _diagnostics;

        // ── Public Events ──────────────────────────────────────────────────
        public event Action OnConnectSuccess;
        public event Action OnConnectionFailed;
        public event Action OnDisconnected;
        public event Action<int> OnJoinRoomSuccess;
        public event Action<int> OnJoinRoomFailed;

        // ── Internal ──────────────────────────────────────────────────────
        private IVoiceTransport _transport;
        private IAudioRecorder  _recorder;
        private AudioPlaybackManager _playback;
        private OpusEncoder _encoder;
        private readonly Dictionary<int, OpusDecoder> _decoders = new();
        private readonly Dictionary<int, long> _lastReceiveTimeMs = new();
        private readonly List<int> _timedOutClients = new();
        private byte[] _encodeOutputBuf;
        private bool _isMuted;
        private DiagnosticsCollector _diagnostics;
        private long _lastServerHeartbeatTimeMs;
        private float _heartbeatTimer;
        private float _diagTickTimer;
        private bool _isDestroyed;

        private readonly object _audioLock = new object();

        private void Awake()
        {
            _encodeOutputBuf = new byte[4000];
            _diagnostics = new DiagnosticsCollector();

            // Ensure UnityMainThreadDispatcher is present in the scene
            if (FindObjectOfType<UnityMainThreadDispatcher>() == null)
            {
                var go = new GameObject("UnityMainThreadDispatcher");
                go.AddComponent<UnityMainThreadDispatcher>();
                Debug.Log("[VoiceNetworkManager] Automatically created UnityMainThreadDispatcher.");
            }

            // Automatically attach the runtime debug UI if configured
            if (AutoAttachDebugUI)
            {
                gameObject.AddComponent<VoiceChatDebugUI>();
            }
        }

        private async void Start()
        {
            if (ConnectOnStart)
            {
                await ConnectAndJoinAsync();
            }
        }

        private void Update()
        {
            // If connected but audio pipeline is not initialized yet (e.g. waiting for Android permission), setup now
            if ((CurrentState == State.Connected || CurrentState == State.InRoom) && _recorder == null)
            {
                SetupAudioPipeline();
            }

            // Tick diagnostics once per second to compute bandwidth
            _diagTickTimer += Time.deltaTime;
            if (_diagTickTimer >= 1f)
            {
                _diagTickTimer = 0f;
                _diagnostics?.Tick();
            }

            // Drain queued decoded PCM frames into AudioClip ring buffers.
            // AudioPlaybackManager.Tick() must run on the Unity main thread.
            _playback?.Tick();

            // Poll microphone fallback recorders (like UnityMicRecorder) on the main thread.
            _recorder?.Tick();

            CheckClientTimeouts();
            CheckConnectionHealth();
        }

        public async Task ConnectAndJoinAsync()
        {
            if (CurrentState != State.Disconnected) return;
            CurrentState = State.Connecting;

            // Resolve port: 0 means use protocol default
            int port = ServerPort > 0 ? ServerPort : VoiceTransportFactory.DefaultPort(Protocol);

            Debug.Log($"[VoiceNetworkManager] Connecting via {Protocol} to {ServerHost}:{port} as client {ClientId}...");

            _transport = VoiceTransportFactory.Create(Protocol, ServerHost, port, ClientId);
            _transport.OnHandshakeAck  += OnHandshakeAck;
            _transport.OnRoomJoinAck   += OnRoomJoinAck;
            _transport.OnAudioReceived += OnAudioReceived;
            _transport.OnDisconnected  += OnDisconnect;
            _transport.OnHeartbeatAck  += OnHeartbeatAck;

            _lastServerHeartbeatTimeMs = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _heartbeatTimer = 0f;

            bool ok = await _transport.ConnectAsync();
            if (!ok)
            {
                UnityMainThreadDispatcher.Enqueue(() =>
                {
                    CurrentState = State.Disconnected;
                    Debug.LogError($"[VoiceNetworkManager] Failed to connect via {Protocol}.");
                    OnConnectionFailed?.Invoke();
                    _ = AutoReconnectAsync();
                });
                return;
            }

            UnityMainThreadDispatcher.Enqueue(() =>
            {
                lock (_audioLock)
                {
                    CurrentState = State.Connected;
                    SetupAudioPipeline();
                    OnConnectSuccess?.Invoke();
                    _transport.JoinRoom(RoomId);
                }
            });
        }

        /// <summary>Mute or unmute the local microphone.</summary>
        public void SetMuted(bool muted)
        {
            _isMuted = muted;
            Debug.Log($"[VoiceNetworkManager] Mic {(muted ? "muted" : "unmuted")}.");
        }

        /// <summary>Switch to a different protocol at runtime (disconnects and reconnects).</summary>
        public async Task SwitchProtocolAsync(VoiceProtocol newProtocol, int newPort = 0)
        {
            Debug.Log($"[VoiceNetworkManager] Switching from {Protocol} to {newProtocol}...");
            TearDown();
            Protocol   = newProtocol;
            ServerPort = newPort;
            await ConnectAndJoinAsync();
        }

        /// <summary>Requests joining a specific room. If disconnected, updates the default RoomId to join on next connection.</summary>
        public void JoinRoom(int roomId)
        {
            RoomId = roomId;
            if (CurrentState == State.Connected || CurrentState == State.InRoom)
            {
                _transport?.JoinRoom(roomId);
            }
        }

        /// <summary>Leaves the current room and joins room 0 (a silent/lobby room).</summary>
        public void LeaveRoom()
        {
            if (CurrentState == State.InRoom)
            {
                JoinRoom(0);
            }
        }

        /// <summary>Disconnects from the server and cleans up the audio pipeline.</summary>
        public void Disconnect()
        {
            TearDown();
        }

        // ── Audio pipeline ────────────────────────────────────────────────
        private void SetupAudioPipeline()
        {
            if (_recorder != null) return; // Already initialized

#if UNITY_ANDROID
            if (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(UnityEngine.Android.Permission.Microphone))
            {
                Debug.LogWarning("[VoiceNetworkManager] Microphone permission not authorized yet. Setup postponed.");
                return;
            }
#endif

            _encoder = new OpusEncoder(
                SampleRate, (NumChannels)Channels, OpusApplication.VoIP);
            _encoder.Bitrate    = 24000;
            _encoder.Complexity = 5;
            _encoder.Signal     = OpusSignal.Voice;

            _playback = new AudioPlaybackManager((int)SampleRate, Channels, FrameSizeInSamples, transform);

            _recorder = AudioRecorderFactory.Create(
                (int)SampleRate, Channels, FrameSizeInSamples, EnableAndroidAEC, EnableAndroidNS, EnableAndroidAGC);
            _recorder.OnAudioFrameCaptured += OnAudioFrameCaptured;
            _recorder.StartRecording();

            if (!_recorder.IsRecording)
            {
                Debug.LogError("[VoiceNetworkManager] Failed to start voice recording. Cleaning up to retry.");
                _recorder.Dispose();
                _recorder = null;
                _encoder?.Dispose();
                _encoder = null;
                _playback?.Dispose();
                _playback = null;
                return;
            }

            Debug.Log($"[VoiceNetworkManager] Audio pipeline ready (protocol={Protocol}).");
        }

        // ── Transport callbacks ───────────────────────────────────────────
        private void OnHandshakeAck(double rttMs)
        {
            _lastServerHeartbeatTimeMs = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            LastRttMs = (float)rttMs;
            _diagnostics?.RecordRtt(rttMs);
            Debug.Log($"[VoiceNetworkManager] [{Protocol}] Handshake ACK — RTT={rttMs:F1}ms");
        }

        private void OnRoomJoinAck(bool success)
        {
            _lastServerHeartbeatTimeMs = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            UnityMainThreadDispatcher.Enqueue(() =>
            {
                if (success)
                {
                    if (RoomId == 0)
                    {
                        CurrentState = State.Connected;
                        Debug.Log($"[VoiceNetworkManager] Left room (joined silent room 0) via {Protocol}.");
                    }
                    else
                    {
                        CurrentState = State.InRoom;
                        Debug.Log($"[VoiceNetworkManager] Joined room {RoomId} via {Protocol}.");
                    }
                    OnJoinRoomSuccess?.Invoke(RoomId);
                }
                else
                {
                    Debug.LogError($"[VoiceNetworkManager] Failed to join room {RoomId}.");
                    OnJoinRoomFailed?.Invoke(RoomId);
                }
            });
        }

        private void OnAudioReceived(int senderId, byte[] opusPacket, int opusLength, uint sequenceNumber, long sendTimestamp)
        {
            OpusDecoder decoder;

            lock (_decoders)
            {
                if (!_decoders.TryGetValue(senderId, out decoder))
                {
                    decoder = new OpusDecoder(SampleRate, (NumChannels)Channels);
                    _decoders[senderId] = decoder;
                    UnityMainThreadDispatcher.Enqueue(() => _playback?.AddClient(senderId));
                }

                _lastReceiveTimeMs[senderId] = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            }

            _lastServerHeartbeatTimeMs = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            _diagnostics?.RecordPacketReceived(senderId, (long)sequenceNumber, opusLength, sendTimestamp);

            float[] pcm = System.Buffers.ArrayPool<float>.Shared.Rent(FrameSizeInSamples * Channels);
            int decoded = decoder.Decode(opusPacket, opusLength, pcm);
            if (decoded > 0)
            {
                _playback?.EnqueueAudio(senderId, (int)sequenceNumber, pcm);
            }
            else
            {
                System.Buffers.ArrayPool<float>.Shared.Return(pcm);
            }
        }

        private void OnDisconnect()
        {
            UnityMainThreadDispatcher.Enqueue(() =>
            {
                bool wasConnected = (CurrentState == State.Connected || CurrentState == State.InRoom);
                CurrentState = State.Disconnected;
                Debug.LogWarning($"[VoiceNetworkManager] Disconnected from server ({Protocol}).");
                if (wasConnected && !_isDestroyed)
                {
                    OnDisconnected?.Invoke();
                }
            });
        }

        // ── Microphone capture callback (background thread) ───────────────
        private void OnAudioFrameCaptured(float[] pcm)
        {
            lock (_audioLock)
            {
                if (_isMuted || CurrentState != State.InRoom || _encoder == null || _transport == null) return;

                _diagnostics?.RecordInputLevel(pcm);

                var sw = System.Diagnostics.Stopwatch.StartNew();
                int encodedLen = _encoder.Encode(pcm, _encodeOutputBuf);
                sw.Stop();

                if (encodedLen > 0)
                {
                    _diagnostics?.RecordEncodeTime(sw.Elapsed.TotalMilliseconds);
                    _diagnostics?.RecordBytesSent(encodedLen + 28);
                    _transport.SendAudio(_encodeOutputBuf, encodedLen);
                }
            }
        }

        // ── Cleanup ───────────────────────────────────────────────────────
        private void TearDown()
        {
            lock (_audioLock)
            {
                bool wasConnected = (CurrentState == State.Connected || CurrentState == State.InRoom);
                CurrentState = State.Disconnected;
                _recorder?.StopRecording();
                _recorder?.Dispose();
                _recorder = null;
                _encoder?.Dispose();
                _encoder = null;
                
                lock (_decoders)
                {
                    foreach (var d in _decoders.Values) d?.Dispose();
                    _decoders.Clear();
                    _lastReceiveTimeMs.Clear();
                }

                if (_transport != null)
                {
                    _transport.OnHandshakeAck  -= OnHandshakeAck;
                    _transport.OnRoomJoinAck   -= OnRoomJoinAck;
                    _transport.OnAudioReceived -= OnAudioReceived;
                    _transport.OnDisconnected  -= OnDisconnect;
                    _transport.OnHeartbeatAck  -= OnHeartbeatAck;
                    _transport.Dispose();
                    _transport = null;
                }

                _playback?.Dispose();
                _playback = null;
                _diagnostics?.Reset();

                if (wasConnected && !_isDestroyed)
                {
                    UnityMainThreadDispatcher.Enqueue(() => OnDisconnected?.Invoke());
                }
            }
        }

        private void OnDestroy()
        {
            _isDestroyed = true;
            TearDown();
        }

        private void OnHeartbeatAck(double rttMs)
        {
            _lastServerHeartbeatTimeMs = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            LastRttMs = (float)rttMs;
            _diagnostics?.RecordRtt(rttMs);
        }

        private void CheckClientTimeouts()
        {
            if (CurrentState == State.Disconnected || _playback == null) return;

            long now = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _timedOutClients.Clear();

            lock (_decoders)
            {
                foreach (var kvp in _lastReceiveTimeMs)
                {
                    if (now - kvp.Value > 5000) // 5 seconds threshold
                    {
                        _timedOutClients.Add(kvp.Key);
                    }
                }

                foreach (int clientId in _timedOutClients)
                {
                    if (_decoders.TryGetValue(clientId, out var decoder))
                    {
                        decoder?.Dispose();
                        _decoders.Remove(clientId);
                    }
                    _lastReceiveTimeMs.Remove(clientId);

                    _playback?.RemoveClient(clientId);
                    _diagnostics?.RemoveClient(clientId);
                }
            }
        }

        private void CheckConnectionHealth()
        {
            if (CurrentState != State.InRoom && CurrentState != State.Connected) return;

            // Send heartbeat every 2 seconds
            _heartbeatTimer += Time.deltaTime;
            if (_heartbeatTimer >= 2f)
            {
                _heartbeatTimer = 0f;
                _transport?.SendHeartbeat();
            }

            // Check server activity timeout
            long now = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (now - _lastServerHeartbeatTimeMs > 6000) // 6 seconds server timeout
            {
                Debug.LogWarning($"[VoiceNetworkManager] Server connection timed out. Attempting reconnection...");
                TearDown();
                _ = AutoReconnectAsync();
            }
        }

        private async Task AutoReconnectAsync()
        {
            await Task.Delay(2000);
            if (_isDestroyed) return;
            if (CurrentState == State.Disconnected)
            {
                Debug.Log("[VoiceNetworkManager] Reconnecting...");
                await ConnectAndJoinAsync();
            }
        }
    }
}
