using System;

namespace Client.Codec
{
    public interface IOpusDecoder : IDisposable
    {
        int Decode(byte[] data, int dataLength, float[] pcm, int decodeFec = 0);
    }
}
