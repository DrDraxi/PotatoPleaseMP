# Potato Please Multiplayer

A co-op multiplayer mod for [Potato Please](https://store.steampowered.com/app/5266240/). Peel together with friends in one
shift: over Steam (invites, friends) or a direct IP connection.

Built for **Potato Please 1.0.4** (Steam build 26529021). Mod version **0.2.2**.

## What's shared

- Every player gets their own workstation. Peeling, the potato pile, sacks, loose potatoes, doors and the claw crane are in sync.
- Money, upgrades, the quota, quests, letters, story choices and the ending are shared. The host keeps the save.
- Machines, cleaning, the snowstorm, power switches (Main Switch, Spoon Catapult), the State telephone and
  Continue Playing after the ending all work in co-op.
- Your own single-player save is never touched while you play in someone else's shift.

## Install

Download a zip from [Releases](../../releases):

- **`PotatoPleaseMP-<version>.zip`**: everything included (BepInEx 6 + the mod). Unzip it into the game folder
  (Steam > Potato Please > Manage > Browse local files) so `winhttp.dll` sits next to `PotatoPlease.exe`.
- **`PotatoPleaseMP-<version>-mod-only.zip`**: just the mod, if you already have BepInEx 6 (bleeding edge be.791, IL2CPP,
  CoreCLR) installed. Unzip into the game folder.

The first start takes a minute or two while BepInEx prepares itself. The main menu then has a **MULTIPLAYER** entry.
Every player needs the same game version and the same mod version.

## Play

- **Host:** MULTIPLAYER > HOST SHIFT, then invite friends from the pause menu or the Steam overlay.
- **Join:** accept the Steam invite, or MULTIPLAYER > JOIN SHIFT.
- **Direct connection (LAN/VPN):** set `[Net] Transport = Direct` in `BepInEx/config/drdraxi.potatopleasemp.cfg`. The host
  opens TCP port 27250, clients set `HostAddress` to the host's IP.

## Building

The mod is a BepInEx 6 IL2CPP (CoreCLR, net6.0) plugin. It compiles against files that are not in this repo:

- BepInEx core DLLs in `.tools/bepinex/BepInEx/core` (from a BepInEx 6 be.791 IL2CPP build), or pass `-p:BepInExCore=<dir>`
- the game's interop assemblies in `lib/interop`: start the game once with BepInEx installed and copy `BepInEx/interop`
  from the game folder, or pass `-p:InteropDir=<dir>`

```
dotnet build src/PotatoPleaseMP/PotatoPleaseMP.csproj -c Release
```

`src/PotatoPleaseMP.Core` (wire protocol, sessions, transports) has no game dependencies, and its tests run with
`dotnet test tests/PotatoPleaseMP.Core.Tests`.

Unofficial fan mod, not affiliated with the game's developers.
