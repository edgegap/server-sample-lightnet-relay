using System;
using Unity.Netcode;
using UnityEngine;

namespace Tanks.NgoRelay
{
    /// <summary>Starts and stops Netcode for GameObjects on the LightNet relay (sits on the NGO NetworkManager).</summary>
    public class NgoSession : MonoBehaviour, ITanksSession
    {
        NetworkManager manager;
        NgoRelayTransport transport;

        void Awake()
        {
            manager = GetComponent<NetworkManager>();
            transport = GetComponent<NgoRelayTransport>();
        }

        public Netcode Netcode => Netcode.NGO;

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

        public void Stop() => manager.Shutdown();

        public bool IsServer => manager.IsServer;
        public bool IsActive => manager.IsListening;
        public string RoomCode => transport.RoomCode;

        void Configure(TanksSettings relay)
        {
            transport.relayAddress = relay.relayAddress;
            transport.relayTcpPort = relay.relayTcpPort;
            transport.relayUdpPort = relay.relayUdpPort;
        }
    }
}
