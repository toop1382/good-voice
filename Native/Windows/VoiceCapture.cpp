// VoiceCapture.cpp
// Native WASAPI audio capture DLL for Unity on Windows
// Provides float32 PCM output at a configurable sample rate and frame size.
// Exported C API:
//   int VoiceCapture_Init(int sampleRate, int channels, int framesPerBuffer)
//   int VoiceCapture_Start()
//   int VoiceCapture_Stop()
//   int VoiceCapture_ReadFrames(float* buffer, int frameCount) -> frames read
//   int VoiceCapture_Destroy()

#include <windows.h>
#include <mmdeviceapi.h>
#include <audioclient.h>
#include <avrt.h>
#include <cstring>
#include <atomic>
#include <thread>
#include <vector>
#include <mutex>

#pragma comment(lib, "avrt.lib")
#pragma comment(lib, "ole32.lib")

#define EXPORT extern "C" __declspec(dllexport)
#define VC_OK          0
#define VC_ERR_INIT   -1
#define VC_ERR_START  -2
#define VC_ERR_PARAM  -3

// Simple lock-free ring buffer for float samples
class FloatRingBuffer
{
public:
    FloatRingBuffer() : buf(nullptr), mask(0), writePos(0), readPos(0) {}

    void init(int capacitySamples)
    {
        // Round up to power of 2 for fast modulo
        int cap = 1;
        while (cap < capacitySamples) cap <<= 1;
        buf = new float[cap]();
        mask = cap - 1;
    }

    ~FloatRingBuffer() { delete[] buf; }

    void write(const float* data, int count)
    {
        for (int i = 0; i < count; i++)
            buf[(writePos++ & mask)] = data[i];
    }

    int read(float* out, int count)
    {
        int avail = (int)(writePos - readPos);
        if (avail < count) return 0;
        for (int i = 0; i < count; i++)
            out[i] = buf[(readPos++ & mask)];
        return count;
    }

    int available() const { return (int)(writePos - readPos); }

private:
    float* buf;
    unsigned mask;
    std::atomic<unsigned long long> writePos{ 0 };
    std::atomic<unsigned long long> readPos{ 0 };
};

// --- Globals ---
static IMMDeviceEnumerator*  g_pEnumerator = nullptr;
static IMMDevice*            g_pDevice     = nullptr;
static IAudioClient*         g_pAudioClient = nullptr;
static IAudioCaptureClient* g_pCaptureClient = nullptr;
static WAVEFORMATEX*         g_pwfx        = nullptr;

static FloatRingBuffer g_ring;
static std::atomic<bool> g_running{ false };
static std::thread g_captureThread;

static int g_sampleRate  = 48000;
static int g_channels    = 1;
static int g_framesPerBuf = 480;

static void CaptureThread()
{
    // Boost this thread to Audio priority class
    DWORD taskIndex = 0;
    HANDLE hTask = AvSetMmThreadCharacteristicsW(L"Pro Audio", &taskIndex);

    while (g_running.load())
    {
        UINT32 packetSize = 0;
        if (FAILED(g_pCaptureClient->GetNextPacketSize(&packetSize)))
            break;

        while (packetSize > 0)
        {
            BYTE*  pData    = nullptr;
            UINT32 numFrames = 0;
            DWORD  flags    = 0;

            if (FAILED(g_pCaptureClient->GetBuffer(&pData, &numFrames, &flags, nullptr, nullptr)))
                break;

            if (!(flags & AUDCLNT_BUFFERFLAGS_SILENT) && pData != nullptr)
            {
                // WASAPI shared mode returns float32 by default (WAVE_FORMAT_IEEE_FLOAT)
                float* fData = reinterpret_cast<float*>(pData);
                int totalSamples = numFrames * g_channels;

                // Down-mix to mono if needed
                if (g_pwfx->nChannels != g_channels && g_channels == 1 && g_pwfx->nChannels == 2)
                {
                    // Mix stereo to mono
                    for (UINT32 f = 0; f < numFrames; f++)
                    {
                        float mono = (fData[f * 2] + fData[f * 2 + 1]) * 0.5f;
                        g_ring.write(&mono, 1);
                    }
                }
                else
                {
                    g_ring.write(fData, totalSamples);
                }
            }

            g_pCaptureClient->ReleaseBuffer(numFrames);

            if (FAILED(g_pCaptureClient->GetNextPacketSize(&packetSize)))
                goto done;
        }

        // Sleep ~half a frame duration to avoid busy-loop
        Sleep((DWORD)(g_framesPerBuf * 500 / g_sampleRate));
    }

done:
    if (hTask) AvRevertMmThreadCharacteristics(hTask);
}

EXPORT int VoiceCapture_Init(int sampleRate, int channels, int framesPerBuffer)
{
    if (sampleRate <= 0 || channels <= 0 || framesPerBuffer <= 0)
        return VC_ERR_PARAM;

    g_sampleRate   = sampleRate;
    g_channels     = channels;
    g_framesPerBuf = framesPerBuffer;

    // Ring buffer: 2 seconds worth of samples
    g_ring.init(sampleRate * channels * 2);

    if (FAILED(CoInitializeEx(nullptr, COINIT_MULTITHREADED)))
        return VC_ERR_INIT;

    if (FAILED(CoCreateInstance(__uuidof(MMDeviceEnumerator), nullptr,
        CLSCTX_ALL, __uuidof(IMMDeviceEnumerator), (void**)&g_pEnumerator)))
        return VC_ERR_INIT;

    if (FAILED(g_pEnumerator->GetDefaultAudioEndpoint(eCapture, eCommunications, &g_pDevice)))
        return VC_ERR_INIT;

    if (FAILED(g_pDevice->Activate(__uuidof(IAudioClient), CLSCTX_ALL, nullptr, (void**)&g_pAudioClient)))
        return VC_ERR_INIT;

    if (FAILED(g_pAudioClient->GetMixFormat(&g_pwfx)))
        return VC_ERR_INIT;

    // Override to our sample rate
    g_pwfx->nSamplesPerSec  = sampleRate;
    g_pwfx->nBlockAlign      = (WORD)(g_pwfx->nChannels * g_pwfx->wBitsPerSample / 8);
    g_pwfx->nAvgBytesPerSec  = g_pwfx->nBlockAlign * sampleRate;

    // 10ms buffer = low latency shared mode
    REFERENCE_TIME bufDuration = (REFERENCE_TIME)(framesPerBuffer * 10000000LL / sampleRate);
    if (FAILED(g_pAudioClient->Initialize(AUDCLNT_SHAREMODE_SHARED,
        AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM | AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY,
        bufDuration, 0, g_pwfx, nullptr)))
        return VC_ERR_INIT;

    if (FAILED(g_pAudioClient->GetService(__uuidof(IAudioCaptureClient), (void**)&g_pCaptureClient)))
        return VC_ERR_INIT;

    return VC_OK;
}

EXPORT int VoiceCapture_Start()
{
    if (!g_pAudioClient) return VC_ERR_INIT;
    if (FAILED(g_pAudioClient->Start())) return VC_ERR_START;

    g_running = true;
    g_captureThread = std::thread(CaptureThread);
    return VC_OK;
}

EXPORT int VoiceCapture_Stop()
{
    g_running = false;
    if (g_captureThread.joinable())
        g_captureThread.join();
    if (g_pAudioClient) g_pAudioClient->Stop();
    return VC_OK;
}

EXPORT int VoiceCapture_ReadFrames(float* buffer, int frameCount)
{
    return g_ring.read(buffer, frameCount * g_channels) / g_channels;
}

EXPORT int VoiceCapture_Destroy()
{
    VoiceCapture_Stop();
    if (g_pCaptureClient) { g_pCaptureClient->Release(); g_pCaptureClient = nullptr; }
    if (g_pAudioClient)   { g_pAudioClient->Release();   g_pAudioClient   = nullptr; }
    if (g_pwfx)           { CoTaskMemFree(g_pwfx);       g_pwfx           = nullptr; }
    if (g_pDevice)        { g_pDevice->Release();        g_pDevice        = nullptr; }
    if (g_pEnumerator)    { g_pEnumerator->Release();    g_pEnumerator    = nullptr; }
    CoUninitialize();
    return VC_OK;
}
