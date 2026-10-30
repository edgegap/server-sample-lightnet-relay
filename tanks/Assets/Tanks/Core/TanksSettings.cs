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

        [Tooltip("LightNet relay address (host name or IP).")]
        public string relayAddress = "localhost";
        [Tooltip("Relay TCP port (reliable channel).")]
        public int relayTcpPort = 7777;
        [Tooltip("Relay UDP port (unreliable channel). On Edgegap it is the same external port as TCP.")]
        public int relayUdpPort = 7777;

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
