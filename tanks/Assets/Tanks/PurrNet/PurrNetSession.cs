using System;
using PurrNet;
using PurrNet.Transports;
using UnityEngine;

namespace Tanks.PurrNetRelay
{
    /// <summary>Starts and stops PurrNet on the LightNet relay (sits on the PurrNet NetworkManager).</summary>
    public class PurrNetSession : MonoBehaviour, ITanksSession
    {
        NetworkManager manager;
        PurrNetRelayTransport transport;

        void Awake()
        {
            manager = GetComponent<NetworkManager>();
            transport = GetComponent<PurrNetRelayTransport>();
            // the game starts it from the HUD or the command line, never on its own
            manager.startServerFlags = StartFlags.None;
            manager.startClientFlags = StartFlags.None;
        }

        public Netcode Netcode => Netcode.PurrNet;

        public void StartHost(TanksSettings relay, Action<string> onRoomCode)
        {
            Configure(relay);
            transport.OnRoomCreated = onRoomCode;
            manager.StartHost();
        }

        public void StartClient(TanksSettings relay, string code)
        {
            Configure(relay);
            transport.roomCode = code;
            manager.StartClient();
        }

        public void Stop()
        {
            if (manager.isClient) manager.StopClient();
            if (manager.isServer) manager.StopServer();
        }

        public bool IsServer => manager.isServer;
        public bool IsActive => manager.serverState != ConnectionState.Disconnected || manager.clientState != ConnectionState.Disconnected;
        public string RoomCode => transport.RoomCode;

        void Configure(TanksSettings relay)
        {
            transport.relayAddress = relay.relayAddress;
            transport.relayTcpPort = relay.relayTcpPort;
            transport.relayUdpPort = relay.relayUdpPort;
        }
    }
}
