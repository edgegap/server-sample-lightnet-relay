namespace LightNet
{
    /// <summary>
    /// Everything <see cref="NetworkComponent"/> needs from a network backend once it has been started.<br/>
    /// <see cref="NetworkService"/> implements it with direct TCP/UDP sockets. Other backends, like the
    /// Edgegap relay (<see cref="LightNet.Edgegap.RelayNetworkService"/>), implement it too, so the same
    /// component, events and <see cref="NetworkData"/> types work over any of them.
    /// </summary>
    public interface INetworkService
    {
        ENetworkState GetState();
        bool IsServer();
        bool Close();
        bool Disconnect(ConnectionHandle connection);
        bool IsAlive(ConnectionHandle handle);
        void SetNetworkErrorEmulation(int looseLevel, int maxSendDelay);
        ConnectionHandle[] GetConnections();
        string GetConnectionName(ConnectionHandle handle);
        bool GetNextEvent(out ConnectionEvent outEvent);
        bool SendMessage(ConnectionHandle connection, ENetChannel channel, byte[] message);
        void BroadcastMessage(ENetChannel channel, byte[] message);
        bool GetNextMessage(out byte[] message, out ConnectionHandle connection, out ENetChannel channel);
    }
}
