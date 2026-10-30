using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using LightNet;
using LightNet.Relay;

// LightNet relay server: one player's game hosts a room, the others join it with the room code.
// Everyone is a LightNet client of this server (TCP for reliable, UDP for unreliable), so nobody
// needs an open port. The server forwards client traffic to the host and host traffic to the client
// it is addressed to, on the channel it came in on.
//
// Environment: RELAY_TCP_PORT (default 7777), RELAY_UDP_PORT (default 7777), RELAY_MAX_PEERS (default 256)
// Exits when it gets SIGTERM / Ctrl+C, or after RELAY_IDLE_EXIT_SECONDS without any room (0 = never).

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

    static int Main()
    {
        int tcpPort = Env("RELAY_TCP_PORT", RelayProtocol.DefaultPort);
        int udpPort = Env("RELAY_UDP_PORT", RelayProtocol.DefaultPort);
        int maxPeers = Env("RELAY_MAX_PEERS", 256);
        int idleExit = Env("RELAY_IDLE_EXIT_SECONDS", 0);
        return new RelayServer().Run(tcpPort, udpPort, maxPeers, idleExit);
    }

    int Run(int tcpPort, int udpPort, int maxPeers, int idleExitSeconds)
    {
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

            if (rooms.Count > 0) lastActivity = DateTime.UtcNow;
            if (idleExitSeconds > 0 && (DateTime.UtcNow - lastActivity).TotalSeconds > idleExitSeconds)
            {
                Console.WriteLine($"No room for {idleExitSeconds} s, exiting");
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
            case RelayMsg.CreateRoom:
                if (room != null) return;
                room = new Room { Code = NewCode(), Host = from };
                rooms[room.Code] = room;
                roomOf[from] = room;
                Send(from, ENetChannel.Reliable, RelayProtocol.Make(RelayMsg.RoomCreated, room.Code));
                Console.WriteLine($"room {room.Code} created by peer {from}");
                break;

            case RelayMsg.JoinRoom:
                if (room != null) return;
                string code = RelayProtocol.ReadText(msg).Trim().ToUpperInvariant();
                if (!rooms.TryGetValue(code, out room))
                {
                    Send(from, ENetChannel.Reliable, RelayProtocol.Make(RelayMsg.JoinFailed, "unknown room " + code));
                    return;
                }
                room.Clients.Add(from);
                roomOf[from] = room;
                // tell the host first: the client may start sending as soon as it is Joined
                Send(room.Host, ENetChannel.Reliable, RelayProtocol.Make(RelayMsg.PeerJoined, (uint)from));
                Send(from, ENetChannel.Reliable, RelayProtocol.Make(RelayMsg.Joined));
                Console.WriteLine($"peer {from} joined room {room.Code} ({room.Clients.Count} client(s))");
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

    void Left(ulong id)
    {
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
