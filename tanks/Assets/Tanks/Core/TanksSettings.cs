using System;
using System.Linq;
using UnityEngine;

namespace Tanks
{
    public enum Netcode
    {
        Mirror,
        FishNet,
        PurrNet,
        NGO,
    }

    /// <summary>
    /// Which netcode the game runs on, and where the LightNet relay is.<br/>
    /// Edit <c>Assets/Tanks/Resources/TanksSettings.asset</c> (or use the <b>Tanks &gt; Netcode</b> menu);
    /// <c>-netcode Mirror|FishNet|PurrNet|NGO</c>, <c>-relay</c>, <c>-relayPort</c> and <c>-relayUdpPort</c>
    /// override it on the command line.
    /// </summary>
    [CreateAssetMenu(menuName = "Tanks/Settings", fileName = "TanksSettings")]
    public class TanksSettings : ScriptableObject
    {
        public const string ResourcePath = "TanksSettings";

        [Tooltip("Netcode the game runs on. Every netcode has its own scene: Tanks_<Netcode>.")]
        public Netcode netcode = Netcode.Mirror;

        [Tooltip("LightNet relay address: a host name or IP for TCP + UDP, or a ws:// or wss:// URL (with its port) for WebSocket.")]
        public string relayAddress = "localhost";
        [Tooltip("Relay TCP port (reliable channel).")]
        public int relayTcpPort = 7777;
        [Tooltip("Relay UDP port (unreliable channel). On Edgegap it is the same external port as TCP.")]
        public int relayUdpPort = 7777;

        [Header("Edgegap matchmaker (optional)")]
        [Tooltip("Off: players host and join rooms with a code on the relay above. On: Find Match asks the Edgegap matchmaker, which starts a relay for the match; one matched player hosts it, the others join automatically.")]
        public bool useMatchmaker = false;
        [Tooltip("Matchmaker API URL from the Edgegap dashboard, e.g. https://om-xxxxxxxxxx.edgegap.net")]
        public string matchmakerUrl = "";
        [Tooltip("Matchmaker Auth Token from the Edgegap dashboard. Safe to ship in clients: it gives no access to the Edgegap account.")]
        public string matchmakerToken = "";
        [Tooltip("Profile name in the matchmaker config (matchmaker-config.json).")]
        public string matchmakerProfile = "casual";
        [Tooltip("Port name on the relay's Edgegap app version.")]
        public string matchmakerPortName = "relay";
        [Tooltip("Connect to the match relay over encrypted WebSocket (wss://) instead of TCP + UDP. Needs a WS port with TLS Upgrade on the app version.")]
        public bool matchmakerUseWebSocket = false;
        [Tooltip("Name of that WS port on the relay's Edgegap app version.")]
        public string matchmakerWebSocketPortName = "relay-ws";
        [Tooltip("Ping Edgegap's latency beacons and send the results in each ticket, so players are matched with players and relays near them. Needs a 'beacons' latencies rule in the profile (matchmaker-config.json has one); turn it off for a profile without it. Not available in browsers.")]
        public bool matchmakerLatencyBeacons = true;

        /// <summary>Room code the host must create: set for a matchmade match, null when the relay picks one.</summary>
        [NonSerialized] public string hostRoomCode;

        /// <summary>The relay as players see it: address:port, or the ws:// / wss:// URL.</summary>
        public string RelayLabel => LightNet.Relay.RelayWebSocket.IsWebSocketAddress(relayAddress) ? relayAddress : $"{relayAddress}:{relayTcpPort}";

        public string SceneName => SceneFor(netcode);
        public static string SceneFor(Netcode n) => "Tanks_" + n;

        static TanksSettings current;

        /// <summary>The settings asset with command-line overrides applied (a copy: the asset is not changed).</summary>
        public static TanksSettings Current
        {
            get
            {
                if (current != null) return current;
                TanksSettings asset = Resources.Load<TanksSettings>(ResourcePath);
                current = asset != null ? Instantiate(asset) : CreateInstance<TanksSettings>();

                if (Enum.TryParse(Args.Value("-netcode"), true, out Netcode n)) current.netcode = n;
                string relay = Args.Value("-relay");
                if (!string.IsNullOrEmpty(relay)) current.relayAddress = relay;
                if (int.TryParse(Args.Value("-relayPort"), out int tcp))
                {
                    current.relayTcpPort = tcp;
                    current.relayUdpPort = tcp;
                }
                if (int.TryParse(Args.Value("-relayUdpPort"), out int udp)) current.relayUdpPort = udp;
                string mmUrl = Args.Value("-matchmaker");
                if (!string.IsNullOrEmpty(mmUrl))
                {
                    current.useMatchmaker = true;
                    current.matchmakerUrl = mmUrl;
                }
                string mmToken = Args.Value("-matchmakerToken");
                if (!string.IsNullOrEmpty(mmToken)) current.matchmakerToken = mmToken;
                if (Args.Flag("-matchmakerWebSocket")) current.matchmakerUseWebSocket = true;
                if (Args.Flag("-noBeacons")) current.matchmakerLatencyBeacons = false;
                string mmProfile = Args.Value("-matchmakerProfile");
                if (!string.IsNullOrEmpty(mmProfile)) current.matchmakerProfile = mmProfile;
                return current;
            }
        }
    }

    public static class Args
    {
        public static string Value(string name)
        {
            string[] args = Environment.GetCommandLineArgs();
            int i = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        public static bool Flag(string name) => Environment.GetCommandLineArgs().Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
    }
}
