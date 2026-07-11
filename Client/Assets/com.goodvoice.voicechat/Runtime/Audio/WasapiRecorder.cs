using System;
using System.Runtime.InteropServices;
using System.Threading;
using UnityEngine;

namespace Client.Audio
{
    /// <summary>
    /// Windows WASAPI-based low-latency audio recorder.
    /// Uses a native C++ DLL (VoiceCapture.dll) to access the Windows Audio Session API
    /// at shared-mode minimum latency (~10ms), far lower than Unity's built-in Microphone class.
    /// </summary>
    public class WasapiRecorder : IAudioRecorder
    {
        // --- Native DLL Imports ---
        private const string DllName = "VoiceCapture";

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int VoiceCapture_Init(int sampleRate, int channels, int framesPerBuffer);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int VoiceCapture_Start();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int VoiceCapture_Stop();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int VoiceCapture_Destroy();

        /// <summary>
        /// Reads captured frames into the provided float buffer.
        /// Returns the number of frames actually read (0 if no data available).
        /// </summary>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int VoiceCapture_ReadFrames([Out] float[] buffer, int frameCount);

        // --- Public Interface ---
        public int SampleRate { get; }
        public int Channels { get; }
        public bool IsRecording { get; private set; }

        public event Action<float[]> OnAudioFrameCaptured;

        private readonly int _framesPerBuffer;
        private float[] _captureBuffer;
        private float[] _slicedCaptureBuffer;
        private Thread _captureThread;
        private CancellationTokenSource _cts;
        private bool _initialized;

        /// <param name="sampleRate">48000 recommended for Opus</param>
        /// <param name="channels">1 (mono) recommended for voice</param>
        /// <param name="framesPerBuffer">Frame count per callback, e.g. 480 = 10ms at 48kHz</param>
        public WasapiRecorder(int sampleRate = 48000, int channels = 1, int framesPerBuffer = 480)
        {
            SampleRate = sampleRate;
            Channels = channels;
            _framesPerBuffer = framesPerBuffer;
            _captureBuffer = new float[framesPerBuffer * channels];
        }

        public void StartRecording()
        {
            if (IsRecording) return;

            if (!_initialized)
            {
                int result = VoiceCapture_Init(SampleRate, Channels, _framesPerBuffer);
                if (result != 0)
                {
                    Debug.LogError($"[WasapiRecorder] VoiceCapture_Init failed with code {result}");
                    return;
                }
                _initialized = true;
            }

            int startResult = VoiceCapture_Start();
            if (startResult != 0)
            {
                Debug.LogError($"[WasapiRecorder] VoiceCapture_Start failed with code {startResult}");
                return;
            }

            IsRecording = true;
            _cts = new CancellationTokenSource();
            _captureThread = new Thread(CaptureLoop)
            {
                IsBackground = true,
                Name = "WASAPI-Capture",
                Priority = System.Threading.ThreadPriority.Highest
            };
            _captureThread.Start(_cts.Token);
            Debug.Log("[WasapiRecorder] Started WASAPI capture.");
        }

        public void StopRecording()
        {
            if (!IsRecording) return;
            IsRecording = false;
            _cts?.Cancel();
            VoiceCapture_Stop();
            _captureThread?.Join(500);
            Debug.Log("[WasapiRecorder] Stopped WASAPI capture.");
        }

        private void CaptureLoop(object obj)
        {
            var token = (CancellationToken)obj;
            int bufferSamples = _framesPerBuffer * Channels;

            while (!token.IsCancellationRequested)
            {
                int framesRead = VoiceCapture_ReadFrames(_captureBuffer, _framesPerBuffer);
                if (framesRead > 0)
                {
                    // Slice to actual samples read
                    int samplesRead = framesRead * Channels;
                    float[] outBuf;
                    if (samplesRead == bufferSamples)
                    {
                        outBuf = _captureBuffer;
                    }
                    else
                    {
                        if (_slicedCaptureBuffer == null || _slicedCaptureBuffer.Length != samplesRead)
                        {
                            _slicedCaptureBuffer = new float[samplesRead];
                        }
                        Array.Copy(_captureBuffer, 0, _slicedCaptureBuffer, 0, samplesRead);
                        outBuf = _slicedCaptureBuffer;
                    }
                    OnAudioFrameCaptured?.Invoke(outBuf);
                }
                else
                {
                    // Spin-wait at minimal cost when no data available (~1ms)
                    Thread.Sleep(1);
                }
            }
        }

        public void Tick() { }

        public void Dispose()
        {
            StopRecording();
            if (_initialized)
            {
                VoiceCapture_Destroy();
                _initialized = false;
            }
            _cts?.Dispose();
        }
    }
}
