using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using LightNet;
using LightNet.Relay;

// LightNet relay server: one player's game hosts a room, the others join it with the room code.
// Everyone is a LightNet client of this server (TCP for reliable, UDP for unreliable), so nobody
// needs an open port. The server forwards client traffic to the host and host traffic to the client
// it is addressed to, on the channel it came in on.
//
// Environment: RELAY_TCP_PORT (default 7777), RELAY_UDP_PORT (default 7777), RELAY_MAX_PEERS (default 256)
// Exits when it gets SIGTERM / Ctrl+C, or after RELAY_IDLE_EXIT_SECONDS without any room (default 600, 0 = never).
//
// Match mode: when the Edgegap matchmaker starts the relay for a match, it injects MM_TICKET_IDS (the
// matched players' tickets). The relay then hosts one room for that match: the first matched player to
// ask (MatchRole) hosts it, the others join it, and nobody else gets the room code. Joins that arrive
// before the host has created the room wait for it. The relay stops when the match room closes, or
// when nobody created it within RELAY_IDLE_EXIT_SECONDS (default 60 in match mode).
// On Edgegap the relay stops its own deployment (ARBITRIUM_DELETE_URL) when it exits on its own.

class RelayServer
{
    class Room
    {
        public string Code;
        public ulong Host;
        public HashSet<ulong> Clients = new HashSet<ulong>();
    }

    readonly NetworkService net = new NetworkService();
    readonly Dictionary<string, Room> rooms = new Dictionary<string, Room>();
    readonly Dictionary<ulong, Room> roomOf = new Dictionary<ulong, Room>();
    long forwardedReliable, forwardedUnreliable;

    // match mode
    HashSet<string> matchTickets;
    string matchCode;
    string matchHostTicket;
    bool matchStarted, matchOver;
    readonly List<ulong> waitingJoins = new List<ulong>();

    static int Main()
    {
        int tcpPort = Env("RELAY_TCP_PORT", RelayProtocol.DefaultPort);
        int udpPort = Env("RELAY_UDP_PORT", RelayProtocol.DefaultPort);
        int maxPeers = Env("RELAY_MAX_PEERS", 256);
        var server = new RelayServer();
        server.ReadMatch();
        int idleExit = Env("RELAY_IDLE_EXIT_SECONDS", server.matchTickets != null ? 60 : 600);
        int code = server.Run(tcpPort, udpPort, maxPeers, idleExit, out bool stoppedItself);
        if (stoppedItself) StopDeployment();
        return code;
    }

    void ReadMatch()
    {
        string ids = Environment.GetEnvironmentVariable("MM_TICKET_IDS");
        if (string.IsNullOrWhiteSpace(ids)) return;
        try
        {
            matchTickets = new HashSet<string>(JsonSerializer.Deserialize<string[]>(ids));
        }
        catch (JsonException)
        {
            Console.Error.WriteLine($"MM_TICKET_IDS is not a JSON array of ticket ids: {ids}");
            return;
        }
        matchCode = NewCode();
        Console.WriteLine($"match mode: match {Environment.GetEnvironmentVariable("MM_MATCH_ID") ?? "?"}, {matchTickets.Count} matched player(s)");
    }

    int Run(int tcpPort, int udpPort, int maxPeers, int idleExitSeconds, out bool stoppedItself)
    {
        stoppedItself = false;
        if (!net.StartServer(tcpPort, udpPort, maxPeers))
        {
            FlushLogs();
            Console.Error.WriteLine($"Cannot listen on TCP {tcpPort} / UDP {udpPort}");
            return 1;
        }
        Console.WriteLine($"LightNet relay listening on TCP {tcpPort}, UDP {udpPort}, max {maxPeers} peers");

        bool stop = false;
        Console.CancelKeyPress += (s, e) => { e.Cancel = true; stop = true; };
        AppDomain.CurrentDomain.ProcessExit += (s, e) => stop = true;

        DateTime lastActivity = DateTime.UtcNow;
        DateTime lastStats = DateTime.UtcNow;
        while (!stop)
        {
            FlushLogs();
            while (net.GetNextEvent(out ConnectionEvent ev))
            {
                ulong id = ev.Connection.GetInternalHandle();
                if (ev.EventType == ConnectionEvent.EType.Connected) Console.WriteLine($"peer {id} connected ({net.GetConnectionName(ev.Connection)})");
                else if (ev.EventType == ConnectionEvent.EType.Disconnected) Left(id);
            }
            while (net.GetNextMessage(out byte[] msg, out ConnectionHandle from, out ENetChannel channel))
            {
                if (msg.Length > 0) Handle(from.GetInternalHandle(), msg, channel);
            }

            if (matchOver)
            {
                Console.WriteLine("Match over, exiting");
                stoppedItself = true;
                break;
            }
            if (rooms.Count > 0) lastActivity = DateTime.UtcNow;
            if (idleExitSeconds > 0 && (DateTime.UtcNow - lastActivity).TotalSeconds > idleExitSeconds)
            {
                Console.WriteLine($"No room for {idleExitSeconds} s, exiting");
                stoppedItself = true;
                break;
            }
            if ((DateTime.UtcNow - lastStats).TotalSeconds >= 10)
            {
                lastStats = DateTime.UtcNow;
                Console.WriteLine($"stats: rooms={rooms.Count} peers={roomOf.Count} forwarded reliable={forwardedReliable} unreliable={forwardedUnreliable}");
            }
            Thread.Sleep(1);
        }

        net.Close();
        Thread.Sleep(500);
        FlushLogs();
        return 0;
    }

    void Handle(ulong from, byte[] msg, ENetChannel channel)
    {
        roomOf.TryGetValue(from, out Room room);
        switch ((RelayMsg)msg[0])
        {
            case RelayMsg.MatchRole:
            {
                string ticket = RelayProtocol.ReadText(msg).Trim();
                if (matchTickets == null || !matchTickets.Contains(ticket))
                {
                    Send(from, ENetChannel.Reliable, RelayProtocol.Make(RelayMsg.JoinFailed, matchTickets == null ? "not a match relay" : "ticket not in this match"));
                    return;
                }
                // the first matched player to ask hosts; asking again gives the same answer
                matchHostTicket ??= ticket;
                bool isHost = ticket == matchHostTicket;
                byte[] reply = RelayProtocol.Make(RelayMsg.MatchAssigned, " " + matchCode);
                reply[1] = (byte)(isHost ? 1 : 0);
                Send(from, ENetChannel.Reliable, reply);
                Console.WriteLine($"peer {from}: ticket {ticket} {(isHost ? "hosts" : "joins")} the match");
                break;
            }

            case RelayMsg.CreateRoom:
                if (room != null) return;
                string wanted = RelayProtocol.ReadText(msg).Trim().ToUpperInvariant();
                if (matchTickets != null && (wanted != matchCode || matchStarted))
                {
                    // a match relay only hosts its match room, once, for the player MatchRole picked
                    Send(from, ENetChannel.Reliable, RelayProtocol.Make(RelayMsg.JoinFailed, "this relay only hosts its match"));
                    return;
                }
                room = new Room { Code = matchTickets != null ? matchCode : NewCode(), Host = from };
                matchStarted = matchTickets != null;
                rooms[room.Code] = room;
                roomOf[from] = room;
                Send(from, ENetChannel.Reliable, RelayProtocol.Make(RelayMsg.RoomCreated, room.Code));
                Console.WriteLine($"room {room.Code} created by peer {from}");
                foreach (ulong waiting in waitingJoins) Join(waiting, room);
                waitingJoins.Clear();
                break;

            case RelayMsg.JoinRoom:
                if (room != null) return;
                string code = RelayProtocol.ReadText(msg).Trim().ToUpperInvariant();
                if (!rooms.TryGetValue(code, out room))
                {
                    if (matchTickets != null && code == matchCode && !matchStarted)
                    {
                        // a matched player was faster than the host: join when the room exists
                        if (!waitingJoins.Contains(from)) waitingJoins.Add(from);
                        Console.WriteLine($"peer {from} waits for the match host");
                        return;
                    }
                    Send(from, ENetChannel.Reliable, RelayProtocol.Make(RelayMsg.JoinFailed, "unknown room " + code));
                    return;
                }
                Join(from, room);
                break;

            case RelayMsg.ToHost:
                if (room == null || room.Host == from) return;
                Send(room.Host, channel, RelayProtocol.Make(RelayMsg.FromPeer, (uint)from, RelayProtocol.ReadPayload(msg, false)));
                Count(channel);
                break;

            case RelayMsg.ToPeer:
            {
                if (room == null || room.Host != from) return;
                ulong to = RelayProtocol.ReadPeerId(msg);
                if (!room.Clients.Contains(to)) return;
                Send(to, channel, RelayProtocol.Make(RelayMsg.FromHost, RelayProtocol.ReadPayload(msg, true)));
                Count(channel);
                break;
            }

            case RelayMsg.KickPeer:
            {
                if (room == null || room.Host != from) return;
                ulong peer = RelayProtocol.ReadPeerId(msg);
                if (room.Clients.Contains(peer))
                {
                    Console.WriteLine($"host of room {room.Code} kicked peer {peer}");
                    net.Disconnect(new ConnectionHandle(peer));
                    Left(peer);
                }
                break;
            }
        }
    }

    void Join(ulong peer, Room room)
    {
        room.Clients.Add(peer);
        roomOf[peer] = room;
        // tell the host first: the client may start sending as soon as it is Joined
        Send(room.Host, ENetChannel.Reliable, RelayProtocol.Make(RelayMsg.PeerJoined, (uint)peer));
        Send(peer, ENetChannel.Reliable, RelayProtocol.Make(RelayMsg.Joined));
        Console.WriteLine($"peer {peer} joined room {room.Code} ({room.Clients.Count} client(s))");
    }

    void Left(ulong id)
    {
        waitingJoins.Remove(id);
        if (!roomOf.TryGetValue(id, out Room room))
        {
            Console.WriteLine($"peer {id} disconnected");
            return;
        }
        roomOf.Remove(id);

        if (room.Host == id)
        {
            rooms.Remove(room.Code);
            Console.WriteLine($"host left, closing room {room.Code}");
            foreach (ulong client in room.Clients.ToList())
            {
                roomOf.Remove(client);
                Send(client, ENetChannel.Reliable, RelayProtocol.Make(RelayMsg.RoomClosed));
            }
            // the clients disconnect themselves on RoomClosed
            matchOver = matchTickets != null;
        }
        else if (room.Clients.Remove(id))
        {
            Send(room.Host, ENetChannel.Reliable, RelayProtocol.Make(RelayMsg.PeerLeft, (uint)id));
            Console.WriteLine($"peer {id} left room {room.Code}");
        }
    }

    void Send(ulong to, ENetChannel channel, byte[] msg)
    {
        net.SendMessage(new ConnectionHandle(to), channel, msg);
    }

    void Count(ENetChannel channel)
    {
        if (channel == ENetChannel.Reliable) forwardedReliable++;
        else forwardedUnreliable++;
    }

    string NewCode()
    {
        const string letters = "ABCDEFGHJKLMNPQRSTUVWXYZ"; // no I or O: easy to read out loud
        string code;
        do
        {
            char[] c = new char[RelayProtocol.CodeLength];
            for (int i = 0; i < c.Length; i++) c[i] = letters[RandomNumberGenerator.GetInt32(letters.Length)];
            code = new string(c);
        } while (rooms.ContainsKey(code));
        return code;
    }

    /// <summary>On Edgegap, stop this deployment: an exited container alone does not end it.</summary>
    static void StopDeployment()
    {
        string url = Environment.GetEnvironmentVariable("ARBITRIUM_DELETE_URL");
        string token = Environment.GetEnvironmentVariable("ARBITRIUM_DELETE_TOKEN");
        if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(token)) return;
        for (int attempt = 1; attempt <= 5; attempt++)
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                using var request = new HttpRequestMessage(HttpMethod.Delete, url);
                request.Headers.TryAddWithoutValidation("Authorization", token);
                HttpResponseMessage response = http.Send(request);
                Console.WriteLine($"Stopping the deployment: HTTP {(int)response.StatusCode}");
                if (response.IsSuccessStatusCode) return;
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"Stopping the deployment failed: {e.Message}");
            }
            Thread.Sleep(2000);
        }
    }

    static void FlushLogs()
    {
        while (LogQueue.GetNext(out LogMessage log))
        {
            (log.Type == ELogType.Info ? Console.Out : Console.Error).WriteLine($"[{log.Type}] {log.Message}");
        }
    }

    static int Env(string name, int fallback)
    {
        return int.TryParse(Environment.GetEnvironmentVariable(name), out int value) ? value : fallback;
    }
}
