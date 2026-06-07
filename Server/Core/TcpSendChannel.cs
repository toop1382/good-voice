using System;
using System.Buffers.Binary;
using System.IO;
using System.Net.Sockets;

namespace Server.Core
{
    /// <summary>
    /// TCP ISendChannel — writes length-prefixed frames to a NetworkStream.
    /// Frame format: [4-byte uint32 LE payload length][payload bytes]
    /// Thread-safe via lock on the stream.
    /// </summary>
    public sealed class TcpSendChannel : ISendChannel
    {
        private readonly NetworkStream _stream;
        private readonly byte[] _lenBuf = new byte[4];
        private readonly object _writeLock = new();
        private volatile bool _alive = true;

        public string Protocol => "TCP";
        public bool IsAlive => _alive && _stream.Socket.Connected;

        public TcpSendChannel(NetworkStream stream)
        {
            _stream = stream;
        }

        public void Send(byte[] data, int offset, int length)
        {
            if (!_alive) return;
            try
            {
                lock (_writeLock)
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(_lenBuf, (uint)length);
                    _stream.Write(_lenBuf, 0, 4);
                    _stream.Write(data, offset, length);
                }
            }
            catch (IOException)
            {
                _alive = false;
            }
            catch (SocketException)
            {
                _alive = false;
            }
            catch (ObjectDisposedException)
            {
                _alive = false;
            }
        }
    }
}
