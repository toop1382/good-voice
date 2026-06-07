using System;
using System.Threading;
using UnityEngine;

namespace Client.Audio
{
    /// <summary>
    /// Unity C# bridge to the native Android Java AudioRecord class.
    /// Calls AndroidVoiceRecorder.java via JNI using Unity's AndroidJavaObject.
    /// Runs a polling thread that reads samples and fires OnAudioFrameCaptured.
    /// </summary>
    public class AndroidRecorderBridge : IAudioRecorder
    {
        public int SampleRate { get; }
        public int Channels { get; }
        public bool IsRecording { get; private set; }

        public event Action<float[]> OnAudioFrameCaptured;

        private readonly int _frameSizeInSamples;
        private AndroidJavaObject _javaRecorder;
        private Thread _readThread;
        private CancellationTokenSource _cts;
        private float[] _readBuffer;

        private readonly bool _enableAec;
        private readonly bool _enableNs;
        private readonly bool _enableAgc;

        /// <param name="sampleRate">Sample rate, e.g. 48000</param>
        /// <param name="channels">1 = mono</param>
        /// <param name="frameSizeInSamples">Samples per frame, e.g. 480 = 10ms at 48kHz</param>
        public AndroidRecorderBridge(int sampleRate = 48000, int channels = 1, int frameSizeInSamples = 480, bool enableAec = true, bool enableNs = true, bool enableAgc = true)
        {
            SampleRate = sampleRate;
            Channels = channels;
            _frameSizeInSamples = frameSizeInSamples;
            _readBuffer = new float[frameSizeInSamples * channels];
            _enableAec = enableAec;
            _enableNs = enableNs;
            _enableAgc = enableAgc;
        }

        public void StartRecording()
        {
            if (IsRecording) return;

#if UNITY_ANDROID && !UNITY_EDITOR
            _javaRecorder = new AndroidJavaObject(
                "com.voicechat.unity.AndroidVoiceRecorder",
                SampleRate,
                Channels,
                _frameSizeInSamples,
                _enableAec,
                _enableNs,
                _enableAgc
            );

            bool started = _javaRecorder.Call<bool>("startRecording");
            if (!started)
            {
                Debug.LogError("[AndroidRecorderBridge] Failed to start AudioRecord on Android.");
                _javaRecorder.Dispose();
                _javaRecorder = null;
                return;
            }
#else
            Debug.LogWarning("[AndroidRecorderBridge] Running outside Android — no audio will be captured.");
#endif
            IsRecording = true;
            _cts = new CancellationTokenSource();
            _readThread = new Thread(ReadLoop)
            {
                IsBackground = true,
                Name = "Android-AudioRead",
                Priority = System.Threading.ThreadPriority.Highest
            };
            _readThread.Start(_cts.Token);
            Debug.Log("[AndroidRecorderBridge] Android audio capture started.");
        }

        public void StopRecording()
        {
            if (!IsRecording) return;
            IsRecording = false;
            _cts?.Cancel();
            _readThread?.Join(500);

#if UNITY_ANDROID && !UNITY_EDITOR
            _javaRecorder?.Call("stopRecording");
            _javaRecorder?.Call("release");
            _javaRecorder?.Dispose();
            _javaRecorder = null;
#endif
            Debug.Log("[AndroidRecorderBridge] Android audio capture stopped.");
        }

        private void ReadLoop(object obj)
        {
            var token = (CancellationToken)obj;

            while (!token.IsCancellationRequested)
            {
#if UNITY_ANDROID && !UNITY_EDITOR
                var recorder = _javaRecorder;
                if (recorder == null) break;

                int framesRead = recorder.Call<int>("readSamples", _readBuffer, _frameSizeInSamples);
                if (framesRead > 0)
                {
                    OnAudioFrameCaptured?.Invoke(_readBuffer);
                }
                else if (framesRead < 0)
                {
                    Debug.LogError("[AndroidRecorderBridge] Read error from Java recorder.");
                    break;
                }
#else
                // Simulate silence in editor
                Thread.Sleep(10);
#endif
            }
        }

        public void Tick() { }

        public void Dispose()
        {
            StopRecording();
            _cts?.Dispose();
        }
    }
}
