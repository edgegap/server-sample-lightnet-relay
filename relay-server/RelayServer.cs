using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using LightNet;
using LightNet.Relay;

// LightNet relay server: one player's game hosts a room, the others join it with the room code.
// Everyone is a LightNet client of this server (TCP for reliable, UDP for unreliable), so nobody
// needs an open port. The server forwards client traffic to the host and host traffic to the client
// it is addressed to, on the channel it came in on.
//
// Players can also connect over WebSocket (RELAY_WS_PORT, see WebSocketPeers): on Edgegap, a WS port with
// TLS Upgrade makes that wss://, encrypted between the player and Edgegap. Both kinds of players share rooms.
//
// Environment: RELAY_TCP_PORT (default 7777), RELAY_UDP_PORT (default 7777), RELAY_WS_PORT (default 7778, 0 = off),
// RELAY_MAX_PEERS (default 256)
// Exits when it gets SIGTERM / Ctrl+C, or after RELAY_IDLE_EXIT_SECONDS without any room (default 600, 0 = never).
//
// Match mode: when the Edgegap matchmaker starts the relay for a match, it injects MM_TICKET_IDS (the matched
// players' tickets). Every room on the relay is then a match room that only its own tickets can enter: each player
// asks for its role with its ticket (MatchRole), the first ticket of a room hosts it, the others join it. Joins that
// arrive before the room's host has created it wait for it.
// With MATCHMAKER_URL and MATCHMAKER_TOKEN set, the relay also uses backfills (MatchBackfill):
// - a room with free seats (RELAY_MATCH_MAX_PLAYERS, default 4) asks the matchmaker for players to fill it,
// - when every room is full and the relay has fewer than RELAY_MAX_ROOMS rooms (default 4), it offers a new room:
//   the player placed there hosts it, and the room then fills like the others.
// A room whose host has stayed alone for RELAY_SOLO_ROOM_SECONDS (default 120) closes: the host is told
// (RoomTimedOut) and can search again. The relay stops when its last match room has ended, deleting its backfills
// first. On Edgegap the relay stops its own deployment (ARBITRIUM_DELETE_URL) when it exits on its own.

class RelayServer
{
    class Room
    {
        public string Code;
        public ulong Host;
        public HashSet<ulong> Clients = new HashSet<ulong>();
    }

    /// <summary>A room the matchmaker filled, before and after its host created it.</summary>
    class MatchRoom
    {
        public string Code;
        public string HostTicket;
        public bool Started;                    // the host created it
        public DateTime CreatedAt = DateTime.UtcNow;
        public DateTime? AloneSince;
        public readonly Dictionary<string, JsonObject> Tickets = new Dictionary<string, JsonObject>(); // all its players' tickets
        public readonly Dictionary<string, DateTime> OnTheWay = new Dictionary<string, DateTime>();    // seated, no role yet
        public readonly List<(string ticket, DateTime since)> Joining = new List<(string, DateTime)>(); // got their role, not in yet
        public readonly List<ulong> WaitingJoins = new List<ulong>();                                   // joined before the host
    }

    readonly NetworkService net = new NetworkService();
    readonly WebSocketPeers webSockets = new WebSocketPeers();
    readonly Dictionary<string, Room> rooms = new Dictionary<string, Room>();
    readonly Dictionary<ulong, Room> roomOf = new Dictionary<ulong, Room>();
    long forwardedReliable, forwardedUnreliable;

    // match mode
    bool matchMode;
    readonly Dictionary<string, MatchRoom> matchRooms = new Dictionary<string, MatchRoom>();   // by code
    readonly Dictionary<string, MatchRoom> roomOfTicket = new Dictionary<string, MatchRoom>();
    readonly List<(ulong peer, string ticket, DateTime since)> pendingRoles = new List<(ulong, string, DateTime)>();
    MatchBackfill backfill;
    int matchMaxPlayers, maxRooms, soloRoomSeconds;
    DateTime? emptySince;   // no match room left: stop after LastRoomGraceSeconds
    const double OnTheWaySeconds = 60, JoiningSeconds = 15, PendingRoleSeconds = 10, HostWaitSeconds = 60, LastRoomGraceSeconds = 3;

    static int Main()
    {
        int tcpPort = Env("RELAY_TCP_PORT", RelayProtocol.DefaultPort);
        int udpPort = Env("RELAY_UDP_PORT", RelayProtocol.DefaultPort);
        int maxPeers = Env("RELAY_MAX_PEERS", 256);
        int wsPort = Env("RELAY_WS_PORT", RelayProtocol.DefaultPort + 1);
        var server = new RelayServer();
        server.ReadMatch();
        int idleExit = Env("RELAY_IDLE_EXIT_SECONDS", 600);
        int code = server.Run(tcpPort, udpPort, wsPort, maxPeers, idleExit, out bool stoppedItself);
        if (stoppedItself) StopDeployment();
        return code;
    }

    void ReadMatch()
    {
        string ids = Environment.GetEnvironmentVariable("MM_TICKET_IDS");
        if (string.IsNullOrWhiteSpace(ids)) return;
        string[] tickets;
        try
        {
            tickets = JsonSerializer.Deserialize<string[]>(ids);
        }
        catch (JsonException)
        {
            Console.Error.WriteLine($"MM_TICKET_IDS is not a JSON array of ticket ids: {ids}");
            return;
        }
        matchMode = true;
        matchMaxPlayers = Env("RELAY_MATCH_MAX_PLAYERS", 4);
        maxRooms = Env("RELAY_MAX_ROOMS", 4);
        soloRoomSeconds = Env("RELAY_SOLO_ROOM_SECONDS", 120);
        MatchRoom first = NewMatchRoom();
        foreach (string ticket in tickets) AddTicket(first, ticket, MatchBackfill.TicketFromEnvironment(ticket));
        Console.WriteLine($"match mode: match {Environment.GetEnvironmentVariable("MM_MATCH_ID") ?? "?"}, {tickets.Length} matched player(s), " +
                          $"up to {matchMaxPlayers} per room, {maxRooms} room(s)");
        backfill = MatchBackfill.FromEnvironment();
        if (backfill != null) backfill.OnTicketAssigned = Seated;
    }

    int Run(int tcpPort, int udpPort, int wsPort, int maxPeers, int idleExitSeconds, out bool stoppedItself)
    {
        stoppedItself = false;
        if (!net.StartServer(tcpPort, udpPort, maxPeers))
        {
            FlushLogs();
            Console.Error.WriteLine($"Cannot listen on TCP {tcpPort} / UDP {udpPort}");
            return 1;
        }
        Console.WriteLine($"LightNet relay listening on TCP {tcpPort}, UDP {udpPort}, max {maxPeers} peers");
        if (wsPort > 0)
        {
            if (!webSockets.Start(wsPort, maxPeers))
            {
                net.Close();
                FlushLogs();
                return 1;
            }
            Console.WriteLine($"WebSocket relay listening on TCP {wsPort}");
        }

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
            while (webSockets.TryGetEvent(out WebSocketPeers.Event ws))
            {
                if (ws.Type == WebSocketPeers.EventType.Connected) Console.WriteLine($"peer {ws.Peer} connected over WebSocket ({ws.Name})");
                else if (ws.Type == WebSocketPeers.EventType.Disconnected) Left(ws.Peer);
                else if (ws.Message.Length > 0) Handle(ws.Peer, ws.Message, ws.Channel);
            }

            if (matchMode)
            {
                UpdateMatch();
                if (matchRooms.Count > 0) emptySince = null;
                else if (emptySince == null)
                {
                    // a backfill may have seated a player we haven't polled yet: check once before stopping
                    emptySince = DateTime.UtcNow;
                    backfill?.PollSoon();
                }
                else if ((DateTime.UtcNow - emptySince.Value).TotalSeconds > LastRoomGraceSeconds)
                {
                    Console.WriteLine("Last match room ended, exiting");
                    stoppedItself = true;
                    break;
                }
            }
            else
            {
                if (rooms.Count > 0) lastActivity = DateTime.UtcNow;
                if (idleExitSeconds > 0 && (DateTime.UtcNow - lastActivity).TotalSeconds > idleExitSeconds)
                {
                    Console.WriteLine($"No room for {idleExitSeconds} s, exiting");
                    stoppedItself = true;
                    break;
                }
            }
            if ((DateTime.UtcNow - lastStats).TotalSeconds >= 10)
            {
                lastStats = DateTime.UtcNow;
                Console.WriteLine($"stats: rooms={rooms.Count} peers={roomOf.Count} forwarded reliable={forwardedReliable} unreliable={forwardedUnreliable}");
            }
            Thread.Sleep(1);
        }

        // no player must be sent here once the relay is stopping
        backfill?.CloseAll();
        net.Close();
        webSockets.Stop();
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
                if (!matchMode)
                {
                    Send(from, ENetChannel.Reliable, RelayProtocol.Make(RelayMsg.JoinFailed, "not a match relay"));
                    return;
                }
                if (!roomOfTicket.ContainsKey(ticket))
                {
                    if (backfill != null)
                    {
                        // maybe a backfilled player that got here before our next backfill poll: wait a little
                        pendingRoles.Add((from, ticket, DateTime.UtcNow));
                        backfill.PollSoon();
                    }
                    else Send(from, ENetChannel.Reliable, RelayProtocol.Make(RelayMsg.JoinFailed, "ticket not in this match"));
                    return;
                }
                AssignRole(from, ticket);
                break;
            }

            case RelayMsg.CreateRoom:
            {
                if (room != null) return;
                string wanted = RelayProtocol.ReadText(msg).Trim().ToUpperInvariant();
                MatchRoom match = null;
                if (matchMode && (!matchRooms.TryGetValue(wanted, out match) || match.Started))
                {
                    // a match relay only hosts its match rooms, each once, for the player MatchRole picked
                    Send(from, ENetChannel.Reliable, RelayProtocol.Make(RelayMsg.JoinFailed, "this relay only hosts its match rooms"));
                    return;
                }
                room = new Room { Code = match != null ? match.Code : NewCode(), Host = from };
                rooms[room.Code] = room;
                roomOf[from] = room;
                Send(from, ENetChannel.Reliable, RelayProtocol.Make(RelayMsg.RoomCreated, room.Code));
                Console.WriteLine($"room {room.Code} created by peer {from}");
                if (match != null)
                {
                    match.Started = true;
                    match.Joining.RemoveAll(j => j.ticket == match.HostTicket);
                    foreach (ulong waiting in match.WaitingJoins) Join(waiting, room);
                    match.WaitingJoins.Clear();
                }
                break;
            }

            case RelayMsg.JoinRoom:
                if (room != null) return;
                string code = RelayProtocol.ReadText(msg).Trim().ToUpperInvariant();
                if (!rooms.TryGetValue(code, out room))
                {
                    if (matchMode && matchRooms.TryGetValue(code, out MatchRoom pending) && !pending.Started)
                    {
                        // a matched player was faster than the host: join when the room exists
                        if (!pending.WaitingJoins.Contains(from)) pending.WaitingJoins.Add(from);
                        Console.WriteLine($"peer {from} waits for the host of room {code}");
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
                    if (WebSocketPeers.Owns(peer)) webSockets.Disconnect(peer);
                    else net.Disconnect(new ConnectionHandle(peer));
                    Left(peer);
                }
                break;
            }
        }
    }

    // ---------------------------------------------------------------- match mode

    MatchRoom NewMatchRoom()
    {
        var match = new MatchRoom { Code = NewCode() };
        matchRooms[match.Code] = match;
        return match;
    }

    void AddTicket(MatchRoom match, string ticket, JsonObject json)
    {
        match.Tickets[ticket] = json;
        match.OnTheWay[ticket] = DateTime.UtcNow;
        roomOfTicket[ticket] = match;
    }

    void AssignRole(ulong peer, string ticket)
    {
        MatchRoom match = roomOfTicket[ticket];
        // the room's first ticket to ask hosts it; asking again gives the same answer
        match.HostTicket ??= ticket;
        bool isHost = ticket == match.HostTicket;
        byte[] reply = RelayProtocol.Make(RelayMsg.MatchAssigned, " " + match.Code);
        reply[1] = (byte)(isHost ? 1 : 0);
        Send(peer, ENetChannel.Reliable, reply);
        match.OnTheWay.Remove(ticket);
        match.Joining.RemoveAll(j => j.ticket == ticket);
        match.Joining.Add((ticket, DateTime.UtcNow));
        Console.WriteLine($"peer {peer}: ticket {ticket} {(isHost ? "hosts" : "joins")} room {match.Code}");
    }

    /// <summary>The matchmaker put a player's ticket in one of our backfills: a seat in a room, or a new room.</summary>
    void Seated(string key, string ticket, JsonObject json)
    {
        if (!matchRooms.TryGetValue(key, out MatchRoom match))
        {
            // a new room, or a room that closed meanwhile: the player hosts a room of its own
            match = NewMatchRoom();
            Console.WriteLine($"new room {match.Code} for ticket {ticket}");
        }
        AddTicket(match, ticket, json);
        foreach (var pending in pendingRoles.Where(p => p.ticket == ticket).ToList())
        {
            pendingRoles.Remove(pending);
            AssignRole(pending.peer, ticket);
        }
    }

    void UpdateMatch()
    {
        DateTime now = DateTime.UtcNow;
        foreach (var pending in pendingRoles.Where(p => (now - p.since).TotalSeconds > PendingRoleSeconds).ToList())
        {
            pendingRoles.Remove(pending);
            Send(pending.peer, ENetChannel.Reliable, RelayProtocol.Make(RelayMsg.JoinFailed, "ticket not in this match"));
        }

        foreach (MatchRoom match in matchRooms.Values.ToList())
        {
            foreach (string t in match.OnTheWay.Where(e => (now - e.Value).TotalSeconds > OnTheWaySeconds).Select(e => e.Key).ToList()) match.OnTheWay.Remove(t);
            match.Joining.RemoveAll(j => (now - j.since).TotalSeconds > JoiningSeconds);

            if (!match.Started)
            {
                // the host never came (and nobody else got its role): give up on the room
                if ((now - match.CreatedAt).TotalSeconds > HostWaitSeconds && match.OnTheWay.Count == 0 && match.Joining.Count == 0)
                {
                    Console.WriteLine($"room {match.Code}: its host never came, dropping it");
                    foreach (ulong waiting in match.WaitingJoins) Send(waiting, ENetChannel.Reliable, RelayProtocol.Make(RelayMsg.JoinFailed, "the host never came"));
                    EndMatchRoom(match);
                }
                continue;
            }

            // a host alone for too long: close the room so the player can find another match
            bool alone = rooms.TryGetValue(match.Code, out Room room) && room.Clients.Count == 0 && match.OnTheWay.Count == 0 && match.Joining.Count == 0;
            match.AloneSince = alone ? match.AloneSince ?? now : null;
            if (soloRoomSeconds > 0 && match.AloneSince != null && (now - match.AloneSince.Value).TotalSeconds > soloRoomSeconds)
            {
                Console.WriteLine($"room {match.Code}: nobody joined its host in {soloRoomSeconds} s, closing it");
                Send(room.Host, ENetChannel.Reliable, RelayProtocol.Make(RelayMsg.RoomTimedOut));
                rooms.Remove(room.Code);
                roomOf.Remove(room.Host);
                EndMatchRoom(match);
            }
        }

        backfill?.Update(Wants);
    }

    /// <summary>What to ask the matchmaker for: seats in rooms that aren't full first, then a new room once all are full.</summary>
    List<MatchBackfill.Want> Wants()
    {
        var wants = new List<MatchBackfill.Want>();
        if (matchRooms.Count == 0) return wants; // the relay is stopping: no new rooms
        int freeSeats = 0;
        foreach (MatchRoom match in matchRooms.Values.OrderByDescending(m => Taken(m)))
        {
            int free = matchMaxPlayers - Taken(match);
            if (free <= 0) continue;
            freeSeats += free;
            // the room's own players' tickets: the matchmaker checks the new player against them (latency)
            wants.Add(new MatchBackfill.Want { Key = match.Code, Count = Math.Min(free, 3), Tickets = match.Tickets.Values.Where(t => t != null).ToList() });
        }
        if (freeSeats == 0 && matchRooms.Count < maxRooms)
        {
            // tickets of players on this relay, so the new room's player is near it too
            var tickets = matchRooms.Values.SelectMany(m => m.Tickets.Values).Where(t => t != null).ToList();
            wants.Add(new MatchBackfill.Want { Key = MatchBackfill.NewRoom, Count = 1, Tickets = tickets });
        }
        return wants;
    }

    /// <summary>Seats taken in a match room: players in it, plus players on their way.</summary>
    int Taken(MatchRoom match)
    {
        int inRoom = rooms.TryGetValue(match.Code, out Room room) ? 1 + room.Clients.Count : 0;
        return inRoom + match.OnTheWay.Count + match.Joining.Count;
    }

    void EndMatchRoom(MatchRoom match)
    {
        matchRooms.Remove(match.Code);
        foreach (string ticket in match.Tickets.Keys) roomOfTicket.Remove(ticket);
    }

    // ---------------------------------------------------------------- rooms

    void Join(ulong peer, Room room)
    {
        // a player entering a match room is its oldest one that got a role (role and join come back to back)
        if (matchRooms.TryGetValue(room.Code, out MatchRoom match) && match.Joining.Count > 0) match.Joining.RemoveAt(0);
        room.Clients.Add(peer);
        roomOf[peer] = room;
        // tell the host first: the client may start sending as soon as it is Joined
        Send(room.Host, ENetChannel.Reliable, RelayProtocol.Make(RelayMsg.PeerJoined, (uint)peer));
        Send(peer, ENetChannel.Reliable, RelayProtocol.Make(RelayMsg.Joined));
        Console.WriteLine($"peer {peer} joined room {room.Code} ({room.Clients.Count} client(s))");
    }

    void Left(ulong id)
    {
        pendingRoles.RemoveAll(p => p.peer == id);
        foreach (MatchRoom match in matchRooms.Values) match.WaitingJoins.Remove(id);
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
            if (matchRooms.TryGetValue(room.Code, out MatchRoom match)) EndMatchRoom(match);
        }
        else if (room.Clients.Remove(id))
        {
            Send(room.Host, ENetChannel.Reliable, RelayProtocol.Make(RelayMsg.PeerLeft, (uint)id));
            Console.WriteLine($"peer {id} left room {room.Code}");
        }
    }

    void Send(ulong to, ENetChannel channel, byte[] msg)
    {
        if (WebSocketPeers.Owns(to)) webSockets.Send(to, channel, msg);
        else net.SendMessage(new ConnectionHandle(to), channel, msg);
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
        } while (rooms.ContainsKey(code) || matchRooms.ContainsKey(code));
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
