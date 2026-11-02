using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

// Join in progress for a match relay: while the match room has free seats, keep backfills open on the Edgegap
// matchmaker. A backfill is a relay-owned ticket that carries this deployment's address; when the matchmaker puts a
// player's ticket in it, that player gets HOST_ASSIGNED with this relay, asks for its role (MatchRole) and joins the
// running match like the others. Same loop as the verified Megacity Metro sample (EdgegapBackfillSystem.cs).
//
// Needs MATCHMAKER_URL and MATCHMAKER_TOKEN (the matchmaker's client-safe Auth Token) on the app version, and the
// variables Edgegap injects into matchmaker deployments (MM_*, ARBITRIUM_*). Runs on the relay's main loop: HTTP calls
// run in the background and their results are applied in Update.

class MatchBackfill
{
    const int MaxOpen = 2;              // backfills open at once; each seats one player
    const double PollSeconds = 2;
    const double RetrySeconds = 5;      // after a failed create (429: rate limited)

    /// <summary>A player's ticket was put in one of our backfills: it is now part of the match.</summary>
    public Action<string> OnTicketAssigned;

    readonly HttpClient http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
    readonly string url, profile;
    readonly JsonObject assignment;
    readonly Dictionary<string, JsonObject> tickets = new Dictionary<string, JsonObject>(); // real players' tickets, sent with each backfill
    readonly HashSet<string> open = new HashSet<string>();
    readonly HashSet<string> busy = new HashSet<string>();  // backfills with a poll or delete in flight
    readonly ConcurrentQueue<Action> results = new ConcurrentQueue<Action>();
    int creating;
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
    public static MatchBackfill FromEnvironment(IEnumerable<string> ticketIds)
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

        var backfill = new MatchBackfill(url, token, profile, assignment);
        foreach (string id in ticketIds)
        {
            if (Parse(Environment.GetEnvironmentVariable("MM_TICKET_" + id)) is JsonObject ticket) backfill.tickets[id] = Ticket(ticket);
        }
        if (backfill.tickets.Count == 0)
        {
            Console.WriteLine("backfill: off (no MM_TICKET_<id> to send with backfills)");
            return null;
        }
        Console.WriteLine($"backfill: on, matchmaker {backfill.url}, profile {profile}");
        return backfill;
    }

    /// <summary>Call every loop iteration; <paramref name="countFreeSeats"/> counts the seats the match can still give out.</summary>
    public void Update(Func<int> countFreeSeats)
    {
        while (results.TryDequeue(out Action apply)) apply();
        if (closing) return;
        int freeSeats = countFreeSeats(); // after the results: a seated player takes a seat
        DateTime now = DateTime.UtcNow;

        int target = Math.Clamp(freeSeats, 0, MaxOpen);
        if (open.Count + creating < target && now >= nextCreate) Create();
        else if (open.Count > target && creating == 0)
        {
            string extra = open.FirstOrDefault(id => !busy.Contains(id));
            if (extra != null) Delete(extra);
        }

        if (now >= nextPoll)
        {
            nextPoll = now.AddSeconds(PollSeconds);
            foreach (string id in open.Where(id => !busy.Contains(id)).ToList()) Poll(id);
        }

        string summary = $"{freeSeats} free seat(s), {open.Count} backfill(s) open";
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
            if (creating > 0) System.Threading.Thread.Sleep(50);
        } while (creating > 0 && DateTime.UtcNow < deadline);
        if (open.Count == 0) return;
        Console.WriteLine($"backfill: closing {open.Count} backfill(s)");
        Task[] deletes = open.Select(id => http.DeleteAsync($"{url}/backfills/{id}")).ToArray<Task>();
        try { Task.WaitAll(deletes, TimeSpan.FromSeconds(5)); } catch (Exception) { }
        open.Clear();
    }

    void Create()
    {
        creating++;
        var body = new JsonObject
        {
            ["profile"] = profile,
            ["attributes"] = new JsonObject { ["assignment"] = assignment.DeepClone() },
            // the new player is checked against the profile's rules with these real players' tickets
            ["tickets"] = new JsonObject(tickets.Take(8).Select(t => KeyValuePair.Create(t.Key, (JsonNode)t.Value.DeepClone()))),
        };
        Send(HttpMethod.Post, "/backfills", body, (status, json) =>
        {
            creating--;
            string id = json?["id"]?.GetValue<string>();
            if ((status == HttpStatusCode.OK || status == HttpStatusCode.Created) && id != null)
            {
                open.Add(id);
                Console.WriteLine($"backfill: opened {id}");
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
                tickets[ticketId] = Ticket(assigned);
                Console.WriteLine($"backfill: {id} seated ticket {ticketId}");
                OnTicketAssigned?.Invoke(ticketId);
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
