using System;
using LightNet;
using LightNet.Relay;
using Mirror;
using UnityEngine;

namespace Tanks.MirrorRelay
{
    /// <summary>
    /// Mirror transport that goes through the LightNet relay server instead of direct sockets.<br/>
    /// Host: <see cref="ServerStart"/> creates a room on the relay and gets a code (<see cref="RoomCode"/>).
    /// Clients: set <see cref="roomCode"/>, then connect to the relay address (NetworkManager.networkAddress).
    /// Nobody opens a port: host and clients are all clients of the relay.
    /// </summary>
    [DisallowMultipleComponent]
    public class MirrorRelayTransport : Transport
    {
        [Tooltip("Relay address the host uses. Empty: NetworkManager.networkAddress.")]
        public string relayAddress = "";
        [Tooltip("Relay TCP port (reliable channel).")]
        public int relayTcpPort = RelayProtocol.DefaultPort;
        [Tooltip("Relay UDP port (unreliable channel).")]
        public int relayUdpPort = RelayProtocol.DefaultPort;
        [Tooltip("Code of the room to join (clients).")]
        public string roomCode = "";

        /// <summary>The room code once hosting (share it with the other players).</summary>
        public string RoomCode => host?.RoomCode;
        public Action<string> OnRoomCreated;

        RelayPeer host;
        RelayPeer client;

        public override bool Available()
        {
            return Application.platform != RuntimePlatform.WebGLPlayer;
        }

        // ---------------------------------------------------------------- client

        public override bool ClientConnected() => client != null && client.IsReady;

        public override void ClientConnect(string address)
        {
            client = new RelayPeer
            {
                OnJoined = () => OnClientConnected.Invoke(),
                OnHostData = (data, channel) => OnClientDataReceived.Invoke(data, ToMirror(channel)),
            };
            client.OnDisconnected = reason =>
            {
                Debug.Log($"LightNet relay: client disconnected ({reason})");
                client = null;
                OnClientDisconnected.Invoke();
            };
            Debug.Log($"LightNet relay: joining room {roomCode} via {address}:{relayTcpPort}/{relayUdpPort}");
            client.Join(address, relayTcpPort, relayUdpPort, roomCode);
        }

        public override void ClientSend(ArraySegment<byte> segment, int channelId = Channels.Reliable)
        {
            client?.Send(0, segment, ToLightNet(channelId));
            OnClientDataSent?.Invoke(segment, channelId);
        }

        public override void ClientDisconnect()
        {
            client?.Close();
        }

        public override void ClientEarlyUpdate()
        {
            TanksSessions.FlushLightNetLogs();
            client?.Poll();
        }

        // ---------------------------------------------------------------- server (the hosting player)

        public override Uri ServerUri()
        {
            return new UriBuilder("lightnet", HostRelayAddress(), relayTcpPort) { Query = "room=" + RoomCode }.Uri;
        }

        public override bool ServerActive() => host != null && host.IsActive;

        public override void ServerStart()
        {
            host = new RelayPeer
            {
                OnRoomCreated = code =>
                {
                    Debug.Log($"LightNet relay: hosting room {code}");
                    OnRoomCreated?.Invoke(code);
                },
                OnPeerJoined = id => OnServerConnected.Invoke((int)id),
                OnPeerLeft = id => OnServerDisconnected.Invoke((int)id),
                OnPeerData = (id, data, channel) => OnServerDataReceived.Invoke((int)id, data, ToMirror(channel)),
            };
            host.OnDisconnected = reason => Debug.LogWarning($"LightNet relay: host connection ended ({reason})");
            Debug.Log($"LightNet relay: creating a room on {HostRelayAddress()}:{relayTcpPort}/{relayUdpPort}");
            host.Host(HostRelayAddress(), relayTcpPort, relayUdpPort);
        }

        public override void ServerSend(int connectionId, ArraySegment<byte> segment, int channelId = Channels.Reliable)
        {
            host?.Send((uint)connectionId, segment, ToLightNet(channelId));
            OnServerDataSent?.Invoke(connectionId, segment, channelId);
        }

        public override void ServerDisconnect(int connectionId)
        {
            host?.Kick((uint)connectionId);
        }

        public override string ServerGetClientAddress(int connectionId)
        {
            return "relay-peer-" + connectionId;
        }

        public override void ServerStop()
        {
            host?.Close();
            host = null;
        }

        public override void ServerEarlyUpdate()
        {
            TanksSessions.FlushLightNetLogs();
            host?.Poll();
        }

        // ---------------------------------------------------------------- common

        public override int GetMaxPacketSize(int channelId = Channels.Reliable)
        {
            return channelId == Channels.Unreliable ? RelayProtocol.MaxUnreliablePayload : RelayProtocol.MaxReliablePayload;
        }

        public override void Shutdown()
        {
            client?.Close();
            client = null;
            ServerStop();
        }

        public override string ToString() => $"LightNet relay {HostRelayAddress()}:{relayTcpPort}/{relayUdpPort}";

        string HostRelayAddress()
        {
            if (!string.IsNullOrEmpty(relayAddress)) return relayAddress;
            return NetworkManager.singleton != null ? NetworkManager.singleton.networkAddress : "localhost";
        }

        static ENetChannel ToLightNet(int mirrorChannel) => mirrorChannel == Channels.Unreliable ? ENetChannel.Unreliable : ENetChannel.Reliable;
        static int ToMirror(ENetChannel channel) => channel == ENetChannel.Unreliable ? Channels.Unreliable : Channels.Reliable;

    }
}
