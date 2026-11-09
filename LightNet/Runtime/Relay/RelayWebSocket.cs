using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace LightNet.Relay
{
    /// <summary>
    /// A game's WebSocket connection to the relay: the alternative to LightNet's TCP + UDP, for <c>ws://</c> and
    /// <c>wss://</c> relay addresses. On Edgegap, a WS port with TLS Upgrade makes it <c>wss://</c>: Edgegap holds the
    /// certificate and the traffic between the game and Edgegap is encrypted.<br/>
    /// Every relay message is one binary frame, <c>[channel: u8][relay message]</c>, so the relay still knows which
    /// messages are reliable and which are unreliable when it forwards them to a TCP/UDP peer. Over WebSocket both
    /// arrive reliably and in order.<br/>
    /// Thread safe: a background task sends and receives; <see cref="TryReceive"/> is polled from the game loop.<br/>
    /// In a WebGL build there is no <c>ClientWebSocket</c> and no threads: the browser's own WebSocket does the work
    /// through <c>RelayWebSocket.jslib</c>, and the same polling drains what it received.
    /// </summary>
    public class RelayWebSocket
    {
        public static bool IsWebSocketAddress(string address)
        {
            return address != null && (address.StartsWith("ws://", StringComparison.OrdinalIgnoreCase) || address.StartsWith("wss://", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>One relay message per binary frame: [channel][message].</summary>
        public static byte[] Frame(ENetChannel channel, byte[] msg)
        {
            byte[] frame = new byte[1 + msg.Length];
            frame[0] = (byte)channel;
            Buffer.BlockCopy(msg, 0, frame, 1, msg.Length);
            return frame;
        }

        public static bool Unframe(byte[] frame, int length, out byte[] msg, out ENetChannel channel)
        {
            msg = null;
            channel = ENetChannel.Reliable;
            if (length < 2) return false;
            channel = frame[0] == (byte)ENetChannel.Unreliable ? ENetChannel.Unreliable : ENetChannel.Reliable;
            msg = new byte[length - 1];
            Buffer.BlockCopy(frame, 1, msg, 0, msg.Length);
            return true;
        }

#if UNITY_WEBGL && !UNITY_EDITOR
        // ---- WebGL: the browser's WebSocket (RelayWebSocket.jslib) ----

        [DllImport("__Internal")] static extern int RelayWs_Open(string url);
        [DllImport("__Internal")] static extern int RelayWs_State(int id);
        [DllImport("__Internal")] static extern IntPtr RelayWs_Reason(int id);
        [DllImport("__Internal")] static extern void RelayWs_Free(IntPtr ptr);
        [DllImport("__Internal")] static extern int RelayWs_Send(int id, byte[] data, int length);
        [DllImport("__Internal")] static extern int RelayWs_NextLength(int id);
        [DllImport("__Internal")] static extern int RelayWs_Receive(int id, byte[] buffer, int length);
        [DllImport("__Internal")] static extern void RelayWs_Close(int id);

        const int Connecting = 0, Open = 1;

        readonly int id;
        bool closed;
        string closeReason;

        public RelayWebSocket(string url)
        {
            id = RelayWs_Open(url);
        }

        public bool IsOpen => !IsClosed && RelayWs_State(id) == Open;

        public bool IsClosed
        {
            get
            {
                if (!closed && RelayWs_State(id) != Connecting && RelayWs_State(id) != Open)
                {
                    IntPtr text = RelayWs_Reason(id);
                    closeReason = Marshal.PtrToStringUTF8(text);
                    RelayWs_Free(text);
                    closed = true;
                }
                return closed;
            }
        }

        public string CloseReason => closeReason;

        public void Send(ENetChannel channel, byte[] msg)
        {
            if (closed) return;
            byte[] frame = Frame(channel, msg);
            RelayWs_Send(id, frame, frame.Length);
        }

        public bool TryReceive(out byte[] msg, out ENetChannel channel)
        {
            msg = null;
            channel = ENetChannel.Reliable;
            while (!closed)
            {
                int length = RelayWs_NextLength(id);
                if (length < 0) return false;
                byte[] frame = new byte[length];
                int n = RelayWs_Receive(id, frame, length);
                if (n >= 0 && Unframe(frame, n, out msg, out channel)) return true;
            }
            return false;
        }

        public void Close()
        {
            if (closed) return;
            closed = true;
            closeReason = "closed";
            RelayWs_Close(id);
        }
#else
        // ---- desktop, mobile and the relay server: .NET ClientWebSocket ----

        public bool IsOpen => opened && !IsClosed;
        public bool IsClosed { get; private set; }
        public string CloseReason { get; private set; }

        readonly ClientWebSocket socket = new ClientWebSocket();
        readonly CancellationTokenSource cancel = new CancellationTokenSource();
        readonly ConcurrentQueue<(byte[] msg, ENetChannel channel)> received = new ConcurrentQueue<(byte[], ENetChannel)>();
        readonly ConcurrentQueue<byte[]> outgoing = new ConcurrentQueue<byte[]>();
        readonly SemaphoreSlim outgoingSignal = new SemaphoreSlim(0);
        volatile bool opened;

        public RelayWebSocket(string url)
        {
            socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
            Task.Run(() => Run(new Uri(url)));
        }

        public void Send(ENetChannel channel, byte[] msg)
        {
            if (IsClosed) return;
            outgoing.Enqueue(Frame(channel, msg));
            outgoingSignal.Release();
        }

        public bool TryReceive(out byte[] msg, out ENetChannel channel)
        {
            if (received.TryDequeue(out var item))
            {
                msg = item.msg;
                channel = item.channel;
                return true;
            }
            msg = null;
            channel = ENetChannel.Reliable;
            return false;
        }

        public void Close()
        {
            if (IsClosed) return;
            Closed("closed");
            // say goodbye in the background; the relay also notices a dropped connection
            Task.Run(async () =>
            {
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", timeout.Token);
                }
                catch (Exception) { }
                cancel.Cancel();
                socket.Dispose();
            });
        }

        async Task Run(Uri url)
        {
            try
            {
                await socket.ConnectAsync(url, cancel.Token);
                opened = true;
                _ = SendLoop();
                var buffer = new byte[64 * 1024];
                var message = new MemoryStream();
                while (!IsClosed)
                {
                    WebSocketReceiveResult result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancel.Token);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        Closed("relay connection lost");
                        cancel.Cancel();
                        return;
                    }
                    message.Write(buffer, 0, result.Count);
                    if (!result.EndOfMessage) continue;
                    if (Unframe(message.GetBuffer(), (int)message.Length, out byte[] msg, out ENetChannel channel)) received.Enqueue((msg, channel));
                    message.SetLength(0);
                }
            }
            catch (Exception e)
            {
                Closed(opened ? "relay connection lost" : "cannot connect to the relay: " + e.Message);
                cancel.Cancel(); // stops the send loop
            }
        }

        async Task SendLoop()
        {
            try
            {
                while (!IsClosed)
                {
                    await outgoingSignal.WaitAsync(cancel.Token);
                    while (outgoing.TryDequeue(out byte[] frame))
                    {
                        await socket.SendAsync(new ArraySegment<byte>(frame), WebSocketMessageType.Binary, true, cancel.Token);
                    }
                }
            }
            catch (Exception)
            {
                Closed("relay connection lost");
                cancel.Cancel(); // stops the receive loop
            }
        }

        void Closed(string reason)
        {
            if (IsClosed) return;
            CloseReason = reason;
            IsClosed = true;
        }
#endif
    }
}
