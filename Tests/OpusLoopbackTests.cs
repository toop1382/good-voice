namespace Tests;

using System;
using Client.Codec;
using Xunit;

public class OpusLoopbackTests
{
    [Fact]
    public void Opus_EncodeDecodeLoopback_ShouldMaintainSignalRMS()
    {
        // Arrange
        const SamplingFrequency freq = SamplingFrequency.Frequency_48000;
        const NumChannels channels = NumChannels.Mono;
        const int sampleRate = 48000;
        const int frameSize = 960; // 20ms at 48000Hz

        float[] originalPcm = new float[frameSize];
        float sineFreq = 1000f; // 1kHz sine wave
        float amplitude = 0.5f;

        // Generate 1kHz sine wave
        for (int i = 0; i < frameSize; i++)
        {
            originalPcm[i] = amplitude * MathF.Sin(2f * MathF.PI * sineFreq * i / sampleRate);
        }

        // Calculate original RMS
        float originalRms = CalculateRms(originalPcm);
        Assert.True(originalRms > 0.3f, $"Original RMS should be non-zero and around 0.35. Actual: {originalRms}");

        // Act
        using var encoder = new OpusEncoder(freq, channels, OpusApplication.Audio);
        encoder.Bitrate = 96000;
        encoder.Complexity = 10;

        using var decoder = new OpusDecoder(freq, channels);

        byte[] encodedData = new byte[1000];
        int encodedLength = encoder.Encode(originalPcm, encodedData);

        Assert.True(encodedLength > 0, "Opus encoding failed: returned length <= 0");

        float[] decodedPcm = new float[frameSize];
        int decodedLength = decoder.Decode(encodedData, encodedLength, decodedPcm);

        Assert.Equal(frameSize, decodedLength);

        // Calculate decoded RMS
        float decodedRms = CalculateRms(decodedPcm);

        // Assert
        Assert.True(decodedRms > 0.25f, $"Decoded RMS should be non-zero. Actual: {decodedRms}");
        
        // The difference in RMS between original and decoded should be extremely small for a clean sine wave
        float rmsDifference = MathF.Abs(originalRms - decodedRms);
        Assert.True(rmsDifference < 0.08f, $"RMS difference too high: {rmsDifference}. Original: {originalRms}, Decoded: {decodedRms}");
    }

    private static float CalculateRms(float[] samples)
    {
        float sumSquares = 0f;
        for (int i = 0; i < samples.Length; i++)
        {
            sumSquares += samples[i] * samples[i];
        }
        return MathF.Sqrt(sumSquares / samples.Length);
    }
}
