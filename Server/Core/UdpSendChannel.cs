using System;
using System.Net;
using System.Net.Sockets;

namespace Server.Core
{
    /// <summary>UDP ISendChannel — stateless, fire-and-forget via Socket.SendTo.</summary>
    public sealed class UdpSendChannel : ISendChannel
    {
        private readonly Socket _socket;
        private readonly IPEndPoint _remoteEndPoint;

        public string Protocol => "UDP";
        public bool IsAlive => true; // UDP is always "alive" — no connection state

        public UdpSendChannel(Socket socket, IPEndPoint remoteEndPoint)
        {
            _socket = socket;
            _remoteEndPoint = remoteEndPoint;
        }

        public void Send(byte[] data, int offset, int length)
        {
            try
            {
                _socket.SendTo(data, offset, length, SocketFlags.None, _remoteEndPoint);
            }
            catch (SocketException)
            {
                // Transient UDP send failures are non-fatal; remote will time out naturally
            }
        }
    }
}
