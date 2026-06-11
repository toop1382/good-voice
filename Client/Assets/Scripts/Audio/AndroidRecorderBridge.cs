using System;
using System.Threading;
using UnityEngine;
using Unity.Collections;

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
        private IntPtr _directBufferGlobalRef = IntPtr.Zero;
        private NativeArray<byte> _directByteBuffer;
        private NativeArray<float> _directFloatBuffer;

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

            try
            {
                IntPtr classRaw = _javaRecorder.GetRawClass();
                IntPtr fieldId = AndroidJNI.GetFieldID(classRaw, "byteBuffer", "Ljava/nio/ByteBuffer;");
                IntPtr localRef = AndroidJNI.GetObjectField(_javaRecorder.GetRawObject(), fieldId);
                _directBufferGlobalRef = AndroidJNI.NewGlobalRef(localRef);
                AndroidJNI.DeleteLocalRef(localRef);

                _directByteBuffer = AndroidJNI.GetDirectByteBuffer(_directBufferGlobalRef);
                if (!_directByteBuffer.IsCreated)
                {
                    throw new InvalidOperationException("Failed to get direct byte buffer.");
                }
                _directFloatBuffer = _directByteBuffer.Reinterpret<float>(1);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[AndroidRecorderBridge] Failed to acquire JNI direct buffer: {ex.Message}");
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

#if UNITY_ANDROID && !UNITY_EDITOR
            // Tell Java to stop first, which will unblock the blocking AudioRecord.read() in the background thread
            try
            {
                _javaRecorder?.Call("stopRecording");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[AndroidRecorderBridge] Exception during Java stopRecording: {ex.Message}");
            }
#endif

            // Wait for the background thread to safely exit
            _readThread?.Join(1000);

#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                _javaRecorder?.Call("release");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[AndroidRecorderBridge] Exception during Java release: {ex.Message}");
            }

            _javaRecorder?.Dispose();
            _javaRecorder = null;

            if (_directBufferGlobalRef != IntPtr.Zero)
            {
                AndroidJNI.DeleteGlobalRef(_directBufferGlobalRef);
                _directBufferGlobalRef = IntPtr.Zero;
                _directByteBuffer = default;
                _directFloatBuffer = default;
            }
#endif
            Debug.Log("[AndroidRecorderBridge] Android audio capture stopped.");
        }

        private void ReadLoop(object obj)
        {
            var token = (CancellationToken)obj;

#if UNITY_ANDROID && !UNITY_EDITOR
            AndroidJNI.AttachCurrentThread();
            try
            {
#endif
                while (!token.IsCancellationRequested)
                {
#if UNITY_ANDROID && !UNITY_EDITOR
                    var recorder = _javaRecorder;
                    if (recorder == null || !_directFloatBuffer.IsCreated) break;

                    int framesRead = recorder.Call<int>("readSamples", _frameSizeInSamples);
                    if (framesRead > 0)
                    {
                        int samplesCount = framesRead * Channels;
                        if (samplesCount == _readBuffer.Length)
                        {
                            _directFloatBuffer.CopyTo(_readBuffer);
                        }
                        else if (samplesCount > 0)
                        {
                            var slice = new NativeSlice<float>(_directFloatBuffer, 0, samplesCount);
                            float[] tempBuffer = new float[samplesCount];
                            slice.CopyTo(tempBuffer);
                            Array.Copy(tempBuffer, 0, _readBuffer, 0, samplesCount);
                        }
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
#if UNITY_ANDROID && !UNITY_EDITOR
            }
            catch (Exception ex)
            {
                Debug.LogError($"[AndroidRecorderBridge] Exception in ReadLoop: {ex.Message}\n{ex.StackTrace}");
            }
            finally
            {
                AndroidJNI.DetachCurrentThread();
            }
#endif
        }

        public void Tick() { }

        public void Dispose()
        {
            StopRecording();
            _cts?.Dispose();
        }
    }
}
