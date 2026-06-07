package com.voicechat.unity;

import android.media.AudioFormat;
import android.media.AudioRecord;
import android.media.MediaRecorder;
import android.media.audiofx.AcousticEchoCanceler;
import android.media.audiofx.NoiseSuppressor;
import android.media.audiofx.AutomaticGainControl;
import android.util.Log;

import java.util.concurrent.atomic.AtomicBoolean;

/**
 * Low-latency Android voice recorder using AudioRecord API.
 * Accessed from Unity via JNI through AndroidRecorderBridge.cs.
 *
 * Usage from Unity C#:
 *   AndroidJavaObject recorder = new AndroidJavaObject("com.voicechat.unity.AndroidVoiceRecorder", sampleRate, channels, frameSizeInSamples);
 *   recorder.Call("startRecording");
 *   int count = recorder.Call<int>("readSamples", floatBuffer, frameSize);
 *   recorder.Call("stopRecording");
 *   recorder.Call("release");
 */
public class AndroidVoiceRecorder {

    private static final String TAG = "AndroidVoiceRecorder";

    private final int sampleRate;
    private final int channelConfig;
    private final int channels;
    private final int frameSizeInSamples;

    private AudioRecord audioRecord;
    private final AtomicBoolean recording = new AtomicBoolean(false);

    // Short buffer for reading from AudioRecord (16-bit PCM)
    private final short[] shortBuffer;

    private AcousticEchoCanceler aec;
    private NoiseSuppressor ns;
    private AutomaticGainControl agc;
    
    private boolean enableAec = true;
    private boolean enableNs = true;
    private boolean enableAgc = true;

    /**
     * @param sampleRate       e.g. 48000 Hz
     * @param channels         1 = mono, 2 = stereo
     * @param frameSizeInSamples  Opus frame size, e.g. 480 for 10ms at 48kHz
     */
    public AndroidVoiceRecorder(int sampleRate, int channels, int frameSizeInSamples) {
        this(sampleRate, channels, frameSizeInSamples, true, true, true);
    }

    public AndroidVoiceRecorder(int sampleRate, int channels, int frameSizeInSamples, boolean enableAec, boolean enableNs, boolean enableAgc) {
        this.sampleRate = sampleRate;
        this.channels = channels;
        this.channelConfig = (channels == 1)
                ? AudioFormat.CHANNEL_IN_MONO
                : AudioFormat.CHANNEL_IN_STEREO;
        this.frameSizeInSamples = frameSizeInSamples;
        this.shortBuffer = new short[frameSizeInSamples * channels];
        this.enableAec = enableAec;
        this.enableNs = enableNs;
        this.enableAgc = enableAgc;
    }

    /**
     * Initialize and start the AudioRecord session.
     * Must be called before readSamples().
     */
    public boolean startRecording() {
        if (recording.get()) {
            Log.w(TAG, "Already recording.");
            return true;
        }

        int minBufSize = AudioRecord.getMinBufferSize(
                sampleRate,
                channelConfig,
                AudioFormat.ENCODING_PCM_16BIT
        );

        if (minBufSize == AudioRecord.ERROR_BAD_VALUE || minBufSize == AudioRecord.ERROR) {
            Log.e(TAG, "AudioRecord.getMinBufferSize() failed: " + minBufSize);
            return false;
        }

        // Use at least 4x the min buffer for safety, but keep it small for low latency
        int bufferSize = Math.max(minBufSize * 2, frameSizeInSamples * channels * 2 * 4);

        audioRecord = new AudioRecord(
                MediaRecorder.AudioSource.VOICE_COMMUNICATION, // Best for VoIP: AEC/NS enabled
                sampleRate,
                channelConfig,
                AudioFormat.ENCODING_PCM_16BIT,
                bufferSize
        );

        if (audioRecord.getState() != AudioRecord.STATE_INITIALIZED) {
            Log.e(TAG, "AudioRecord failed to initialize.");
            audioRecord.release();
            audioRecord = null;
            return false;
        }

        int audioSessionId = audioRecord.getAudioSessionId();

        if (enableAec && AcousticEchoCanceler.isAvailable()) {
            try {
                aec = AcousticEchoCanceler.create(audioSessionId);
                if (aec != null) {
                    aec.setEnabled(true);
                    Log.i(TAG, "Explicitly enabled AcousticEchoCanceler.");
                }
            } catch (Exception e) {
                Log.e(TAG, "Failed to enable AcousticEchoCanceler: " + e.getMessage());
            }
        } else {
            Log.i(TAG, "AcousticEchoCanceler not requested or not available.");
        }

        if (enableNs && NoiseSuppressor.isAvailable()) {
            try {
                ns = NoiseSuppressor.create(audioSessionId);
                if (ns != null) {
                    ns.setEnabled(true);
                    Log.i(TAG, "Explicitly enabled NoiseSuppressor.");
                }
            } catch (Exception e) {
                Log.e(TAG, "Failed to enable NoiseSuppressor: " + e.getMessage());
            }
        } else {
            Log.i(TAG, "NoiseSuppressor not requested or not available.");
        }

        if (enableAgc && AutomaticGainControl.isAvailable()) {
            try {
                agc = AutomaticGainControl.create(audioSessionId);
                if (agc != null) {
                    agc.setEnabled(true);
                    Log.i(TAG, "Explicitly enabled AutomaticGainControl.");
                }
            } catch (Exception e) {
                Log.e(TAG, "Failed to enable AutomaticGainControl: " + e.getMessage());
            }
        } else {
            Log.i(TAG, "AutomaticGainControl not requested or not available.");
        }

        audioRecord.startRecording();
        recording.set(true);
        Log.i(TAG, "AudioRecord started: " + sampleRate + "Hz, ch=" + channels + ", buf=" + bufferSize);
        return true;
    }

    /**
     * Read one frame of audio as 16-bit PCM shorts converted to floats in [-1, 1].
     * This is a blocking call — it will block until enough samples are available.
     *
     * @param outFloats Output float array (must be length >= frameSizeInSamples * channels)
     * @param frameSize Number of frames (not samples) to read
     * @return Number of frames actually read, or -1 on error
     */
    public int readSamples(float[] outFloats, int frameSize) {
        if (!recording.get() || audioRecord == null) return -1;

        int totalSamples = frameSize * channels;
        int read = audioRecord.read(shortBuffer, 0, totalSamples);

        if (read < 0) {
            Log.e(TAG, "AudioRecord.read() error: " + read);
            return -1;
        }

        // Convert short PCM [-32768, 32767] → float PCM [-1.0, 1.0]
        for (int i = 0; i < read; i++) {
            outFloats[i] = shortBuffer[i] / 32768.0f;
        }

        return read / channels; // Return frames read
    }

    /**
     * Stop recording and release resources.
     */
    public void stopRecording() {
        if (!recording.compareAndSet(true, false)) return;
        if (audioRecord != null) {
            audioRecord.stop();
            Log.i(TAG, "AudioRecord stopped.");
        }
    }

    /**
     * Release the AudioRecord object. Call after stopRecording().
     */
    public void release() {
        stopRecording();
        if (aec != null) {
            try { aec.release(); } catch (Exception e) {}
            aec = null;
        }
        if (ns != null) {
            try { ns.release(); } catch (Exception e) {}
            ns = null;
        }
        if (agc != null) {
            try { agc.release(); } catch (Exception e) {}
            agc = null;
        }
        if (audioRecord != null) {
            audioRecord.release();
            audioRecord = null;
        }
        Log.i(TAG, "AudioRecord released.");
    }

    public boolean isRecording() {
        return recording.get();
    }
}
