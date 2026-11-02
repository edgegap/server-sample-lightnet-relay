using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Edgegap;
using Edgegap.Matchmaking;
using LightNet.Relay;
using Newtonsoft.Json;
using UnityEngine;

namespace Tanks
{
    /// <summary>
    /// Optional Edgegap matchmaking (<see cref="TanksSettings.useMatchmaker"/>, off by default).<br/>
    /// Find Match → ticket → the matchmaker groups players and starts a LightNet relay on Edgegap for the
    /// match → each player asks that relay for its role with its ticket id: the first one hosts the match
    /// room, the others join it. <see cref="OnMatched"/> gets the role and room code; the game then starts
    /// its netcode exactly as for a hand-made room.
    /// </summary>
    public class TanksMatchmaker
    {
        /// <summary>Relay found and role assigned: isHost, room code. The relay address and ports are already in the settings.</summary>
        public Action<bool, string> OnMatched;

        public string Status { get; private set; } = "Checking matchmaker...";
        public bool IsHealthy { get; private set; }
        public bool IsBusy { get; private set; }

        const int RoleAttempts = 10;
        const float RoleRetrySeconds = 2;

        readonly TanksSettings settings;
        readonly GroupClient<TanksGroupUpRequest, TanksTicketAttributes> client;
        string ticketId;
        bool assigned;
        RelayPeer rolePeer;
        int roleAttempt;
        float retryAt = -1;

        public TanksMatchmaker(MonoBehaviour owner, TanksSettings settings)
        {
            this.settings = settings;
            if (string.IsNullOrWhiteSpace(settings.matchmakerUrl) || string.IsNullOrWhiteSpace(settings.matchmakerToken))
            {
                Status = "Set the matchmaker URL and token in TanksSettings.";
                Debug.LogError("Tanks matchmaker: " + Status);
                return;
            }
            client = new GroupClient<TanksGroupUpRequest, TanksTicketAttributes>(
                owner,
                settings.matchmakerUrl.Trim().TrimEnd('/'),
                settings.matchmakerToken.Trim(), // sent raw as the Authorization header
                3, 1f, 10, 30f, true, false);
            client.Initialize(OnMonitorUpdate, OnGroupUpdate);
        }

        public void FindMatch()
        {
            if (client == null || !IsHealthy || IsBusy) return;
            IsBusy = true;
            assigned = false;
            if (!settings.matchmakerLatencyBeacons)
            {
                CreateTicket(null);
                return;
            }
            Dictionary<string, float> fixedPings = ParseBeacons(Args.Value("-beacons"));
            if (fixedPings != null)
            {
                CreateTicket(fixedPings); // tests: pretend to be somewhere else
                return;
            }
            Status = "Measuring latency...";
            client.Beacons(
                beacons => client.MeasureBeaconsRoundTripTime(beacons.Beacons, CreateTicket),
                (error, request) => Fail("Couldn't get the latency beacons."));
        }

        void CreateTicket(Dictionary<string, float> pings)
        {
            // 0 ms means the ping failed, not "closest"
            Dictionary<string, float> beacons = pings?.Where(p => p.Value > 0).ToDictionary(p => p.Key, p => p.Value);
            if (settings.matchmakerLatencyBeacons)
            {
                Debug.Log("Tanks matchmaker: beacons " + (beacons == null || beacons.Count == 0 ? "none measured" : string.Join(", ", beacons.Select(b => $"{b.Key} {b.Value:F0} ms"))));
            }
            Status = "Searching for a match...";
            // abandon: the SDK starts with an empty group and refuses to create another without it
            client.CreateGroup(new TanksGroupUpRequest(settings.matchmakerProfile, settings.matchmakerLatencyBeacons ? beacons ?? new Dictionary<string, float>() : null), abandon: true);
        }

        /// <summary>"-beacons City=ms,City=ms": fixed pings instead of measuring them.</summary>
        static Dictionary<string, float> ParseBeacons(string arg)
        {
            if (string.IsNullOrWhiteSpace(arg)) return null;
            var beacons = new Dictionary<string, float>();
            foreach (string pair in arg.Split(','))
            {
                string[] kv = pair.Split('=');
                if (kv.Length == 2 && float.TryParse(kv[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float ms)) beacons[kv[0].Trim()] = ms;
            }
            return beacons;
        }

        public void Cancel()
        {
            if (client == null || !IsBusy || assigned) return;
            Status = "Cancelling...";
            client.StopMatchmaking(() =>
            {
                GroupUpResponseDTO current = client.Group.Current;
                if (current?.GroupID != null)
                {
                    // too late: a match was found, take it
                    Status = "Match already found, joining...";
                    client.ResumeMatchmaking(current, abandon: true);
                    return;
                }
                IsBusy = false;
                Status = "Matchmaking cancelled.";
            });
        }

        /// <summary>Call every frame.</summary>
        public void Poll()
        {
            TanksSessions.FlushLightNetLogs();
            rolePeer?.Poll();
            if (retryAt >= 0 && Time.realtimeSinceStartup >= retryAt)
            {
                retryAt = -1;
                RequestRole();
            }
        }

        void OnMonitorUpdate(Observable<MonitorResponseDTO> monitor, ObservableActionType action, string message)
        {
            if (action == ObservableActionType.Update && message == "healthy")
            {
                IsHealthy = true;
                if (!IsBusy) Status = "Ready to matchmake.";
            }
            else if (action == ObservableActionType.Update || action == ObservableActionType.Error)
            {
                IsHealthy = false;
                Status = "Matchmaker unavailable.";
            }
        }

        void OnGroupUpdate(Observable<GroupUpResponseDTO> group, ObservableActionType action, string message)
        {
            if (action == ObservableActionType.Error)
            {
                Fail("Matchmaking failed, try again.");
                return;
            }
            if (action != ObservableActionType.Update || assigned) return;

            GroupUpResponseDTO current = group.Current;
            if (current?.GroupID == null) return;
            switch (current.Status)
            {
                case "SEARCHING":
                    Status = "Searching for players...";
                    break;
                case "MATCH_FOUND":
                    Status = "Match found, starting the relay...";
                    break;
                case "HOST_ASSIGNED":
                    // read the assignment now: the SDK clears the group later
                    DeploymentDTO relay = current.Assignment;
                    string portName = settings.matchmakerUseWebSocket ? settings.matchmakerWebSocketPortName : settings.matchmakerPortName;
                    if (relay?.Ports == null || !relay.Ports.TryGetValue(portName, out PortMappingDTO port)
                        || !int.TryParse(port.External, out int external))
                    {
                        Fail($"The relay has no port named '{portName}'.");
                        return;
                    }
                    if (settings.matchmakerUseWebSocket)
                    {
                        // TLS Upgrade: Edgegap's certificate is for the deployment's host name, not its IP
                        settings.relayAddress = $"wss://{relay.Fqdn}:{external}";
                    }
                    else
                    {
                        settings.relayAddress = !string.IsNullOrEmpty(relay.PublicIP) ? relay.PublicIP : relay.Fqdn;
                        // the relay's TCP/UDP port: one external port for both protocols
                        settings.relayTcpPort = settings.relayUdpPort = external;
                    }
                    ticketId = current.TicketID;
                    assigned = true;
                    roleAttempt = 0;
                    Debug.Log($"Tanks matchmaker: relay for the match at {settings.relayAddress}{(settings.matchmakerUseWebSocket ? "" : ":" + external)}, ticket {ticketId}");
                    RequestRole();
                    break;
                case "CANCELLED":
                    Fail("No match found in time, try again.");
                    break;
            }
        }

        void RequestRole()
        {
            roleAttempt++;
            Status = $"Joining the match relay (attempt {roleAttempt}/{RoleAttempts})...";
            rolePeer = new RelayPeer
            {
                OnMatchAssigned = (isHost, code) =>
                {
                    rolePeer = null;
                    IsBusy = false;
                    Status = isHost ? "Hosting the match." : "Joining the match.";
                    Debug.Log($"Tanks matchmaker: {(isHost ? "hosting" : "joining")} match room {code}");
                    OnMatched?.Invoke(isHost, code);
                },
            };
            rolePeer.OnDisconnected = reason =>
            {
                rolePeer = null;
                Debug.LogWarning($"Tanks matchmaker: relay role request failed ({reason})");
                // the relay may still be starting: retry, unless it refused the ticket
                if (reason.StartsWith("join failed") || roleAttempt >= RoleAttempts) Fail("Couldn't join the match relay.");
                else retryAt = Time.realtimeSinceStartup + RoleRetrySeconds;
            };
            rolePeer.RequestMatchRole(settings.relayAddress, settings.relayTcpPort, settings.relayUdpPort, ticketId);
        }

        void Fail(string status)
        {
            Status = status;
            IsBusy = false;
            retryAt = -1;
            Debug.LogWarning("Tanks matchmaker: " + status);
        }
    }

    /// <summary>
    /// Ticket attributes: one per rule of the profile, named like the rule. <c>beacons</c> is for the profile's
    /// <c>latencies</c> rule (left out when <see cref="TanksSettings.matchmakerLatencyBeacons"/> is off).
    /// </summary>
    public class TanksTicketAttributes
    {
        [JsonProperty("beacons", NullValueHandling = NullValueHandling.Ignore)]
        public Dictionary<string, float> Beacons;
    }

    /// <summary>A solo player is a group of one, ready at once.</summary>
    public class TanksGroupUpRequest : GroupUpRequestDTO<TanksTicketAttributes>
    {
        public TanksGroupUpRequest(string profile, Dictionary<string, float> beacons) : base(profile, true)
        {
            Attributes = new TanksTicketAttributes { Beacons = beacons };
        }
    }
}
