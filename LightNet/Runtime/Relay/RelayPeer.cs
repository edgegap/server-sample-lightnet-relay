using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;

namespace LightNet.Relay
{
    /// <summary>
    /// A game's connection to the LightNet relay server, as the room host or as a client.<br/>
    /// Netcode agnostic: a transport (Mirror, FishNet, NGO, PurrNet...) calls <see cref="Poll"/> once per
    /// frame on the main thread and maps the callbacks to its own connect, data and disconnect events.
    /// </summary>
    public class RelayPeer
    {
        // host
        public Action<string> OnRoomCreated;
        public Action<uint> OnPeerJoined;
        public Action<uint> OnPeerLeft;
        public Action<uint, ArraySegment<byte>, ENetChannel> OnPeerData;

        // client
        public Action OnJoined;
        public Action<ArraySegment<byte>, ENetChannel> OnHostData;

        // both. Fired once, when the relay connection ends or could not be made (reason for logs).
        public Action<string> OnDisconnected;

        public bool IsHost { get; private set; }
        public bool IsReady { get; private set; }   // host: room created, client: joined
        public bool IsActive => service != null;
        public string RoomCode { get; private set; }

        /// <summary>Payload bytes received from other players, per channel, by every peer in this process (for tests).</summary>
        public static long TotalReceivedReliable, TotalReceivedUnreliable;

        NetworkService service;
        ConnectionHandle relay;
        string joinCode;
        bool connected;

        public void Host(string relayAddress, int tcpPort, int udpPort)
        {
            IsHost = true;
            Start(relayAddress, tcpPort, udpPort);
        }

        public void Join(string relayAddress, int tcpPort, int udpPort, string code)
        {
            IsHost = false;
            joinCode = (code ?? "").Trim().ToUpperInvariant();
            Start(relayAddress, tcpPort, udpPort);
        }

        void Start(string relayAddress, int tcpPort, int udpPort)
        {
            Close();
            IPAddress address = Resolve(relayAddress);
            service = new NetworkService();
            if (address == null || !service.StartClient(address, tcpPort, udpPort))
            {
                service = null;
                OnDisconnected?.Invoke("cannot reach relay '" + relayAddress + "'");
            }
        }

        /// <summary>Send to the host (as a client) or to <paramref name="peerId"/> (as the host).</summary>
        public void Send(uint peerId, ArraySegment<byte> payload, ENetChannel channel)
        {
            if (!IsReady) return;
            byte[] msg = IsHost ? RelayProtocol.Make(RelayMsg.ToPeer, peerId, payload) : RelayProtocol.Make(RelayMsg.ToHost, payload);
            service.SendMessage(relay, channel, msg);
        }

        public void Kick(uint peerId)
        {
            if (IsReady && IsHost) service.SendMessage(relay, ENetChannel.Reliable, RelayProtocol.Make(RelayMsg.KickPeer, peerId));
        }

        public void Close()
        {
            if (service == null) return;
            NetworkService s = service;
            service = null;
            if (s.GetState() != ENetworkState.Closed) s.Close();
            Ended("closed");
        }

        public void Poll()
        {
            if (service == null) return;

            while (service != null && service.GetNextEvent(out ConnectionEvent ev))
            {
                if (ev.EventType == ConnectionEvent.EType.Connected)
                {
                    connected = true;
                    relay = ev.Connection;
                    service.SendMessage(relay, ENetChannel.Reliable,
                        IsHost ? RelayProtocol.Make(RelayMsg.CreateRoom) : RelayProtocol.Make(RelayMsg.JoinRoom, joinCode));
                }
                else if (ev.EventType == ConnectionEvent.EType.Disconnected)
                {
                    CloseWith("relay connection lost");
                }
            }

            while (service != null && service.GetNextMessage(out byte[] msg, out ConnectionHandle _, out ENetChannel channel))
            {
                if (msg.Length > 0) Handle(msg, channel);
            }

            // LightNet closes without an event when the TCP connect fails
            if (service != null && !connected && service.GetState() == ENetworkState.Closed)
            {
                CloseWith("cannot connect to the relay");
            }
        }

        void Handle(byte[] msg, ENetChannel channel)
        {
            switch ((RelayMsg)msg[0])
            {
                case RelayMsg.RoomCreated:
                    RoomCode = RelayProtocol.ReadText(msg);
                    IsReady = true;
                    OnRoomCreated?.Invoke(RoomCode);
                    break;
                case RelayMsg.Joined:
                    RoomCode = joinCode;
                    IsReady = true;
                    OnJoined?.Invoke();
                    break;
                case RelayMsg.JoinFailed:
                    CloseWith("join failed: " + RelayProtocol.ReadText(msg));
                    break;
                case RelayMsg.RoomClosed:
                    CloseWith("the host left");
                    break;
                case RelayMsg.PeerJoined:
                    OnPeerJoined?.Invoke(RelayProtocol.ReadPeerId(msg));
                    break;
                case RelayMsg.PeerLeft:
                    OnPeerLeft?.Invoke(RelayProtocol.ReadPeerId(msg));
                    break;
                case RelayMsg.FromPeer:
                    if (IsReady)
                    {
                        ArraySegment<byte> payload = RelayProtocol.ReadPayload(msg, true);
                        Count(payload, channel);
                        OnPeerData?.Invoke(RelayProtocol.ReadPeerId(msg), payload, channel);
                    }
                    break;
                case RelayMsg.FromHost:
                    // unreliable data can overtake the reliable Joined; drop it until we are in
                    if (IsReady)
                    {
                        ArraySegment<byte> payload = RelayProtocol.ReadPayload(msg, false);
                        Count(payload, channel);
                        OnHostData?.Invoke(payload, channel);
                    }
                    break;
            }
        }

        static void Count(ArraySegment<byte> payload, ENetChannel channel)
        {
            if (channel == ENetChannel.Reliable) TotalReceivedReliable += payload.Count;
            else TotalReceivedUnreliable += payload.Count;
        }

        void CloseWith(string reason)
        {
            NetworkService s = service;
            service = null;
            if (s != null && s.GetState() != ENetworkState.Closed) s.Close();
            Ended(reason);
        }

        void Ended(string reason)
        {
            bool wasStarted = connected || IsReady || reason != "closed";
            IsReady = false;
            connected = false;
            RoomCode = null;
            if (wasStarted) OnDisconnected?.Invoke(reason);
        }

        static IPAddress Resolve(string host)
        {
            if (IPAddress.TryParse(host, out IPAddress ip)) return ip;
            try
            {
                IPAddress[] all = Dns.GetHostAddresses(host);
                return all.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? all.FirstOrDefault();
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
