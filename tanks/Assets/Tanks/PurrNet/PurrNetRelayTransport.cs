using System;
using System.Collections.Generic;
using LightNet;
using LightNet.Relay;
using PurrNet.Transports;
using UnityEngine;

namespace Tanks.PurrNetRelay
{
    /// <summary>
    /// PurrNet transport that goes through the LightNet relay server.<br/>
    /// Server: creates a room on the relay (<see cref="RoomCode"/>); relay peer N is connection N + 1.
    /// Client: joins <see cref="roomCode"/>. In host mode the local client never touches the relay: it is
    /// looped back in this transport as connection <see cref="HostClientId"/>.
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(-100)]
    public class PurrNetRelayTransport : GenericTransport, ITransport
    {
        public const int HostClientId = 1;

        public string relayAddress = "localhost";
        public int relayTcpPort = RelayProtocol.DefaultPort;
        public int relayUdpPort = RelayProtocol.DefaultPort;
        public string roomCode = "";

        public string RoomCode => host?.RoomCode;
        public Action<string> OnRoomCreated;

        public event OnConnected onConnected;
        public event OnDisconnected onDisconnected;
        public event OnDataReceived onDataReceived;
        public event OnDataSent onDataSent;
        public event OnConnectionState onConnectionState;

        public override bool isSupported => Application.platform != RuntimePlatform.WebGLPlayer;
        public override ITransport transport => this;

        public ConnectionState listenerState { get; private set; } = ConnectionState.Disconnected;
        public ConnectionState clientState { get; private set; } = ConnectionState.Disconnected;

        readonly List<Connection> serverConnections = new List<Connection>();
        public IReadOnlyList<Connection> connections => serverConnections;

        static readonly Connection ServerConnection = new Connection(0);

        RelayPeer host, client;
        bool localClient;
        readonly Queue<(Connection from, byte[] data)> toServer = new Queue<(Connection, byte[])>();
        readonly Queue<byte[]> toClient = new Queue<byte[]>();

        static Connection FromPeer(uint peerId) => new Connection((int)peerId + 1);
        static uint ToPeer(Connection conn) => (uint)(conn.connectionId - 1);

        // ---------------------------------------------------------------- server

        protected override void StartServerInternal() => Listen(0);

        public void Listen(ushort port)
        {
            if (listenerState != ConnectionState.Disconnected) return;
            SetServerState(ConnectionState.Connecting);
            host = new RelayPeer
            {
                OnRoomCreated = code =>
                {
                    Debug.Log($"LightNet relay (PurrNet): hosting room {code}");
                    SetServerState(ConnectionState.Connected);
                    OnRoomCreated?.Invoke(code);
                    if (localClient && clientState == ConnectionState.Connecting) LocalClientConnected();
                },
                OnPeerJoined = id =>
                {
                    Connection conn = FromPeer(id);
                    serverConnections.Add(conn);
                    onConnected?.Invoke(conn, true);
                },
                OnPeerLeft = id =>
                {
                    Connection conn = FromPeer(id);
                    if (serverConnections.Remove(conn)) onDisconnected?.Invoke(conn, DisconnectReason.ClientRequest, true);
                },
                OnPeerData = (id, data, channel) => onDataReceived?.Invoke(FromPeer(id), new ByteData(data), true),
            };
            host.OnDisconnected = reason =>
            {
                Debug.LogWarning($"LightNet relay (PurrNet): host connection ended ({reason})");
                host = null;
                StopListening();
            };
            Debug.Log($"LightNet relay (PurrNet): creating a room on {relayAddress}:{relayTcpPort}/{relayUdpPort}");
            host.Host(relayAddress, relayTcpPort, relayUdpPort);
        }

        public void StopListening()
        {
            if (listenerState == ConnectionState.Disconnected) return;
            SetServerState(ConnectionState.Disconnecting);
            RelayPeer h = host;
            host = null;
            h?.Close();
            if (localClient) Disconnect();
            foreach (Connection conn in serverConnections.ToArray())
                onDisconnected?.Invoke(conn, DisconnectReason.ServerRequest, true);
            serverConnections.Clear();
            toServer.Clear();
            SetServerState(ConnectionState.Disconnected);
        }

        public void SendToClient(Connection target, ByteData data, Channel method = Channel.ReliableOrdered)
        {
            if (listenerState != ConnectionState.Connected) return;
            if (target.connectionId == HostClientId)
            {
                if (localClient) toClient.Enqueue(data.span.ToArray());
            }
            else
            {
                host?.Send(ToPeer(target), data.segment, ToLightNet(method));
            }
            RaiseDataSent(target, data, true);
        }

        public void CloseConnection(Connection conn)
        {
            if (conn.connectionId == HostClientId)
            {
                Disconnect();
                return;
            }
            if (!serverConnections.Remove(conn)) return;
            host?.Kick(ToPeer(conn));
            onDisconnected?.Invoke(conn, DisconnectReason.ServerRequest, true);
        }

        // ---------------------------------------------------------------- client

        protected override void StartClientInternal() => Connect(relayAddress, (ushort)relayTcpPort);

        public void Connect(string ip, ushort port)
        {
            if (clientState != ConnectionState.Disconnected) return;
            SetClientState(ConnectionState.Connecting);

            if (listenerState != ConnectionState.Disconnected)
            {
                // host mode: loop back, connected once the room exists
                localClient = true;
                if (listenerState == ConnectionState.Connected) LocalClientConnected();
                return;
            }

            localClient = false;
            client = new RelayPeer
            {
                OnJoined = () =>
                {
                    SetClientState(ConnectionState.Connected);
                    onConnected?.Invoke(ServerConnection, false);
                },
                OnHostData = (data, channel) => onDataReceived?.Invoke(ServerConnection, new ByteData(data), false),
            };
            client.OnDisconnected = reason =>
            {
                Debug.Log($"LightNet relay (PurrNet): client disconnected ({reason})");
                client = null;
                Disconnect();
            };
            Debug.Log($"LightNet relay (PurrNet): joining room {roomCode} via {relayAddress}:{relayTcpPort}/{relayUdpPort}");
            client.Join(relayAddress, relayTcpPort, relayUdpPort, roomCode);
        }

        void LocalClientConnected()
        {
            var conn = new Connection(HostClientId);
            serverConnections.Add(conn);
            SetClientState(ConnectionState.Connected);
            onConnected?.Invoke(conn, true);
            onConnected?.Invoke(ServerConnection, false);
        }

        public void Disconnect()
        {
            if (clientState == ConnectionState.Disconnected) return;
            bool wasConnected = clientState == ConnectionState.Connected;
            SetClientState(ConnectionState.Disconnecting);
            RelayPeer c = client;
            client = null;
            c?.Close();
            toClient.Clear();
            if (localClient && wasConnected)
            {
                var conn = new Connection(HostClientId);
                if (serverConnections.Remove(conn)) onDisconnected?.Invoke(conn, DisconnectReason.ClientRequest, true);
            }
            localClient = false;
            SetClientState(ConnectionState.Disconnected);
            if (wasConnected) onDisconnected?.Invoke(ServerConnection, DisconnectReason.ClientRequest, false);
        }

        public void SendToServer(ByteData data, Channel method = Channel.ReliableOrdered)
        {
            if (clientState != ConnectionState.Connected) return;
            if (localClient) toServer.Enqueue((new Connection(HostClientId), data.span.ToArray()));
            else client?.Send(0, data.segment, ToLightNet(method));
            RaiseDataSent(ServerConnection, data, false);
        }

        // ---------------------------------------------------------------- pumping

        public void ReceiveMessages(float delta)
        {
            TanksSessions.FlushLightNetLogs();
            host?.Poll();
            client?.Poll();

            while (toServer.Count > 0)
            {
                var (from, data) = toServer.Dequeue();
                onDataReceived?.Invoke(from, new ByteData(data, 0, data.Length), true);
            }
            while (toClient.Count > 0)
            {
                byte[] data = toClient.Dequeue();
                onDataReceived?.Invoke(ServerConnection, new ByteData(data, 0, data.Length), false);
            }
        }

        public void SendMessages(float delta) { }

        public void RaiseDataReceived(Connection conn, ByteData data, bool asServer) => onDataReceived?.Invoke(conn, data, asServer);
        public void RaiseDataSent(Connection conn, ByteData data, bool asServer) => onDataSent?.Invoke(conn, data, asServer);

        public int GetMTU(Connection target, Channel channel, bool asServer)
        {
            return ToLightNet(channel) == ENetChannel.Unreliable ? RelayProtocol.MaxUnreliablePayload : RelayProtocol.MaxReliablePayload;
        }

        // ---------------------------------------------------------------- helpers

        ConnectionState prevServer = ConnectionState.Disconnected, prevClient = ConnectionState.Disconnected;

        void SetServerState(ConnectionState state)
        {
            listenerState = state;
            if (prevServer == state) return;
            prevServer = state;
            onConnectionState?.Invoke(state, true);
        }

        void SetClientState(ConnectionState state)
        {
            clientState = state;
            if (prevClient == state) return;
            prevClient = state;
            onConnectionState?.Invoke(state, false);
        }

        static ENetChannel ToLightNet(Channel channel) =>
            channel == Channel.Unreliable || channel == Channel.UnreliableSequenced ? ENetChannel.Unreliable : ENetChannel.Reliable;

        void OnDestroy()
        {
            Disconnect();
            StopListening();
        }
    }
}
