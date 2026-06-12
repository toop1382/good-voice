using UnityEngine;

namespace Client.Network
{
    /// <summary>The transport protocol to use for voice communication.</summary>
    public enum VoiceProtocol
    {
        /// <summary>UDP — lowest latency, fire-and-forget. Best for LAN and most WAN connections. Server port 50005.</summary>
        UDP,

        /// <summary>TCP — reliable, ordered. Use when UDP is blocked. Server port 50006.</summary>
        TCP,

        /// <summary>WebSocket — HTTP-upgrade. Best for WebGL builds and clients behind HTTP proxies. Server port 50007.</summary>
        WebSocket
    }

    /// <summary>
    /// Factory that creates the correct IVoiceTransport for the selected protocol.
    ///
    /// Default ports (match server defaults):
    ///   UDP       → 50005
    ///   TCP       → 50006
    ///   WebSocket → 50007
    /// </summary>
    public static class VoiceTransportFactory
    {
        public static IVoiceTransport Create(
            VoiceProtocol protocol,
            string host,
            int port,
            int clientId)
        {
            switch (protocol)
            {
                case VoiceProtocol.UDP:
                    Debug.Log($"[VoiceTransportFactory] UDP → {host}:{port}");
                    return new UdpVoiceTransport(host, port, clientId);

                case VoiceProtocol.TCP:
                    Debug.Log($"[VoiceTransportFactory] TCP → {host}:{port}");
                    return new TcpVoiceTransport(host, port, clientId);

                case VoiceProtocol.WebSocket:
                    Debug.Log($"[VoiceTransportFactory] WebSocket → ws://{host}:{port}/voice/");
                    return new WebSocketVoiceTransport(host, port, clientId);

                default:
                    Debug.LogWarning($"[VoiceTransportFactory] Unknown protocol {protocol}, falling back to UDP.");
                    return new UdpVoiceTransport(host, port, clientId);
            }
        }

        /// <summary>Returns the default server port for each protocol.</summary>
        public static int DefaultPort(VoiceProtocol protocol) => protocol switch
        {
            VoiceProtocol.UDP       => 50005,
            VoiceProtocol.TCP       => 50006,
            VoiceProtocol.WebSocket => 50007,
            _                       => 50005
        };
    }
}
