# LightNet relay + Mirror Tanks

A relay server built on [LightNet](https://github.com/edgegap/server-sample-unity-smart-relays) that runs in a Docker container (and so on Edgegap), and Mirror's Tanks sample playing through it. One player hosts a room and gets a 6-letter code; the other joins with the code. Neither game opens a port: both are clients of the relay.

| Folder | What it is |
|---|---|
| `LightNet/` | LightNet core as a Unity package (`com.lightnet.relay`), with fixes for running as a relay (see below), plus `Runtime/Relay/`: the relay protocol and `RelayPeer`, the netcode-agnostic client of the relay. |
| `relay-server/` | The relay: a .NET 8 console app built from the same LightNet sources, and its Dockerfile. |
| `tanks/` | [edgegap/netcode-game-sample-unity-mirror-tanks](https://github.com/edgegap/netcode-game-sample-unity-mirror-tanks) with `Assets/LightNetRelay/`: a Mirror transport for the relay and a bootstrap that switches the Tanks scene to it at runtime (no scene edits). |
| `e2e.sh` | Automated test: two Tanks clients through the relay. |

## How it works

```
 Tanks (Mirror host) ──TCP+UDP──►  LightNet relay (Docker)  ◄──TCP+UDP── Tanks (Mirror client)
   CreateRoom → code                 room CODE: host + clients             JoinRoom CODE
   ToPeer(id, data)  ───────────────► FromHost(data) ─────────────────────►
   ◄──────────────── FromPeer(id, data) ◄───────────────────────────────── ToHost(data)
```

- Reliable messages go over LightNet's TCP channel and unreliable ones over its UDP channel. The relay forwards each message on the channel it arrived on.
- The relay is not a game server: the hosting player's game runs the Mirror server, and the relay only forwards bytes between the host and each client. Clients do not talk to each other directly.
- When the host leaves, the room closes and the relay disconnects its clients.

## Run it

Requirements: Docker, .NET 8 SDK (only to build outside Docker), Unity 2021.3 (built with 2021.3.45f2).

```bash
docker build --platform linux/amd64 -f relay-server/Dockerfile -t lightnet-relay:dev .
docker run --rm -p 7777:7777/tcp -p 7777:7777/udp lightnet-relay:dev
```

Open `tanks/` in Unity, open `Assets/Mirror/Examples/Tanks/Scenes/Scene.unity`, or build with **LightNet Relay > Build Windows client**. In the first game press **Host (Server + Client)**: the room code appears under the HUD. In the second, type the code in **Room code**, keep the address (`localhost`, or the relay's address) and press **Client**.

Automated test, with the container running:

```bash
./e2e.sh 127.0.0.1 7777 7777
```

It passes when both games see two tanks, both received reliable and unreliable data through the relay, and the host saw the client's tank move.

Relay settings (environment variables): `RELAY_TCP_PORT` and `RELAY_UDP_PORT` (default 7777), `RELAY_MAX_PEERS` (default 256), `RELAY_IDLE_EXIT_SECONDS` (exit when no room exists for that long; 0 = never).

Game command line: `-relay ADDRESS -relayPort TCP -relayUdpPort UDP`, then `-host [-codeFile PATH]` or `-join CODE`; `-e2e -expectTanks N -timeout S` for tests.

## Changes to LightNet

Upstream LightNet assumed a server that can reach its clients directly. To run as a relay behind Docker or Edgegap port mapping:

- **UDP works through NAT.** Upstream, the server sends UDP to a port on the client (base port + n), which NAT and firewalls block. Now the client sends and receives on one UDP socket and sends its handle every second; the server answers to the address those datagrams come from.
- **No Unity dependency in the core.** `ASSERT`/`CLAMP` no longer call UnityEngine, so the same sources build the .NET relay. Before, `ASSERT(FreeUnreliablePorts.TryDequeue(...))` also skipped the dequeue in Unity release builds, where `Debug.Assert` calls are compiled out.
- **Runs on .NET 8.** `Thread.Abort` (not supported there) is guarded.
- **No busy loops.** Network threads sleep 1 ms between polls instead of spinning a core each.
- **Other fixes.** TCP no-delay is on, receive buffers are 64 KB (Mirror batches), a full server refuses connections instead of asserting, slots are freed on connection loss, a closing peer flushes its Disconnect, and the random packet-loss emulation is skipped when off (`System.Random` is not thread safe).

## On Edgegap

The app version needs one port: 7777 with protocol `TCP/UDP` (two separate 7777 TCP and 7777 UDP entries are rejected as duplicates). Edgegap maps both protocols to the same external port, so clients pass it once: `-relay <host>.pr.edgegap.net -relayPort <external port>`. Edgegap's readiness check opens a TCP connection at start, which shows in the relay log as a peer that connects and leaves.

Tested on Edgegap on 2026-10-29: two Tanks clients through a relay deployed in Newark (`./e2e.sh <host>.pr.edgegap.net <port>`), both passed. One deployment serves many rooms; a lobby or matchmaker can also deploy one relay per match near the players.

The `tanks/` project comes from commit `01d82b3` of edgegap/netcode-game-sample-unity-mirror-tanks.
