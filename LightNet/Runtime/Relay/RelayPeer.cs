using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace LightNet.Relay
{
    /// <summary>
    /// A game's connection to the LightNet relay server, as the room host or as a client.<br/>
    /// Netcode agnostic: a transport (Mirror, FishNet, NGO, PurrNet...) calls <see cref="Poll"/> once per
    /// frame on the main thread and maps the callbacks to its own connect, data and disconnect events.<br/>
    /// The relay address picks the connection: a host name or IP uses LightNet's TCP + UDP, and a <c>ws://</c> or
    /// <c>wss://</c> URL uses one WebSocket (<see cref="RelayWebSocket"/>; the ports are then in the URL).
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

        // match role request: isHost and the match room code. The connection closes right after.
        public Action<bool, string> OnMatchAssigned;

        // all. Fired once, when the relay connection ends or could not be made (reason for logs).
        public Action<string> OnDisconnected;

        public bool IsHost { get; private set; }
        public bool IsReady { get; private set; }   // host: room created, client: joined
        public bool IsActive => service != null || ws != null;
        public string RoomCode { get; private set; }

        /// <summary>Why the last relay connection in this process ended (for games and tests), e.g. <see cref="NoPlayersJoined"/>.</summary>
        public static string LastDisconnectReason;
        public const string NoPlayersJoined = "no players joined";

        /// <summary>Payload bytes received from other players, per channel, by every peer in this process (for tests).</summary>
        public static long TotalReceivedReliable, TotalReceivedUnreliable;

        NetworkService service;   // TCP + UDP
        RelayWebSocket ws;        // or WebSocket
        ConnectionHandle relay;
        string joinCode;
        string hostCode;
        string matchTicket;
        bool connected;

        /// <summary>Create a room. <paramref name="code"/>: the match room code from <see cref="RequestMatchRole"/>, null otherwise.</summary>
        public void Host(string relayAddress, int tcpPort, int udpPort, string code = null)
        {
            IsHost = true;
            matchTicket = null;
            hostCode = string.IsNullOrEmpty(code) ? null : code.Trim().ToUpperInvariant();
            Start(relayAddress, tcpPort, udpPort);
        }

        public void Join(string relayAddress, int tcpPort, int udpPort, string code)
        {
            IsHost = false;
            matchTicket = null;
            joinCode = (code ?? "").Trim().ToUpperInvariant();
            Start(relayAddress, tcpPort, udpPort);
        }

        /// <summary>
        /// Relay started by the Edgegap matchmaker: ask whether this player hosts the match or joins it.
        /// <see cref="OnMatchAssigned"/> gets the answer, then this connection closes; host or join with the code.
        /// </summary>
        public void RequestMatchRole(string relayAddress, int tcpPort, int udpPort, string ticketId)
        {
            IsHost = false;
            matchTicket = ticketId ?? "";
            Start(relayAddress, tcpPort, udpPort);
        }

        void Start(string relayAddress, int tcpPort, int udpPort)
        {
            Close();
            if (RelayWebSocket.IsWebSocketAddress(relayAddress))
            {
                ws = new RelayWebSocket(relayAddress.Trim());
                return;
            }
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
            Transmit(channel, msg);
        }

        public void Kick(uint peerId)
        {
            if (IsReady && IsHost) Transmit(ENetChannel.Reliable, RelayProtocol.Make(RelayMsg.KickPeer, peerId));
        }

        public void Close()
        {
            if (!IsActive) return;
            DropLink();
            Ended("closed");
        }

        void Transmit(ENetChannel channel, byte[] msg)
        {
            if (service != null) service.SendMessage(relay, channel, msg);
            else ws?.Send(channel, msg);
        }

        void DropLink()
        {
            NetworkService s = service;
            service = null;
            if (s != null && s.GetState() != ENetworkState.Closed) s.Close();
            ws?.Close();
            ws = null;
        }

        byte[] Hello()
        {
            return matchTicket != null ? RelayProtocol.Make(RelayMsg.MatchRole, matchTicket)
                : IsHost ? (hostCode != null ? RelayProtocol.Make(RelayMsg.CreateRoom, hostCode) : RelayProtocol.Make(RelayMsg.CreateRoom))
                : RelayProtocol.Make(RelayMsg.JoinRoom, joinCode);
        }

        public void Poll()
        {
            if (ws != null)
            {
                if (!connected && ws.IsOpen)
                {
                    connected = true;
                    ws.Send(ENetChannel.Reliable, Hello());
                }
                while (ws != null && ws.TryReceive(out byte[] wsMsg, out ENetChannel wsChannel))
                {
                    if (wsMsg.Length > 0) Handle(wsMsg, wsChannel);
                }
                if (ws != null && ws.IsClosed) CloseWith(ws.CloseReason);
                return;
            }
            if (service == null) return;

            while (service != null && service.GetNextEvent(out ConnectionEvent ev))
            {
                if (ev.EventType == ConnectionEvent.EType.Connected)
                {
                    connected = true;
                    relay = ev.Connection;
                    service.SendMessage(relay, ENetChannel.Reliable, Hello());
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
                case RelayMsg.MatchAssigned:
                    if (matchTicket != null && msg.Length > 1)
                    {
                        bool isHost = msg[1] == 1;
                        string code = Encoding.ASCII.GetString(msg, 2, msg.Length - 2);
                        // done with this connection: close it without OnDisconnected
                        DropLink();
                        connected = false;
                        OnMatchAssigned?.Invoke(isHost, code);
                    }
                    break;
                case RelayMsg.JoinFailed:
                    CloseWith("join failed: " + RelayProtocol.ReadText(msg));
                    break;
                case RelayMsg.RoomClosed:
                    CloseWith("the host left");
                    break;
                case RelayMsg.RoomTimedOut:
                    CloseWith(NoPlayersJoined);
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
            DropLink();
            Ended(reason);
        }

        void Ended(string reason)
        {
            bool wasStarted = connected || IsReady || reason != "closed";
            if (wasStarted) LastDisconnectReason = reason;
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
