namespace MockClient.Network;

using System;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using global::MockClient.Models;

public class DegradationConfig
{
    public double PacketLossRate { get; set; } // 0.0 to 1.0 (e.g. 0.10 for 10% packet drop)
    public int ArtificialDelayMs { get; set; }  // Extra delay added to sends in milliseconds
}

public class MockClientUdpSocket : IDisposable
{
    private readonly Socket _socket;
    private readonly IPEndPoint _serverEndPoint;
    private readonly byte[] _receiveBuffer;
    private bool _isRunning;
    private Task? _receiveTask;
    private readonly Random _random = new Random();

    public DegradationConfig? Degradation { get; set; }

    public event Action<VoicePacket, IPEndPoint>? OnPacketReceived;
    public event Action<Exception>? OnError;

    public MockClientUdpSocket(IPEndPoint serverEndPoint)
    {
        _serverEndPoint = serverEndPoint;
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        _socket.Bind(new IPEndPoint(IPAddress.Any, 0));
        _receiveBuffer = new byte[65535];
    }

    public void Start()
    {
        if (_isRunning) return;
        _isRunning = true;
        _receiveTask = Task.Run(ReceiveLoopAsync);
    }

    public async Task SendPacketAsync(VoicePacket packet)
    {
        // Simulate packet loss on send
        if (Degradation != null && Degradation.PacketLossRate > 0)
        {
            if (_random.NextDouble() < Degradation.PacketLossRate)
            {
                // Drop packet silently
                return;
            }
        }

        // Simulate latency delay
        if (Degradation != null && Degradation.ArtificialDelayMs > 0)
        {
            await Task.Delay(Degradation.ArtificialDelayMs);
        }

        var buffer = new byte[VoicePacket.HeaderSize + packet.PayloadLength];
        packet.Serialize(buffer);
        await _socket.SendToAsync(new ArraySegment<byte>(buffer), SocketFlags.None, _serverEndPoint);
    }

    private async Task ReceiveLoopAsync()
    {
        var remoteEP = (EndPoint)new IPEndPoint(IPAddress.Any, 0);
        while (_isRunning)
        {
            try
            {
                var result = await _socket.ReceiveFromAsync(new ArraySegment<byte>(_receiveBuffer), SocketFlags.None, remoteEP);

                // Simulate loss on receipt
                if (Degradation != null && Degradation.PacketLossRate > 0)
                {
                    if (_random.NextDouble() < Degradation.PacketLossRate)
                    {
                        continue; // Drop packet
                    }
                }

                ProcessReceivedPacket(result);
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.OperationAborted || !_isRunning)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception ex)
            {
                if (_isRunning)
                {
                    OnError?.Invoke(ex);
                }
            }
        }
    }

    private void ProcessReceivedPacket(SocketReceiveFromResult result)
    {
        if (result.ReceivedBytes >= VoicePacket.HeaderSize)
        {
            var receivedSpan = _receiveBuffer.AsSpan(0, result.ReceivedBytes);
            var packet = VoicePacket.Deserialize(receivedSpan);
            OnPacketReceived?.Invoke(packet, (IPEndPoint)result.RemoteEndPoint);
        }
    }

    public void Dispose()
    {
        _isRunning = false;
        try
        {
            _socket.Close();
        }
        catch
        {
            // Suppress close errors
        }
        _socket.Dispose();
    }
}
