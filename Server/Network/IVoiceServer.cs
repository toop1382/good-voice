namespace Server.Network
{
    /// <summary>Common interface for all voice server transport implementations.</summary>
    public interface IVoiceServer
    {
        string ProtocolName { get; }
        void Start();
        void Stop();
    }
}
