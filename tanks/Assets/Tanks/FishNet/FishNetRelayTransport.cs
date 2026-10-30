using System;
using System.Collections.Generic;
using FishNet.Managing;
using FishNet.Transporting;
using LightNet;
using LightNet.Relay;
using UnityEngine;

namespace Tanks.FishNetRelay
{
    /// <summary>
    /// FishNet transport that goes through the LightNet relay server.<br/>
    /// Server: creates a room on the relay (<see cref="RoomCode"/>); each relay peer is a FishNet connection.
    /// Client: joins <see cref="roomCode"/>. In host mode the local client never touches the relay: it is
    /// looped back in this transport as connection <see cref="HostClientId"/>.
    /// </summary>
    [DisallowMultipleComponent]
    public class FishNetRelayTransport : Transport
    {
        /// <summary>Connection id of the host's own client. Relay peer ids start at 1.</summary>
        public const int HostClientId = 0;

        public string relayAddress = "localhost";
        public int relayTcpPort = RelayProtocol.DefaultPort;
        public int relayUdpPort = RelayProtocol.DefaultPort;
        public string roomCode = "";

        public string RoomCode => host?.RoomCode;
        public Action<string> OnRoomCreated;

        public override event Action<ClientConnectionStateArgs> OnClientConnectionState;
        public override event Action<ServerConnectionStateArgs> OnServerConnectionState;
        public override event Action<RemoteConnectionStateArgs> OnRemoteConnectionState;
        public override event Action<ClientReceivedDataArgs> OnClientReceivedData;
        public override event Action<ServerReceivedDataArgs> OnServerReceivedData;

        RelayPeer host, client;
        LocalConnectionState serverState = LocalConnectionState.Stopped;
        LocalConnectionState clientState = LocalConnectionState.Stopped;
        bool localClient; // client is the host's own (looped back)
        readonly HashSet<int> peers = new HashSet<int>();

        readonly Queue<(int connection, byte[] data, Channel channel)> toServer = new Queue<(int, byte[], Channel)>();
        readonly Queue<(byte[] data, Channel channel)> toClient = new Queue<(byte[], Channel)>();

        // ---------------------------------------------------------------- polling

        void Update()
        {
            TanksSessions.FlushLightNetLogs();
            host?.Poll();
            client?.Poll();
        }

        public override void IterateIncoming(bool asServer)
        {
            if (asServer)
            {
                while (toServer.Count > 0)
                {
                    var (connection, data, channel) = toServer.Dequeue();
                    if (connection == HostClientId ? localClient && clientState == LocalConnectionState.Started : peers.Contains(connection))
                        HandleServerReceivedDataArgs(new ServerReceivedDataArgs(new ArraySegment<byte>(data), channel, connection, Index));
                }
            }
            else
            {
                while (toClient.Count > 0)
                {
                    var (data, channel) = toClient.Dequeue();
                    if (clientState == LocalConnectionState.Started)
                        HandleClientReceivedDataArgs(new ClientReceivedDataArgs(new ArraySegment<byte>(data), channel, Index));
                }
            }
        }

        public override void IterateOutgoing(bool asServer) { }

        // ---------------------------------------------------------------- sending

        public override void SendToServer(byte channelId, ArraySegment<byte> segment)
        {
            if (clientState != LocalConnectionState.Started) return;
            if (localClient) toServer.Enqueue((HostClientId, Copy(segment), (Channel)channelId));
            else client?.Send(0, segment, ToLightNet(channelId));
        }

        public override void SendToClient(byte channelId, ArraySegment<byte> segment, int connectionId)
        {
            if (serverState != LocalConnectionState.Started) return;
            if (connectionId == HostClientId)
            {
                if (localClient) toClient.Enqueue((Copy(segment), (Channel)channelId));
            }
            else
            {
                host?.Send((uint)connectionId, segment, ToLightNet(channelId));
            }
        }

        // ---------------------------------------------------------------- starting and stopping

        public override bool StartConnection(bool server) => server ? StartServer() : StartClient();

        public override bool StopConnection(bool server) => server ? StopServer() : StopClient();

        public override bool StopConnection(int connectionId, bool immediately)
        {
            if (connectionId == HostClientId) return StopClient();
            if (!peers.Remove(connectionId)) return false;
            host?.Kick((uint)connectionId);
            HandleRemoteConnectionState(new RemoteConnectionStateArgs(RemoteConnectionState.Stopped, connectionId, Index));
            return true;
        }

        public override void Shutdown()
        {
            StopClient();
            StopServer();
        }

        bool StartServer()
        {
            if (serverState != LocalConnectionState.Stopped) return false;
            SetServerState(LocalConnectionState.Starting);
            host = new RelayPeer
            {
                OnRoomCreated = code =>
                {
                    Debug.Log($"LightNet relay (FishNet): hosting room {code}");
                    SetServerState(LocalConnectionState.Started);
                    OnRoomCreated?.Invoke(code);
                    if (localClient && clientState == LocalConnectionState.Starting) LocalClientConnected();
                },
                OnPeerJoined = id =>
                {
                    peers.Add((int)id);
                    HandleRemoteConnectionState(new RemoteConnectionStateArgs(RemoteConnectionState.Started, (int)id, Index));
                },
                OnPeerLeft = id =>
                {
                    if (peers.Remove((int)id))
                        HandleRemoteConnectionState(new RemoteConnectionStateArgs(RemoteConnectionState.Stopped, (int)id, Index));
                },
                OnPeerData = (id, data, channel) => toServer.Enqueue(((int)id, Copy(data), ToFishNet(channel))),
            };
            host.OnDisconnected = reason =>
            {
                Debug.LogWarning($"LightNet relay (FishNet): host connection ended ({reason})");
                host = null;
                StopServer();
            };
            Debug.Log($"LightNet relay (FishNet): creating a room on {relayAddress}:{relayTcpPort}/{relayUdpPort}");
            host.Host(relayAddress, relayTcpPort, relayUdpPort);
            return true;
        }

        bool StopServer()
        {
            if (serverState == LocalConnectionState.Stopped) return false;
            SetServerState(LocalConnectionState.Stopping);
            RelayPeer h = host;
            host = null;
            h?.Close();
            foreach (int id in new List<int>(peers))
                HandleRemoteConnectionState(new RemoteConnectionStateArgs(RemoteConnectionState.Stopped, id, Index));
            peers.Clear();
            toServer.Clear();
            if (localClient) StopClient();
            SetServerState(LocalConnectionState.Stopped);
            return true;
        }

        bool StartClient()
        {
            if (clientState != LocalConnectionState.Stopped) return false;
            SetClientState(LocalConnectionState.Starting);

            if (serverState != LocalConnectionState.Stopped)
            {
                // host mode: loop back, connected once the room exists
                localClient = true;
                if (serverState == LocalConnectionState.Started) LocalClientConnected();
                return true;
            }

            localClient = false;
            client = new RelayPeer
            {
                OnJoined = () => SetClientState(LocalConnectionState.Started),
                OnHostData = (data, channel) => toClient.Enqueue((Copy(data), ToFishNet(channel))),
            };
            client.OnDisconnected = reason =>
            {
                Debug.Log($"LightNet relay (FishNet): client disconnected ({reason})");
                client = null;
                StopClient();
            };
            Debug.Log($"LightNet relay (FishNet): joining room {roomCode} via {relayAddress}:{relayTcpPort}/{relayUdpPort}");
            client.Join(relayAddress, relayTcpPort, relayUdpPort, roomCode);
            return true;
        }

        void LocalClientConnected()
        {
            SetClientState(LocalConnectionState.Started);
            HandleRemoteConnectionState(new RemoteConnectionStateArgs(RemoteConnectionState.Started, HostClientId, Index));
        }

        bool StopClient()
        {
            if (clientState == LocalConnectionState.Stopped) return false;
            bool wasStarted = clientState == LocalConnectionState.Started;
            SetClientState(LocalConnectionState.Stopping);
            RelayPeer c = client;
            client = null;
            c?.Close();
            toClient.Clear();
            if (localClient && wasStarted && serverState != LocalConnectionState.Stopped)
                HandleRemoteConnectionState(new RemoteConnectionStateArgs(RemoteConnectionState.Stopped, HostClientId, Index));
            localClient = false;
            SetClientState(LocalConnectionState.Stopped);
            return true;
        }

        void SetServerState(LocalConnectionState state)
        {
            serverState = state;
            HandleServerConnectionState(new ServerConnectionStateArgs(state, Index));
        }

        void SetClientState(LocalConnectionState state)
        {
            clientState = state;
            HandleClientConnectionState(new ClientConnectionStateArgs(state, Index));
        }

        // ---------------------------------------------------------------- state and events

        public override LocalConnectionState GetConnectionState(bool server) => server ? serverState : clientState;

        public override RemoteConnectionState GetConnectionState(int connectionId)
        {
            bool connected = connectionId == HostClientId ? localClient && clientState == LocalConnectionState.Started : peers.Contains(connectionId);
            return connected ? RemoteConnectionState.Started : RemoteConnectionState.Stopped;
        }

        public override string GetConnectionAddress(int connectionId) => connectionId == HostClientId ? "local" : "relay-peer-" + connectionId;

        public override bool IsLocalTransport(int connectionId) => connectionId == HostClientId && localClient;

        public override void HandleClientConnectionState(ClientConnectionStateArgs args) => OnClientConnectionState?.Invoke(args);
        public override void HandleServerConnectionState(ServerConnectionStateArgs args) => OnServerConnectionState?.Invoke(args);
        public override void HandleRemoteConnectionState(RemoteConnectionStateArgs args) => OnRemoteConnectionState?.Invoke(args);
        public override void HandleClientReceivedDataArgs(ClientReceivedDataArgs args) => OnClientReceivedData?.Invoke(args);
        public override void HandleServerReceivedDataArgs(ServerReceivedDataArgs args) => OnServerReceivedData?.Invoke(args);

        public override int GetMTU(byte channel) => channel == (byte)Channel.Unreliable ? RelayProtocol.MaxUnreliablePayload : RelayProtocol.MaxReliablePayload;

        public override void SetClientAddress(string address) => relayAddress = address;
        public override string GetClientAddress() => relayAddress;

        static ENetChannel ToLightNet(byte channelId) => channelId == (byte)Channel.Unreliable ? ENetChannel.Unreliable : ENetChannel.Reliable;
        static Channel ToFishNet(ENetChannel channel) => channel == ENetChannel.Unreliable ? Channel.Unreliable : Channel.Reliable;

        static byte[] Copy(ArraySegment<byte> segment)
        {
            byte[] copy = new byte[segment.Count];
            Buffer.BlockCopy(segment.Array, segment.Offset, copy, 0, segment.Count);
            return copy;
        }

        void OnDestroy() => Shutdown();
    }
}
