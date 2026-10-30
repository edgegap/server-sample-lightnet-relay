# Tanks through the LightNet relay

Mirror's Tanks sample playing through the [LightNet relay](../relay-server) on **Mirror, FishNet, PurrNet or Netcode for GameObjects (NGO)**. One build holds all four; one setting picks the netcode. One player hosts a room and gets a 6-letter code, the other joins with the code. Neither game opens a port: both are clients of the relay.

Unity 6000.0.54f1. Comes from commit `01d82b3` of [edgegap/netcode-game-sample-unity-mirror-tanks](https://github.com/edgegap/netcode-game-sample-unity-mirror-tanks).

## Pick the netcode

Set the `netcode` field of `Assets/Tanks/Resources/TanksSettings.asset` (`Mirror`, `FishNet`, `PurrNet` or `NGO`). The same asset holds the relay address and port. Or:

- Editor menu **Tanks > Netcode > Mirror / FishNet / PurrNet / NGO**.
- In the game, before hosting or joining: the netcode buttons at the top of the HUD.
- Command line: `-netcode FishNet`.

Host and players must use the same netcode.

## Play

1. Start the relay (see the [main README](../README.md#run-it)), or use one deployed on Edgegap.
2. Press Play on `Assets/Tanks/Scenes/Boot.unity`, or build with **Tanks > Build Windows client**.
3. In the first game press **Host**: the room code appears in the HUD.
4. In the second game type the code and press **Join room**.

Command line: `-netcode NAME -relay ADDRESS -relayPort PORT [-relayUdpPort UDP]`, then `-host [-codeFile PATH]` or `-join CODE`. On Edgegap, pass the deployment's host and external port: `-relay <host>.pr.edgegap.net -relayPort <port>`.

## Layout

```
Assets/Tanks/
  Core/       netcode-free gameplay: TanksSettings, TankCore, ProjectileCore, TankSpawnPoint, TanksGame (HUD, command line, e2e test)
  Mirror/ FishNet/ PurrNet/ NGO/
              one assembly per netcode: <Netcode>RelayTransport, <Netcode>Tank, <Netcode>Projectile, <Netcode>Session, prefabs
  Scenes/     Boot (loads the selected netcode's scene), Tanks_Mirror, Tanks_FishNet, Tanks_PurrNet, Tanks_NGO
  Editor/     TanksSetup (generates the scenes and prefabs), TanksBuild
```

The netcode scenes and prefabs are generated from Mirror's example by **Tanks > Regenerate netcode scenes and prefabs**; run it again after changing them.

How the relay works, the automated test (`e2e.sh`), the Edgegap setup and the notes on fitting four netcodes in one project are in the [main README](../README.md).
