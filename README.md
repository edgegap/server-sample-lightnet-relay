# Small network relay with game sample using Mirror, FishNet, PurrNet or NGO

A small network relay built on [LightNet](https://github.com/edgegap/server-sample-unity-smart-relays) that runs in a Docker container (and so on Edgegap), and Mirror's Tanks sample playing through it on **Mirror, FishNet, PurrNet or Netcode for GameObjects (NGO)**: one setting picks the netcode. One player hosts a room and gets a 6-letter code; the other joins with the code. Neither game opens a port: both are clients of the relay.

| Folder | What it is |
|---|---|
| `LightNet/` | LightNet core as a Unity package (`com.lightnet.relay`), with fixes for running as a relay (see below), plus `Runtime/Relay/`: the relay protocol and `RelayPeer`, the netcode-agnostic client of the relay. |
| `relay-server/` | The relay: a .NET 8 console app built from the same LightNet sources, and its Dockerfile. |
| `tanks/` | Unity 6 project (6000.0.54f1) from [edgegap/netcode-game-sample-unity-mirror-tanks](https://github.com/edgegap/netcode-game-sample-unity-mirror-tanks) with `Assets/Tanks/`: the game on all four netcodes. |
| `e2e.sh` | Automated test: two Tanks clients through the relay, on each netcode. |
| `matchmaker-config.json`, `e2e-matchmaker.sh` | Optional [Edgegap matchmaker](#optional-edgegap-matchmaker) config, and its automated test. |

## Pick the netcode

The netcode is the `netcode` field of **`tanks/Assets/Tanks/Resources/TanksSettings.asset`** (`Mirror`, `FishNet`, `PurrNet` or `NGO`), which also holds the relay address and port. Three other ways to swap it:

- Editor menu **Tanks > Netcode > Mirror / FishNet / PurrNet / NGO**: sets the asset and opens that netcode's scene.
- In the game, before hosting or joining: the netcode buttons at the top of the HUD.
- Command line: `-netcode FishNet`.

Host and players must use the same netcode. One build contains all four.

| Netcode | Version | Relay transport | Tank sync |
|---|---|---|---|
| Mirror | 66.0.9 (in `Assets/Mirror`) | `MirrorRelayTransport` | `[SyncVar]`, `[Command]`, `[ClientRpc]`, client-authoritative `NetworkTransform` |
| FishNet | 4.7.4 (in `Assets/FishNet`) | `FishNetRelayTransport` | `SyncVar<int>`, `[ServerRpc]`, `[ObserversRpc]`, client-authoritative `NetworkTransform` |
| PurrNet | 1.24.1 (UPM, git) | `PurrNetRelayTransport` | `SyncVar<int>`, `[ServerRpc]`, `[ObserversRpc]`, owner-authoritative `NetworkTransform` |
| NGO | 1.13.0 (UPM) | `NgoRelayTransport` | `NetworkVariable<int>`, `[ServerRpc]`, `[ClientRpc]`, owner-authoritative `NetworkTransform` with unreliable deltas |

## How the project is laid out

```
Assets/Tanks/
  Core/       netcode-free: TanksSettings, TankCore (input, movement, turret, health bar, hits),
              ProjectileCore, TankSpawnPoint, ITanksSession, TanksGame (HUD, command line, e2e test)
  Mirror/ FishNet/ PurrNet/ NGO/
              per netcode, in its own assembly: <Netcode>RelayTransport, <Netcode>Tank,
              <Netcode>Projectile, <Netcode>Session, Prefabs/Tank + Projectile
  Scenes/     Boot (loads the selected netcode's scene), Tanks_Mirror, Tanks_FishNet, Tanks_PurrNet, Tanks_NGO
  Editor/     TanksSetup (generates the scenes and prefabs), TanksBuild
```

The gameplay is Mirror's Tanks example split in two: `TankCore` has no netcode, and the netcode component next to it (`ITankNet`) syncs health, fire and despawn. The four scenes and prefab sets are generated from Mirror's example scene and prefabs by **Tanks > Regenerate netcode scenes and prefabs**; run it again after changing them.

The four relay transports all wrap `RelayPeer`. On the host, the netcode's own client never goes through the relay: NGO and Mirror handle it themselves, and the FishNet and PurrNet transports loop it back in-process.

## How the relay works

```
 Tanks (host: server + client) ──TCP+UDP──►  LightNet relay (Docker)  ◄──TCP+UDP── Tanks (client)
   CreateRoom → code                           room CODE: host + clients            JoinRoom CODE
   ToPeer(id, data)  ─────────────────────────► FromHost(data) ─────────────────────►
   ◄──────────────────────── FromPeer(id, data) ◄──────────────────────────────────── ToHost(data)
```

- Reliable messages go over LightNet's TCP channel and unreliable ones over its UDP channel. The relay forwards each message on the channel it arrived on.
- The relay is not a game server: the hosting player's game runs the netcode's server, and the relay only forwards bytes between the host and each client. The relay doesn't know or care which netcode is used.
- When the host leaves, the room closes and the relay disconnects its clients.
- Limits per message: 1,177 bytes unreliable, 16 KB reliable. The transports report these as their MTU.

## Run it

Requirements: Docker, Unity 6000.0.54f1, .NET 8 SDK (only to build the relay outside Docker).

```bash
docker build --platform linux/amd64 -f relay-server/Dockerfile -t lightnet-relay:dev .
docker run --rm -p 7777:7777/tcp -p 7777:7777/udp lightnet-relay:dev
```

Open `tanks/` in Unity, pick a netcode (above) and press Play on `Assets/Tanks/Scenes/Boot.unity` (or open a `Tanks_<Netcode>` scene directly), or build with **Tanks > Build Windows client**. In the first game press **Host**: the room code appears in the HUD. In the second, type the code and press **Join room**.

Automated test: it runs on Windows (from Git Bash) and needs the Windows client build first, made in Unity with **Tanks > Build Windows client** (`tanks/Builds/Windows/Tanks.exe`, not in the repository). With the relay running, test all four netcodes or name some:

```bash
./e2e.sh 127.0.0.1 7777
./e2e.sh 127.0.0.1 7777 FishNet NGO
```

For each netcode it starts a host and a client. The client drives in circles and fires every second. It passes when both games see two tanks, both received reliable and unreliable data through the relay, both saw a projectile spawn, and the host saw the client's tank move.

Relay settings (environment variables): `RELAY_TCP_PORT` and `RELAY_UDP_PORT` (default 7777), `RELAY_MAX_PEERS` (default 256), `RELAY_IDLE_EXIT_SECONDS` (exit when no room exists for that long, default 600, so an idle deployment on Edgegap stops; 0 = never).

Game command line: `-netcode NAME -relay ADDRESS -relayPort PORT [-relayUdpPort UDP]`, then `-host [-codeFile PATH]` or `-join CODE`; for tests, `-e2e -expectTanks N -timeout S` and `-switchFrom NAME` (start on another netcode and switch at runtime).

## Optional: Edgegap matchmaker

Off by default. With it on, players press **Find Match** instead of passing room codes around: the [Edgegap matchmaker](https://docs.edgegap.com/learn/matchmaking) groups 2 to 4 players and starts a relay on Edgegap for their match, near them. Each player then asks that relay for its role with its matchmaking ticket ID: the first one hosts the match room, the others join it, and each netcode starts exactly as with a hand-made room. Hosting and joining by code still work next to it.

```
 Find Match ──ticket──► Edgegap matchmaker ──match──► deploys a relay (MM_TICKET_IDS = the matched tickets)
 ◄──── HOST_ASSIGNED: relay IP + port ────┘
 MatchRole(ticket) ──► relay: first ticket → host + room code, others → join + room code (unknown tickets refused)
 host: CreateRoom(code) / others: JoinRoom(code), held until the host's room exists
```

The relay switches to match mode by itself when it finds `MM_TICKET_IDS`, which only the matchmaker injects. In match mode it hosts only that match's room, exits when the host leaves (or after `RELAY_IDLE_EXIT_SECONDS`, default 60, if nobody created the room), and stops its own Edgegap deployment (`ARBITRIUM_DELETE_URL`) so an ended match costs nothing. Any other relay works as before.

Set it up:

1. Push the relay image and create an app version as in [On Edgegap](#on-edgegap), with the port named `relay`.
2. In the Edgegap dashboard, **Matchmaker > Create Matchmaker**: upload [`matchmaker-config.json`](matchmaker-config.json) (profile `casual`, 2 to 4 players, 30 s ticket expiration) after setting `application.name` and `version` to your app version, then **Create and Start**.
3. In `tanks/Assets/Tanks/Resources/TanksSettings.asset`, turn on `useMatchmaker` and paste the matchmaker's **API URL** and **Auth Token** (the token is safe to ship in a game: it gives no access to the Edgegap account). Or, on the command line: `-matchmaker URL -matchmakerToken TOKEN`, and `-findMatch` to search at start.

The client uses the [Edgegap Unity SDK](https://github.com/edgegap/sdk-unity-sessions-matchmaker-server-browser) (`com.edgegap.unity-sdk`); the code is in `Assets/Tanks/Core/TanksMatchmaker.cs`.

Automated test: two clients press Find Match and play one match through the relay the matchmaker started, on each netcode in turn (Windows, Git Bash, Windows client build needed as above):

```bash
./e2e-matchmaker.sh https://<id>.edgegap.net <matchmaker token>
./e2e-matchmaker.sh https://<id>.edgegap.net <matchmaker token> FishNet NGO
```

## Changes to LightNet

Upstream LightNet assumed a server that can reach its clients directly. To run as a relay behind Docker or Edgegap port mapping:

- **UDP works through NAT.** Upstream, the server sends UDP to a port on the client (base port + n), which NAT and firewalls block. Now the client sends and receives on one UDP socket and sends its handle every second; the server answers to the address those datagrams come from.
- **No Unity dependency in the core.** `ASSERT`/`CLAMP` no longer call UnityEngine, so the same sources build the .NET relay. Before, `ASSERT(FreeUnreliablePorts.TryDequeue(...))` also skipped the dequeue in Unity release builds, where `Debug.Assert` calls are compiled out.
- **Runs on .NET 8.** `Thread.Abort` (not supported there) is guarded.
- **No busy loops.** Network threads sleep 1 ms between polls instead of spinning a core each.
- **Other fixes.** TCP no-delay is on, receive buffers are 64 KB, a full server refuses connections instead of asserting, slots are freed on connection loss, a closing peer flushes its Disconnect, and the random packet-loss emulation is skipped when off (`System.Random` is not thread safe).

## Notes on having four netcodes in one project

- PurrNet needs Unity 2022.3 or newer, so the project moved from 2021.3 to Unity 6000.0.54f1.
- Mirror's `SimpleWebTransport` (WebSocket transport, unused here) was removed: PurrNet ships an assembly with the same name.
- FishNet is in `Assets/FishNet` rather than installed as a package: some of its code-generation files share GUIDs with NGO's, which Unity can only fix outside a read-only package. Its Yak transport, which doesn't compile at tag 4.7.4, was left out (see `Assets/FishNet/VERSION.txt`).
- `com.unity.transport` is pinned to 2.6.0, which PurrNet's optional UTP transport needs.
- Each netcode's code is in its own assembly that references only that netcode, so each code generator only processes its own code.

## On Edgegap

The app version needs one port: 7777 with protocol `TCP/UDP` (two separate 7777 TCP and 7777 UDP entries are rejected as duplicates). Edgegap maps both protocols to the same external port, so clients pass it once: `-relay <host>.pr.edgegap.net -relayPort <external port>`. Edgegap's readiness check opens a TCP connection at start, which shows in the relay log as a peer that connects and leaves.

Tested on Edgegap on 2026-10-29: `./e2e.sh <host>.pr.edgegap.net <port>` through a relay deployed in Newark passed on Mirror, FishNet, PurrNet and NGO. One deployment serves many rooms; a lobby or matchmaker can also deploy one relay per match near the players.

The `tanks/` project comes from commit `01d82b3` of edgegap/netcode-game-sample-unity-mirror-tanks.

## License

Apache License 2.0 (see [LICENSE](LICENSE)), the license of LightNet. Third-party code keeps its own license: Mirror (MIT, `tanks/Assets/Mirror`), FishNet (`tanks/Assets/FishNet/LICENSE.txt`) and ParrelSync (`tanks/Assets/ParrelSync/LICENSE.md`) in the project; PurrNet and NGO are installed as packages under theirs.
