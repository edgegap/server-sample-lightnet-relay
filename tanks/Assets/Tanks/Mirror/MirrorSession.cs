using System;
using System.Reflection;
using Mirror;
using UnityEngine;

namespace Tanks.MirrorRelay
{
    /// <summary>Starts and stops Mirror on the LightNet relay (sits on the Mirror NetworkManager).</summary>
    public class MirrorSession : MonoBehaviour, ITanksSession
    {
        NetworkManager manager;
        MirrorRelayTransport transport;

        void Awake()
        {
            manager = GetComponent<NetworkManager>();
            transport = GetComponent<MirrorRelayTransport>();
            // NetworkManager.transport is protected in this Mirror version, and StartHost/StartClient
            // copy it into Transport.activeTransport, so make sure it is the relay
            typeof(NetworkManager).GetField("transport", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                .SetValue(manager, transport);
            Transport.activeTransport = transport;
        }

        public Netcode Netcode => Netcode.Mirror;

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
            if (NetworkServer.active && NetworkClient.isConnected) manager.StopHost();
            else if (NetworkServer.active) manager.StopServer();
            else if (NetworkClient.active) manager.StopClient();
        }

        public bool IsServer => NetworkServer.active;
        public bool IsActive => NetworkServer.active || NetworkClient.active;
        public string RoomCode => transport.RoomCode;

        void Configure(TanksSettings relay)
        {
            manager.networkAddress = relay.relayAddress;
            transport.relayAddress = relay.relayAddress;
            transport.relayTcpPort = relay.relayTcpPort;
            transport.relayUdpPort = relay.relayUdpPort;
        }
    }
}
