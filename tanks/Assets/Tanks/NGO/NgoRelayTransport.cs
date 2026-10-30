using System;
using System.Collections.Generic;
using LightNet;
using LightNet.Relay;
using Unity.Netcode;
using UnityEngine;

namespace Tanks.NgoRelay
{
    /// <summary>
    /// Netcode for GameObjects transport that goes through the LightNet relay server.<br/>
    /// Server: creates a room on the relay (<see cref="RoomCode"/>); relay peer ids are the transport client ids.
    /// Client: joins <see cref="roomCode"/>. NGO runs the host's own client without the transport.
    /// </summary>
    [DisallowMultipleComponent]
    public class NgoRelayTransport : NetworkTransport
    {
        public string relayAddress = "localhost";
        public int relayTcpPort = RelayProtocol.DefaultPort;
        public int relayUdpPort = RelayProtocol.DefaultPort;
        public string roomCode = "";

        public string RoomCode => host?.RoomCode;
        public Action<string> OnRoomCreated;

        // relay peer ids start at 1, so 0 is free for the server
        public override ulong ServerClientId => 0;

        RelayPeer host, client;
        int polledFrame = -1;
        readonly Queue<(NetworkEvent type, ulong clientId, ArraySegment<byte> payload)> events = new Queue<(NetworkEvent, ulong, ArraySegment<byte>)>();

        public override void Initialize(NetworkManager networkManager = null)
        {
            if (networkManager == null) return;
            // keep NGO messages within what the relay forwards in one piece
            networkManager.MaximumTransmissionUnitSize = RelayProtocol.MaxUnreliablePayload;
            networkManager.MaximumFragmentedMessageSize = RelayProtocol.MaxReliablePayload;
        }

        public override bool StartServer()
        {
            host = new RelayPeer
            {
                OnRoomCreated = code =>
                {
                    Debug.Log($"LightNet relay (NGO): hosting room {code}");
                    OnRoomCreated?.Invoke(code);
                },
                OnPeerJoined = id => events.Enqueue((NetworkEvent.Connect, id, default)),
                OnPeerLeft = id => events.Enqueue((NetworkEvent.Disconnect, id, default)),
                OnPeerData = (id, data, channel) => events.Enqueue((NetworkEvent.Data, id, data)),
            };
            host.OnDisconnected = reason =>
            {
                Debug.LogWarning($"LightNet relay (NGO): host connection ended ({reason})");
                host = null;
                events.Enqueue((NetworkEvent.TransportFailure, ServerClientId, default));
            };
            Debug.Log($"LightNet relay (NGO): creating a room on {relayAddress}:{relayTcpPort}/{relayUdpPort}");
            host.Host(relayAddress, relayTcpPort, relayUdpPort);
            return true;
        }

        public override bool StartClient()
        {
            client = new RelayPeer
            {
                OnJoined = () => events.Enqueue((NetworkEvent.Connect, ServerClientId, default)),
                OnHostData = (data, channel) => events.Enqueue((NetworkEvent.Data, ServerClientId, data)),
            };
            client.OnDisconnected = reason =>
            {
                Debug.Log($"LightNet relay (NGO): client disconnected ({reason})");
                client = null;
                events.Enqueue((NetworkEvent.Disconnect, ServerClientId, default));
            };
            Debug.Log($"LightNet relay (NGO): joining room {roomCode} via {relayAddress}:{relayTcpPort}/{relayUdpPort}");
            client.Join(relayAddress, relayTcpPort, relayUdpPort, roomCode);
            return true;
        }

        public override NetworkEvent PollEvent(out ulong clientId, out ArraySegment<byte> payload, out float receiveTime)
        {
            // NGO calls this until it gets Nothing; read the relay once per frame
            if (events.Count == 0 && polledFrame != Time.frameCount)
            {
                polledFrame = Time.frameCount;
                TanksSessions.FlushLightNetLogs();
                host?.Poll();
                client?.Poll();
            }

            receiveTime = Time.realtimeSinceStartup;
            if (events.Count == 0)
            {
                clientId = 0;
                payload = default;
                return NetworkEvent.Nothing;
            }
            var ev = events.Dequeue();
            clientId = ev.clientId;
            payload = ev.payload;
            return ev.type;
        }

        public override void Send(ulong clientId, ArraySegment<byte> payload, NetworkDelivery networkDelivery)
        {
            ENetChannel channel = networkDelivery == NetworkDelivery.Unreliable || networkDelivery == NetworkDelivery.UnreliableSequenced
                ? ENetChannel.Unreliable
                : ENetChannel.Reliable;
            if (host != null) host.Send((uint)clientId, payload, channel);
            else client?.Send(0, payload, channel);
        }

        public override void DisconnectRemoteClient(ulong clientId) => host?.Kick((uint)clientId);

        public override void DisconnectLocalClient()
        {
            RelayPeer c = client;
            client = null;
            c?.Close();
        }

        public override ulong GetCurrentRtt(ulong clientId) => 0;

        public override void Shutdown()
        {
            RelayPeer h = host, c = client;
            host = client = null;
            h?.Close();
            c?.Close();
            events.Clear();
        }
    }
}
