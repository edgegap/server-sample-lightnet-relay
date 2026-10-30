using System;
using System.Text;

namespace LightNet.Relay
{
    /// <summary>
    /// Messages between game peers and the LightNet relay server. Every LightNet message starts with
    /// one of these bytes (LightNet's "first byte is the message type" convention).<br/>
    /// One player hosts a room and gets a code; the others join with that code. The relay forwards
    /// client traffic to the host and host traffic to the client it is addressed to, on the channel
    /// (reliable or unreliable) it arrived on.
    /// </summary>
    public enum RelayMsg : byte
    {
        // peer -> relay
        CreateRoom = 0x01,  // [] (host)
        JoinRoom = 0x02,    // [code: ascii]
        ToHost = 0x03,      // [payload] (client)
        ToPeer = 0x04,      // [peerId: u32][payload] (host)
        KickPeer = 0x05,    // [peerId: u32] (host)

        // relay -> peer
        RoomCreated = 0x10, // [code: ascii] (host)
        Joined = 0x11,      // [] (client)
        JoinFailed = 0x12,  // [reason: ascii] (client)
        PeerJoined = 0x13,  // [peerId: u32] (host)
        PeerLeft = 0x14,    // [peerId: u32] (host)
        FromPeer = 0x15,    // [peerId: u32][payload] (host)
        FromHost = 0x16,    // [payload] (client)
        RoomClosed = 0x17,  // [] (client, the host left)
    }

    public static class RelayProtocol
    {
        public const int DefaultPort = 7777;
        public const int CodeLength = 6;

        // Mirror and the other netcodes batch messages; keep unreliable datagrams under a safe MTU:
        // 1200 - LightNet UDP header (handle 8 + timestamp 8 + length 2) - relay header (1 + 4).
        public const int MaxUnreliablePayload = 1177;
        public const int MaxReliablePayload = 16 * 1024;

        public static byte[] Make(RelayMsg type)
        {
            return new byte[] { (byte)type };
        }

        public static byte[] Make(RelayMsg type, string text)
        {
            byte[] bytes = Encoding.ASCII.GetBytes(text);
            byte[] msg = new byte[1 + bytes.Length];
            msg[0] = (byte)type;
            Array.Copy(bytes, 0, msg, 1, bytes.Length);
            return msg;
        }

        public static byte[] Make(RelayMsg type, uint peerId)
        {
            byte[] msg = new byte[1 + sizeof(uint)];
            msg[0] = (byte)type;
            WriteU32(msg, 1, peerId);
            return msg;
        }

        public static byte[] Make(RelayMsg type, ArraySegment<byte> payload)
        {
            byte[] msg = new byte[1 + payload.Count];
            msg[0] = (byte)type;
            Array.Copy(payload.Array, payload.Offset, msg, 1, payload.Count);
            return msg;
        }

        public static byte[] Make(RelayMsg type, uint peerId, ArraySegment<byte> payload)
        {
            byte[] msg = new byte[1 + sizeof(uint) + payload.Count];
            msg[0] = (byte)type;
            WriteU32(msg, 1, peerId);
            Array.Copy(payload.Array, payload.Offset, msg, 1 + sizeof(uint), payload.Count);
            return msg;
        }

        public static string ReadText(byte[] msg)
        {
            return Encoding.ASCII.GetString(msg, 1, msg.Length - 1);
        }

        public static uint ReadPeerId(byte[] msg)
        {
            return msg.Length >= 1 + sizeof(uint) ? ReadU32(msg, 1) : 0;
        }

        /// <summary>The payload after the type byte and, if <paramref name="withPeerId"/>, the peer id.</summary>
        public static ArraySegment<byte> ReadPayload(byte[] msg, bool withPeerId)
        {
            int offset = withPeerId ? 1 + sizeof(uint) : 1;
            return offset <= msg.Length ? new ArraySegment<byte>(msg, offset, msg.Length - offset) : new ArraySegment<byte>(msg, 0, 0);
        }

        // little endian on every platform
        static void WriteU32(byte[] buffer, int offset, uint value)
        {
            buffer[offset] = (byte)value;
            buffer[offset + 1] = (byte)(value >> 8);
            buffer[offset + 2] = (byte)(value >> 16);
            buffer[offset + 3] = (byte)(value >> 24);
        }

        static uint ReadU32(byte[] buffer, int offset)
        {
            return (uint)(buffer[offset] | buffer[offset + 1] << 8 | buffer[offset + 2] << 16 | buffer[offset + 3] << 24);
        }
    }
}
