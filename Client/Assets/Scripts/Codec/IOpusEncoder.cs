using System;

namespace Client.Codec
{
    public interface IOpusEncoder : IDisposable
    {
        int Bitrate { get; set; }
        int Complexity { get; set; }
        int Encode(float[] pcm, byte[] output);
    }
}
