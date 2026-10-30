using System;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.AI;

namespace Mirror.LightNetRelay
{
    /// <summary>
    /// Puts the Tanks sample on the LightNet relay without editing its scene: swaps the NetworkManager's
    /// transport for <see cref="LightNetRelayTransport"/> and adds a room code box next to the HUD.<br/>
    /// Command line (all optional):
    /// <code>
    /// -relay ADDRESS -relayPort TCP -relayUdpPort UDP   relay to use (default localhost 7777/7777)
    /// -host [-codeFile PATH]                           host a room, write its code to PATH
    /// -join CODE                                       join a room
    /// -e2e [-expectTanks N] [-timeout S]               test: pass when N tanks are synced through the relay
    /// </code>
    /// </summary>
    public class LightNetRelayBootstrap : MonoBehaviour
    {
        LightNetRelayTransport transport;
        NetworkManager manager;

        bool e2e;
        int expectTanks = 2;
        float timeout = 60;
        float started;
        bool passed;
        float passedAt;
        float maxRemoteMove;
        string status = "";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Install()
        {
            NetworkManager nm = NetworkManager.singleton != null ? NetworkManager.singleton : FindObjectOfType<NetworkManager>();
            if (nm == null || nm.GetComponent<LightNetRelayBootstrap>() != null) return;
            nm.gameObject.AddComponent<LightNetRelayBootstrap>();
        }

        void Awake()
        {
            manager = GetComponent<NetworkManager>();
            if (!TryGetComponent(out transport)) transport = gameObject.AddComponent<LightNetRelayTransport>();
            // NetworkManager.transport is protected in this Mirror version, and StartHost/StartClient
            // copy it into Transport.activeTransport, so set the field itself
            typeof(NetworkManager).GetField("transport", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                .SetValue(manager, transport);
            Transport.activeTransport = transport;

            string relay = Arg("-relay");
            if (relay != null) manager.networkAddress = relay;
            if (int.TryParse(Arg("-relayPort"), out int tcp)) transport.relayTcpPort = tcp;
            if (int.TryParse(Arg("-relayUdpPort"), out int udp)) transport.relayUdpPort = udp;
            else if (tcp > 0) transport.relayUdpPort = tcp;

            e2e = Flag("-e2e");
            if (int.TryParse(Arg("-expectTanks"), out int n)) expectTanks = n;
            if (float.TryParse(Arg("-timeout"), out float t)) timeout = t;

            string codeFile = Arg("-codeFile");
            transport.OnRoomCreated = code =>
            {
                if (codeFile != null) File.WriteAllText(codeFile, code);
            };
        }

        void Start()
        {
            started = Time.realtimeSinceStartup;
            if (Flag("-host"))
            {
                manager.StartHost();
            }
            else if (Arg("-join") != null)
            {
                transport.roomCode = Arg("-join");
                manager.StartClient();
            }
        }

        void Update()
        {
            if (!e2e) return;
            if (passed)
            {
                // The host stays a few seconds so the client sees everything, then closes the room.
                // The client keeps driving until the host is gone, so the host can see it move.
                bool done = NetworkServer.active ? Time.realtimeSinceStartup - passedAt > 3 : !NetworkClient.active || Time.realtimeSinceStartup - passedAt > timeout;
                if (done)
                {
                    Application.Quit(0);
                    return;
                }
            }

            NetworkIdentity[] tanks = NetworkClient.spawned.Values.Where(i => i != null && i.GetComponent("Tank") != null).ToArray();
            NetworkIdentity local = NetworkClient.localPlayer;

            // the joining client drives its tank, so the host can see it move through the relay
            if (local != null && !NetworkServer.active)
            {
                // Move() applies right away; velocity would be reset by Tank.Update (no input in batch mode)
                if (local.TryGetComponent(out NavMeshAgent agent) && agent.isOnNavMesh) agent.Move(local.transform.forward * agent.speed * Time.deltaTime);
            }

            foreach (NetworkIdentity tank in tanks)
            {
                if (tank == local) continue;
                Vector3 spawn = tank.TryGetComponent(out E2ESpawnMarker marker) ? marker.position : MarkSpawn(tank);
                maxRemoteMove = Mathf.Max(maxRemoteMove, Vector3.Distance(spawn, tank.transform.position));
            }

            status = $"tanks={tanks.Length}/{expectTanks} remoteMoved={maxRemoteMove:F1}m localPos={(local != null ? local.transform.position.ToString("F1") : "-")} reliableIn={transport.ReceivedReliable}B unreliableIn={transport.ReceivedUnreliable}B";
            if (passed) return;
            bool ok = tanks.Length >= expectTanks && transport.ReceivedReliable > 0 && transport.ReceivedUnreliable > 0;
            // the host checks it saw the client's tank move (NetworkTransform over the relay)
            if (NetworkServer.active) ok &= maxRemoteMove > 1f;
            if (ok)
            {
                passed = true;
                passedAt = Time.realtimeSinceStartup;
                Debug.Log($"E2E PASS ({(NetworkServer.active ? "host" : "client")}) {status}");
            }
            else if (Time.realtimeSinceStartup - started > timeout)
            {
                Debug.LogError($"E2E FAIL ({(NetworkServer.active ? "host" : "client")}) {status}");
                Application.Quit(1);
            }
        }

        static Vector3 MarkSpawn(NetworkIdentity tank)
        {
            E2ESpawnMarker marker = tank.gameObject.AddComponent<E2ESpawnMarker>();
            marker.position = tank.transform.position;
            return marker.position;
        }

        void OnGUI()
        {
            GUILayout.BeginArea(new Rect(10, 140, 260, 120));
            if (NetworkServer.active && transport.RoomCode != null)
            {
                GUILayout.Label("Room code: " + transport.RoomCode);
            }
            else if (!NetworkClient.active && !NetworkServer.active)
            {
                GUILayout.Label("LightNet relay: Host creates a room.\nTo join, type its code, then Client.");
                GUILayout.BeginHorizontal();
                GUILayout.Label("Room code", GUILayout.Width(70));
                transport.roomCode = GUILayout.TextField(transport.roomCode ?? "", 6).ToUpperInvariant();
                GUILayout.EndHorizontal();
            }
            if (e2e) GUILayout.Label(status);
            GUILayout.EndArea();
        }

        static string Arg(string name)
        {
            string[] args = Environment.GetCommandLineArgs();
            int i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        static bool Flag(string name) => Environment.GetCommandLineArgs().Contains(name);

        class E2ESpawnMarker : MonoBehaviour
        {
            public Vector3 position;
        }
    }
}
