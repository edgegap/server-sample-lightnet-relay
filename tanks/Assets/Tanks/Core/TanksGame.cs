using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LightNet.Relay;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Tanks
{
    /// <summary>
    /// Runs the game on whichever netcode <see cref="TanksSettings"/> picks: loads its scene, shows the HUD
    /// (host / join by room code / switch netcode) and handles the command line.<br/>
    /// Command line (all optional):
    /// <code>
    /// -netcode Mirror|FishNet|PurrNet|NGO                netcode (default: TanksSettings.asset)
    /// -relay ADDRESS -relayPort PORT [-relayUdpPort UDP]   relay to use
    /// -host [-codeFile PATH]                              host a room, write its code to PATH
    /// -join CODE                                          join a room
    /// -matchmaker URL -matchmakerToken TOKEN              use the Edgegap matchmaker (TanksSettings.useMatchmaker)
    /// -findMatch                                          find a match through the matchmaker at start
    /// -matchmakerWebSocket  -noBeacons  -beacons City=ms,City=ms   wss:// to the match relay / no latency / fixed pings (tests)
    /// -e2e [-expectTanks N] [-timeout S]                  test: drive, fire, pass when N tanks sync through the relay
    ///      [-holdAfterPass S]                             the host keeps its room S seconds after passing (default 3)
    /// -switchFrom NETCODE                                 test: start on NETCODE, then switch to -netcode at runtime
    /// </code>
    /// </summary>
    public class TanksGame : MonoBehaviour
    {
        ITanksSession session;
        TanksSettings settings;
        TanksMatchmaker matchmaker;
        bool findMatchPending;
        string joinCode = "";
        bool autoStarted;
        Netcode? switchFrom;

        // e2e
        bool e2e;
        int expectTanks = 2;
        float timeout = 90;
        float holdAfterPass = 3;
        float started;
        bool wasActive;
        bool passed;
        float passedAt;
        float maxRemoteMove;
        string status = "";
        readonly Dictionary<TankCore, (float seenAt, Vector3? baseline)> remote = new Dictionary<TankCore, (float, Vector3?)>();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Install()
        {
            if (FindFirstObjectByType<TanksGame>() != null) return;
            var go = new GameObject(nameof(TanksGame));
            DontDestroyOnLoad(go);
            go.AddComponent<TanksGame>();
        }

        void Awake()
        {
            settings = TanksSettings.Current;
            e2e = Args.Flag("-e2e");
            if (int.TryParse(Args.Value("-expectTanks"), out int n)) expectTanks = n;
            if (float.TryParse(Args.Value("-timeout"), out float t)) timeout = t;
            if (float.TryParse(Args.Value("-holdAfterPass"), out float hold)) holdAfterPass = hold;
            joinCode = Args.Value("-join") ?? "";
            TankCore.AutoDrive = TankCore.AutoFire = e2e && Args.Value("-join") != null;
            findMatchPending = Args.Flag("-findMatch");
            if (Enum.TryParse(Args.Value("-switchFrom"), true, out Netcode from) && from != settings.netcode) switchFrom = from;
            if (Application.isBatchMode) Application.targetFrameRate = 60;
        }

        void Start()
        {
            started = Time.realtimeSinceStartup;
            SceneManager.sceneLoaded += (_, _) => session = null;
            if (settings.useMatchmaker)
            {
                matchmaker = new TanksMatchmaker(this, settings) { OnMatched = StartMatch };
            }
        }

        void Update()
        {
            matchmaker?.Poll();
            if (session == null)
            {
                session = TanksSessions.Find();
                if (session == null)
                {
                    // boot scene: open the selected netcode's scene
                    string scene = TanksSettings.SceneFor(switchFrom ?? settings.netcode);
                    if (SceneManager.GetActiveScene().name != scene) SceneManager.LoadScene(scene);
                    return;
                }
                if (switchFrom == session.Netcode)
                {
                    Debug.Log($"Tanks: switching from {session.Netcode} to {settings.netcode}");
                    switchFrom = null;
                    SwitchTo(settings.netcode);
                    return;
                }
                settings.netcode = session.Netcode;
                Debug.Log($"Tanks: running on {session.Netcode} through the LightNet relay at {settings.RelayLabel}");
            }

            if (!autoStarted)
            {
                autoStarted = true;
                if (Args.Flag("-host")) Host();
                else if (Args.Value("-join") != null) Join();
            }
            // nobody joined our matchmade room in time: the relay closed it, so stop hosting and look for another match
            if (RelayPeer.LastDisconnectReason == RelayPeer.NoPlayersJoined && matchmaker != null)
            {
                Debug.Log("Tanks: nobody joined the room in time, finding another match");
                RelayPeer.LastDisconnectReason = null;
                if (session.IsActive) session.Stop();
                findMatchPending = true;
            }

            if (findMatchPending && matchmaker != null && matchmaker.IsHealthy && !session.IsActive)
            {
                findMatchPending = false;
                matchmaker.FindMatch();
            }

            if (e2e) UpdateTest();
        }

        void Host()
        {
            settings.hostRoomCode = null;
            string codeFile = Args.Value("-codeFile");
            session.StartHost(settings, code =>
            {
                Debug.Log($"Tanks: room code {code}");
                if (codeFile != null) File.WriteAllText(codeFile, code);
            });
        }

        void Join() => session.StartClient(settings, joinCode.Trim().ToUpperInvariant());

        /// <summary>The matchmaker found a match: the relay address is in the settings, the relay picked our role.</summary>
        void StartMatch(bool isHost, string code)
        {
            if (session == null || session.IsActive) return;
            if (isHost)
            {
                settings.hostRoomCode = code;
                session.StartHost(settings, c => Debug.Log($"Tanks: hosting match room {c}"));
            }
            else
            {
                joinCode = code;
                TankCore.AutoDrive = TankCore.AutoFire = e2e;
                session.StartClient(settings, code);
            }
        }

        void SwitchTo(Netcode netcode)
        {
            // NGO and FishNet keep their NetworkManager across scene loads: drop the old netcode first
            session.Stop();
            Destroy(((MonoBehaviour)session).gameObject);
            session = null;
            settings.netcode = netcode;
            SceneManager.LoadScene(settings.SceneName);
        }

        // ---------------------------------------------------------------- HUD

        void OnGUI()
        {
            if (session == null) return;
            GUILayout.BeginArea(new Rect(10, 10, 300, 400));
            GUILayout.Label($"Netcode: {session.Netcode}   Relay: {settings.RelayLabel}");

            if (!session.IsActive)
            {
                GUILayout.BeginHorizontal();
                foreach (Netcode n in Enum.GetValues(typeof(Netcode)))
                {
                    GUI.enabled = n != session.Netcode;
                    if (GUILayout.Button(n.ToString())) SwitchTo(n);
                }
                GUI.enabled = true;
                GUILayout.EndHorizontal();

                GUILayout.BeginHorizontal();
                GUILayout.Label("Relay", GUILayout.Width(45));
                settings.relayAddress = GUILayout.TextField(settings.relayAddress);
                if (int.TryParse(GUILayout.TextField(settings.relayTcpPort.ToString(), GUILayout.Width(55)), out int port) && port != settings.relayTcpPort)
                {
                    settings.relayTcpPort = port;
                    settings.relayUdpPort = port;
                }
                GUILayout.EndHorizontal();

                if (matchmaker != null)
                {
                    GUILayout.Label("Edgegap matchmaker: " + matchmaker.Status);
                    if (matchmaker.IsBusy)
                    {
                        if (GUILayout.Button("Cancel")) matchmaker.Cancel();
                    }
                    else
                    {
                        GUI.enabled = matchmaker.IsHealthy;
                        if (GUILayout.Button("Find Match")) matchmaker.FindMatch();
                        GUI.enabled = true;
                    }
                    GUILayout.Label("Or host / join a room by code:");
                }

                if (GUILayout.Button("Host (creates a room)")) Host();

                GUILayout.BeginHorizontal();
                joinCode = GUILayout.TextField(joinCode, 6, GUILayout.Width(80)).ToUpperInvariant();
                GUI.enabled = joinCode.Length == RelayProtocol.CodeLength;
                if (GUILayout.Button("Join room")) Join();
                GUI.enabled = true;
                GUILayout.EndHorizontal();
            }
            else
            {
                if (session.IsServer) GUILayout.Label(session.RoomCode != null ? $"Room code: {session.RoomCode}" : "Creating a room...");
                else GUILayout.Label($"Room {joinCode}");
                if (GUILayout.Button("Stop")) session.Stop();
            }

            if (e2e) GUILayout.Label(status);
            GUILayout.EndArea();
        }

        // ---------------------------------------------------------------- e2e test

        void UpdateTest()
        {
            if (session.IsActive) wasActive = true;
            string role = session.IsServer ? "host" : "client";

            if (passed)
            {
                // The host stays a few seconds so the client sees everything, then closes the room.
                // The client keeps driving until the host is gone, so the host can see it move.
                bool done = session.IsServer ? Time.realtimeSinceStartup - passedAt > holdAfterPass : !session.IsActive || Time.realtimeSinceStartup - passedAt > timeout;
                if (done) Quit(0);
                return;
            }

            if (wasActive && !session.IsActive)
            {
                Debug.LogError($"E2E FAIL ({role}) disconnected before passing: {status}");
                Quit(1);
                return;
            }

            // remote tanks: wait for the owner's spawn placement to arrive, then measure how far they drive
            foreach (TankCore tank in TankCore.All.Where(t => t.Net != null && t.Net.IsSpawned && !t.IsLocal))
            {
                if (!remote.TryGetValue(tank, out var r)) remote[tank] = r = (Time.time, null);
                if (r.baseline == null)
                {
                    if (Time.time - r.seenAt > 1.5f) remote[tank] = (r.seenAt, tank.transform.position);
                }
                else
                {
                    maxRemoteMove = Mathf.Max(maxRemoteMove, Vector3.Distance(r.baseline.Value, tank.transform.position));
                }
            }

            int tanks = TankCore.All.Count(t => t.Net != null && t.Net.IsSpawned);
            TankCore local = TankCore.All.FirstOrDefault(t => t.IsLocal);
            status = $"netcode={session.Netcode} tanks={tanks}/{expectTanks} remoteMoved={maxRemoteMove:F1}m projectiles={ProjectileCore.SpawnedCount} " +
                     $"localHealth={(local != null ? local.Net.Health.ToString() : "-")} reliableIn={RelayPeer.TotalReceivedReliable}B unreliableIn={RelayPeer.TotalReceivedUnreliable}B";

            bool ok = tanks >= expectTanks && RelayPeer.TotalReceivedReliable > 0 && RelayPeer.TotalReceivedUnreliable > 0 && ProjectileCore.SpawnedCount > 0;
            // the host checks it saw the client's tank move (owner authoritative transform over the relay)
            if (session.IsServer) ok &= maxRemoteMove > 1f;
            if (ok)
            {
                passed = true;
                passedAt = Time.realtimeSinceStartup;
                Debug.Log($"E2E PASS ({role}) {status}");
            }
            else if (Time.realtimeSinceStartup - started > timeout)
            {
                Debug.LogError($"E2E FAIL ({role}) {status}");
                Quit(1);
            }
        }

        void Quit(int code)
        {
            session?.Stop();
            Application.Quit(code);
        }
    }
}
