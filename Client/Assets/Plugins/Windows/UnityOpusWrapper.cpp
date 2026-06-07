// UnityOpusWrapper.cpp
// Native C wrapper around libopus for use in Unity (Windows DLL and Android JNI .so)
// Exports a simplified C API matching UnityOpusLibrary.cs P/Invoke declarations.
//
// Build: link against libopus.a / opus.lib
// Windows: cl.exe /LD /O2 UnityOpusWrapper.cpp opus.lib -o UnityOpus.dll
// Android: Use Android.mk or CMakeLists with target_link_libraries(unityopus opus)

#include <opus/opus.h>
#include <cstring>
#include <cstdlib>

#if defined(_WIN32)
#define EXPORT extern "C" __declspec(dllexport)
#else
#define EXPORT extern "C" __attribute__((visibility("default")))
#endif

// ------- Encoder API -------

EXPORT OpusEncoder* OpusEncoderCreate(int Fs, int channels, int application, int* error)
{
    return opus_encoder_create(Fs, channels, application, error);
}

EXPORT int OpusEncode(OpusEncoder* st, const opus_int16* pcm, int frame_size, unsigned char* data, opus_int32 max_data_bytes)
{
    return opus_encode(st, pcm, frame_size, data, max_data_bytes);
}

EXPORT int OpusEncodeFloat(OpusEncoder* st, const float* pcm, int frame_size, unsigned char* data, opus_int32 max_data_bytes)
{
    return opus_encode_float(st, pcm, frame_size, data, max_data_bytes);
}

EXPORT void OpusEncoderDestroy(OpusEncoder* st)
{
    opus_encoder_destroy(st);
}

EXPORT int OpusEncoderSetBitrate(OpusEncoder* st, int bitrate)
{
    return opus_encoder_ctl(st, OPUS_SET_BITRATE(bitrate));
}

EXPORT int OpusEncoderSetComplexity(OpusEncoder* st, int complexity)
{
    return opus_encoder_ctl(st, OPUS_SET_COMPLEXITY(complexity));
}

EXPORT int OpusEncoderSetSignal(OpusEncoder* st, int signal)
{
    return opus_encoder_ctl(st, OPUS_SET_SIGNAL(signal));
}

// ------- Decoder API -------

EXPORT OpusDecoder* OpusDecoderCreate(int Fs, int channels, int* error)
{
    return opus_decoder_create(Fs, channels, error);
}

EXPORT int OpusDecode(OpusDecoder* st, const unsigned char* data, opus_int32 len,
                      opus_int16* pcm, int frame_size, int decode_fec)
{
    return opus_decode(st, data, len, pcm, frame_size, decode_fec);
}

EXPORT int OpusDecodeFloat(OpusDecoder* st, const unsigned char* data, opus_int32 len,
                            float* pcm, int frame_size, int decode_fec)
{
    return opus_decode_float(st, data, len, pcm, frame_size, decode_fec);
}

EXPORT void OpusDecoderDestroy(OpusDecoder* st)
{
    opus_decoder_destroy(st);
}

// ------- Utility -------

EXPORT void OpusPcmSoftClip(float* pcm, int frame_size, int channels, float* softclip_mem)
{
    opus_pcm_soft_clip(pcm, frame_size, channels, softclip_mem);
}
