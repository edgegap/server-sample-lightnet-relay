using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using LightNet;
using LightNet.Relay;

// Players that reach the relay over WebSocket instead of LightNet's TCP + UDP (RelayWebSocket in the games).
// On Edgegap, a WS port with TLS Upgrade turns it into wss://: Edgegap terminates TLS and forwards plain
// WebSocket here, so the relay needs no certificate. Each binary frame is [channel: u8][relay message],
// so a WebSocket peer and a TCP/UDP peer can share a room and the relay keeps each message's channel.
// Other HTTP requests (health checks) get "200 ok".
//
// Peer ids start at 0x40000000 so they never clash with LightNet's connection handles, and stay positive as
// an int: the netcodes use them as connection ids (FishNet refuses negative ones).

class WebSocketPeers
{
    public enum EventType { Connected, Message, Disconnected }

    public struct Event
    {
        public EventType Type;
        public ulong Peer;
        public byte[] Message;
        public ENetChannel Channel;
        public string Name;
    }

    const ulong FirstId = 0x40000000;
    const int MaxFrame = 64 * 1024;
    static readonly byte[] HealthResponse = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok");

    class Peer
    {
        public WebSocket Socket;
        public Channel<byte[]> Outgoing = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
        public CancellationTokenSource Cancel = new CancellationTokenSource();
    }

    readonly ConcurrentQueue<Event> events = new ConcurrentQueue<Event>();
    readonly ConcurrentDictionary<ulong, Peer> peers = new ConcurrentDictionary<ulong, Peer>();
    TcpListener listener;
    long nextId = (long)FirstId;
    int maxPeers;

    public static bool Owns(ulong peer) => peer >= FirstId;

    public bool Start(int port, int maxPeers)
    {
        this.maxPeers = maxPeers;
        try
        {
            listener = new TcpListener(IPAddress.Any, port);
            listener.Start();
        }
        catch (SocketException e)
        {
            Console.Error.WriteLine($"Cannot listen for WebSocket on TCP {port}: {e.Message}");
            return false;
        }
        Task.Run(AcceptLoop);
        return true;
    }

    public void Stop()
    {
        listener?.Stop();
        foreach (Peer peer in peers.Values) peer.Cancel.Cancel();
    }

    public bool TryGetEvent(out Event ev) => events.TryDequeue(out ev);

    public void Send(ulong id, ENetChannel channel, byte[] msg)
    {
        if (peers.TryGetValue(id, out Peer peer)) peer.Outgoing.Writer.TryWrite(RelayWebSocket.Frame(channel, msg));
    }

    public void Disconnect(ulong id)
    {
        if (!peers.TryGetValue(id, out Peer peer)) return;
        peer.Outgoing.Writer.TryComplete(); // the send loop closes the socket after the queued messages
    }

    async Task AcceptLoop()
    {
        while (true)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync();
            }
            catch (Exception)
            {
                return; // stopped
            }
            _ = Task.Run(() => Serve(client));
        }
    }

    async Task Serve(TcpClient client)
    {
        string name = client.Client.RemoteEndPoint?.ToString() ?? "?";
        client.NoDelay = true;
        NetworkStream stream = client.GetStream();
        string key;
        try
        {
            key = await ReadHandshake(stream);
        }
        catch (Exception)
        {
            client.Dispose();
            return;
        }
        if (key == null || peers.Count >= maxPeers)
        {
            // not a WebSocket upgrade (a health check), or the relay is full
            try { await stream.WriteAsync(HealthResponse); } catch (Exception) { }
            client.Dispose();
            return;
        }

        string accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
        await stream.WriteAsync(Encoding.ASCII.GetBytes(
            "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: " + accept + "\r\n\r\n"));

        ulong id = (ulong)Interlocked.Increment(ref nextId) - 1;
        var peer = new Peer
        {
            Socket = WebSocket.CreateFromStream(stream, new WebSocketCreationOptions { IsServer = true, KeepAliveInterval = TimeSpan.FromSeconds(15) }),
        };
        peers[id] = peer;
        events.Enqueue(new Event { Type = EventType.Connected, Peer = id, Name = name });

        Task sending = SendLoop(peer);
        try
        {
            var buffer = new byte[MaxFrame];
            var message = new MemoryStream();
            while (true)
            {
                WebSocketReceiveResult result = await peer.Socket.ReceiveAsync(new ArraySegment<byte>(buffer), peer.Cancel.Token);
                if (result.MessageType == WebSocketMessageType.Close) break;
                message.Write(buffer, 0, result.Count);
                if (message.Length > MaxFrame) break; // larger than any relay message
                if (!result.EndOfMessage) continue;
                if (RelayWebSocket.Unframe(message.GetBuffer(), (int)message.Length, out byte[] msg, out ENetChannel channel))
                {
                    events.Enqueue(new Event { Type = EventType.Message, Peer = id, Message = msg, Channel = channel });
                }
                message.SetLength(0);
            }
        }
        catch (Exception)
        {
            // connection lost
        }
        peers.TryRemove(id, out _);
        peer.Outgoing.Writer.TryComplete();
        peer.Cancel.Cancel();
        await sending;
        client.Dispose();
        events.Enqueue(new Event { Type = EventType.Disconnected, Peer = id });
    }

    static async Task SendLoop(Peer peer)
    {
        try
        {
            await foreach (byte[] frame in peer.Outgoing.Reader.ReadAllAsync(peer.Cancel.Token))
            {
                await peer.Socket.SendAsync(frame, WebSocketMessageType.Binary, true, peer.Cancel.Token);
            }
            // Disconnect(): close after the queued messages went out
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await peer.Socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", timeout.Token);
        }
        catch (Exception)
        {
        }
        peer.Cancel.Cancel();
    }

    /// <summary>Reads the HTTP request head; returns its Sec-WebSocket-Key, or null if it isn't a WebSocket upgrade.</summary>
    static async Task<string> ReadHandshake(NetworkStream stream)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var head = new StringBuilder();
        var one = new byte[1];
        // byte by byte: the WebSocket frames that follow must stay in the stream
        while (!head.ToString().EndsWith("\r\n\r\n"))
        {
            if (head.Length > 8192 || await stream.ReadAsync(one, timeout.Token) == 0) return null;
            head.Append((char)one[0]);
        }
        string key = null;
        bool upgrade = false;
        foreach (string line in head.ToString().Split("\r\n"))
        {
            int colon = line.IndexOf(':');
            if (colon <= 0) continue;
            string field = line.Substring(0, colon).Trim();
            string value = line.Substring(colon + 1).Trim();
            if (field.Equals("Upgrade", StringComparison.OrdinalIgnoreCase) && value.Equals("websocket", StringComparison.OrdinalIgnoreCase)) upgrade = true;
            else if (field.Equals("Sec-WebSocket-Key", StringComparison.OrdinalIgnoreCase)) key = value;
        }
        return upgrade ? key : null;
    }
}
