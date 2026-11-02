using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

// Backfills for a match relay. A backfill is a relay-owned ticket that carries this deployment's address; when the
// matchmaker puts a player's ticket in it, that player gets HOST_ASSIGNED with this relay and asks for its role
// (MatchRole). Each backfill is opened for a key the relay chooses: a room code (a seat in that room) or NewRoom (the
// player hosts a new room on this relay). The relay says how many it wants per key, in priority order; this class
// keeps that many open, at most MaxOpen in all, creating them no faster than CreateSpacingSeconds apart.
// Same API flow as the verified Megacity Metro sample (EdgegapBackfillSystem.cs).
//
// Needs MATCHMAKER_URL and MATCHMAKER_TOKEN (the matchmaker's client-safe Auth Token) on the app version, and the
// variables Edgegap injects into matchmaker deployments (MM_*, ARBITRIUM_*). Runs on the relay's main loop: HTTP calls
// run in the background and their results are applied in Update.

class MatchBackfill
{
    public const string NewRoom = "new room";

    /// <summary>How many backfills the relay wants open for a key, and the real players' tickets to send with them.</summary>
    public struct Want
    {
        public string Key;
        public int Count;
        public List<JsonObject> Tickets;
    }

    const int MaxOpen = 5;                      // per relay: backfill creation is rate limited (5/s on the free tier)
    const double CreateSpacingSeconds = 0.3;
    const double PollSeconds = 2;
    const double RetrySeconds = 5;              // after a failed create (429: rate limited)
    const int MaxTicketsPerBackfill = 8;

    /// <summary>The matchmaker put a player's ticket in one of our backfills: key, ticket id, ticket.</summary>
    public Action<string, string, JsonObject> OnTicketAssigned;

    readonly HttpClient http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
    readonly string url, profile;
    readonly JsonObject assignment;
    readonly Dictionary<string, string> open = new Dictionary<string, string>();   // backfill id -> key
    readonly Dictionary<string, int> creating = new Dictionary<string, int>();     // key -> creates in flight
    readonly HashSet<string> busy = new HashSet<string>();                         // backfills with a poll or delete in flight
    readonly ConcurrentQueue<Action> results = new ConcurrentQueue<Action>();
    DateTime nextPoll, nextCreate;
    bool closing;
    string lastSummary;

    MatchBackfill(string url, string token, string profile, JsonObject assignment)
    {
        this.url = url.TrimEnd('/');
        this.profile = profile;
        this.assignment = assignment;
        http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", token); // raw, no "Bearer"
    }

    /// <summary>Null (with the reason logged) when this relay can't backfill: not started by the matchmaker, or no matchmaker URL/token.</summary>
    public static MatchBackfill FromEnvironment()
    {
        string url = Environment.GetEnvironmentVariable("MATCHMAKER_URL");
        string token = Environment.GetEnvironmentVariable("MATCHMAKER_TOKEN");
        string profile = Environment.GetEnvironmentVariable("MM_MATCH_PROFILE");
        string requestId = Environment.GetEnvironmentVariable("ARBITRIUM_REQUEST_ID");
        string ports = Environment.GetEnvironmentVariable("ARBITRIUM_PORTS_MAPPING");
        if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(token))
        {
            Console.WriteLine("backfill: off (set MATCHMAKER_URL and MATCHMAKER_TOKEN on the app version to fill free seats)");
            return null;
        }
        if (string.IsNullOrEmpty(profile) || string.IsNullOrEmpty(requestId) || string.IsNullOrEmpty(ports))
        {
            Console.WriteLine("backfill: off (MM_MATCH_PROFILE, ARBITRIUM_REQUEST_ID or ARBITRIUM_PORTS_MAPPING missing)");
            return null;
        }

        // what backfilled players receive: this deployment, as the matchmaker describes deployments
        string fqdn = requestId + ".pr.edgegap.net";
        JsonObject portMap = JsonNode.Parse(ports)?["ports"]?.AsObject() ?? new JsonObject();
        foreach (var port in portMap)
        {
            if (port.Value is JsonObject p && p["link"] == null) p["link"] = $"{fqdn}:{p["external"]}";
        }
        var assignment = new JsonObject
        {
            ["request_id"] = requestId,
            ["fqdn"] = fqdn,
            ["public_ip"] = Environment.GetEnvironmentVariable("ARBITRIUM_PUBLIC_IP") ?? "",
            ["ports"] = portMap.DeepClone(),
            ["location"] = Parse(Environment.GetEnvironmentVariable("ARBITRIUM_DEPLOYMENT_LOCATION")) ?? new JsonObject(),
        };
        Console.WriteLine($"backfill: on, matchmaker {url.TrimEnd('/')}, profile {profile}");
        return new MatchBackfill(url, token, profile, assignment);
    }

    /// <summary>A matched player's ticket as the matchmaker injected it (MM_TICKET_&lt;id&gt;), in the shape backfills take; null if missing.</summary>
    public static JsonObject TicketFromEnvironment(string id)
    {
        return Parse(Environment.GetEnvironmentVariable("MM_TICKET_" + id)) is JsonObject t ? Ticket(t) : null;
    }

    /// <summary>Call every loop iteration. <paramref name="wants"/> lists what to keep open, most important first.</summary>
    public void Update(Func<List<Want>> wants)
    {
        while (results.TryDequeue(out Action apply)) apply();
        if (closing) return;
        DateTime now = DateTime.UtcNow;

        // after the results: a seated player takes a seat
        var targets = new Dictionary<string, int>();
        var tickets = new Dictionary<string, List<JsonObject>>();
        int budget = MaxOpen;
        foreach (Want want in wants())
        {
            int count = want.Tickets == null || want.Tickets.Count == 0 ? 0 : Math.Clamp(want.Count, 0, budget);
            budget -= count;
            targets[want.Key] = count;
            tickets[want.Key] = want.Tickets;
        }

        // open what is missing, in priority order, one create at a time
        foreach (var target in targets)
        {
            if (now < nextCreate) break;
            if (Have(target.Key) < target.Value)
            {
                Create(target.Key, tickets[target.Key]);
                nextCreate = now.AddSeconds(CreateSpacingSeconds);
            }
        }

        // close what is no longer wanted (a room filled up or closed)
        foreach (var group in open.GroupBy(b => b.Value).ToList())
        {
            int extra = group.Count() + Creating(group.Key) - (targets.TryGetValue(group.Key, out int t) ? t : 0);
            foreach (string id in group.Select(b => b.Key).Where(id => !busy.Contains(id)).Take(Math.Max(0, extra)).ToList()) Delete(id);
        }

        if (now >= nextPoll)
        {
            nextPoll = now.AddSeconds(PollSeconds);
            foreach (string id in open.Keys.Where(id => !busy.Contains(id)).ToList()) Poll(id);
        }

        string summary = targets.Count == 0 && open.Count == 0 ? "nothing to fill"
            : string.Join(", ", targets.Keys.Union(open.Values).Distinct().Select(k => $"{k}: {open.Values.Count(v => v == k)} open / {(targets.TryGetValue(k, out int n) ? n : 0)} wanted"));
        if (summary != lastSummary)
        {
            lastSummary = summary;
            Console.WriteLine("backfill: " + summary);
        }
    }

    /// <summary>A player with an unknown ticket is asking to join: check the backfills now instead of in 2 s.</summary>
    public void PollSoon() => nextPoll = DateTime.UtcNow;

    /// <summary>Delete every open backfill (waits up to 5 s), so no player is sent to a relay that is stopping.</summary>
    public void CloseAll()
    {
        closing = true;
        // a backfill still being created would stay open: wait for those answers first
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        do
        {
            while (results.TryDequeue(out Action apply)) apply();
            if (creating.Values.Sum() > 0) System.Threading.Thread.Sleep(50);
        } while (creating.Values.Sum() > 0 && DateTime.UtcNow < deadline);
        if (open.Count == 0) return;
        Console.WriteLine($"backfill: closing {open.Count} backfill(s)");
        Task[] deletes = open.Keys.Select(id => http.DeleteAsync($"{url}/backfills/{id}")).ToArray<Task>();
        try { Task.WaitAll(deletes, TimeSpan.FromSeconds(5)); } catch (Exception) { }
        open.Clear();
    }

    int Creating(string key) => creating.TryGetValue(key, out int n) ? n : 0;
    int Have(string key) => open.Values.Count(k => k == key) + Creating(key);

    void Create(string key, List<JsonObject> tickets)
    {
        creating[key] = Creating(key) + 1;
        var body = new JsonObject
        {
            ["profile"] = profile,
            ["attributes"] = new JsonObject { ["assignment"] = assignment.DeepClone() },
            // the new player is checked against the profile's rules (latency) with these real players' tickets
            ["tickets"] = new JsonObject(tickets.Take(MaxTicketsPerBackfill)
                .Select(t => KeyValuePair.Create(t["id"].GetValue<string>(), (JsonNode)t.DeepClone()))),
        };
        Send(HttpMethod.Post, "/backfills", body, (status, json) =>
        {
            creating[key] = Creating(key) - 1;
            string id = json?["id"]?.GetValue<string>();
            if ((status == HttpStatusCode.OK || status == HttpStatusCode.Created) && id != null)
            {
                open[id] = key;
                Console.WriteLine($"backfill: opened {id} for {key}");
            }
            else
            {
                nextCreate = DateTime.UtcNow.AddSeconds(RetrySeconds);
                Console.Error.WriteLine($"backfill: create failed, HTTP {(int)status} {json?.ToJsonString()}");
            }
        });
    }

    void Poll(string id)
    {
        busy.Add(id);
        Send(HttpMethod.Get, "/backfills/" + id, null, (status, json) =>
        {
            busy.Remove(id);
            if (!open.TryGetValue(id, out string key)) return;
            if (status == HttpStatusCode.NotFound)
            {
                open.Remove(id); // expired after the profile's ticket_expiration_period: a new one opens
                return;
            }
            string state = json?["status"]?.GetValue<string>() ?? "";
            if (state == "ASSIGNED" && json?["assigned_ticket"] is JsonObject assigned)
            {
                open.Remove(id);
                string ticketId = assigned["id"]?.GetValue<string>();
                if (ticketId == null) return;
                Console.WriteLine($"backfill: {id} seated ticket {ticketId} ({key})");
                OnTicketAssigned?.Invoke(key, ticketId, Ticket(assigned));
            }
            else if (state.Contains("CANCEL") || state.Contains("EXPIRE"))
            {
                open.Remove(id);
            }
        });
    }

    void Delete(string id)
    {
        busy.Add(id);
        Send(HttpMethod.Delete, "/backfills/" + id, null, (status, json) =>
        {
            busy.Remove(id);
            open.Remove(id);
        });
    }

    void Send(HttpMethod method, string path, JsonObject body, Action<HttpStatusCode, JsonNode> done)
    {
        Task.Run(async () =>
        {
            HttpStatusCode status = 0;
            JsonNode json = null;
            try
            {
                using var request = new HttpRequestMessage(method, url + path);
                if (body != null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
                using HttpResponseMessage response = await http.SendAsync(request);
                status = response.StatusCode;
                json = Parse(await response.Content.ReadAsStringAsync());
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"backfill: {method} {path} failed: {e.Message}");
            }
            results.Enqueue(() => done(status, json));
        });
    }

    /// <summary>The ticket fields a backfill request takes.</summary>
    static JsonObject Ticket(JsonObject t)
    {
        var ticket = new JsonObject
        {
            ["id"] = t["id"]?.DeepClone(),
            ["created_at"] = t["created_at"]?.DeepClone(),
            ["player_ip"] = t["player_ip"]?.DeepClone(),
            ["group_id"] = t["group_id"]?.DeepClone(),
            ["attributes"] = t["attributes"]?.DeepClone() ?? new JsonObject(),
        };
        if (t["team_id"] != null) ticket["team_id"] = t["team_id"].DeepClone();
        return ticket;
    }

    static JsonNode Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonNode.Parse(json); }
        catch (Exception) { return null; }
    }
}
