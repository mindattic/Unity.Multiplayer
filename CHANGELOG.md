# Changelog

All notable changes to Unity.Multiplayer are documented here. The package follows
[HOUSE-LAW-1](https://github.com/mindattic/MindAttic/blob/main/MindAttic.HouseRules.md#house-law-1--whole-number-versioning)
whole-number versioning (`1`, `2`, `3` — never `1.1.0`); `unity/com.mindattic.unity.multiplayer/package.json`
carries the package's own version.

## [Unreleased]

### Added

- The Multiplayer-ready standard (UM-T74): [docs/articles/multiplayer-ready.md](docs/articles/multiplayer-ready.md)
  holds the eight rules every MindAttic Unity game follows so co-op or PvP can be added without re-architecting
  (commands stamped with a player id through one pipeline, one authority apart from presentation, no global player,
  deterministic rules, stable ids, serializable state and commands, an `ISession` seam with a `LocalSession`, a
  two-player test), a checklist, the law text games cite from their BIBLE, a rule-by-rule map onto the kit, and
  Unity.Cursory as the ported-after-the-fact counter-example.
- Unity Relay join codes set up end to end for a home host with no port forwarding (UM-T71).
  - **Engine-free check.** `RelaySetup` (Core) checks whether the package is installed and the project linked to a
    Unity Cloud project (`RelayReadiness`). It holds the setup sentences and turns raw Unity Services failures into
    ones that say what to fix: not linked, Relay off, a missing environment, offline.
  - **`RelayJoinCodeService`.** Adds `Readiness`, `IsInstalled`, an `Environment` (Unity Services environment,
    `production` by default) and a shared anonymous `SignInAsync()`. A project that isn't linked fails before
    anything is sent, with `RelaySetup.NotLinkedMessage`. Other failures become a `RelaySetupException`.
  - **NetBootstrap.** A **Unity Relay** section: **Relay Environment**, **Relay Region** and **Sign In At Startup**,
    which signs in as the scene starts.
  - **Front end.** Host and Join by code show the explained sentence in the menu, as a toast or a dialog.
  - **Setup.** **MindAttic ▸ Multiplayer ▸ New Game…** gains step 3, which adds the package, opens Services settings
    or the dashboard, and shows the link. **MindAttic ▸ Multiplayer ▸ Check Unity Relay Setup** and
    `RelaySetupCheck.CheckBatch` do the same check in the editor and in batch mode. `tools/link-unity-cloud.ps1` links
    a project to a Cloud project and adds the package with the editor closed.
  - **Docs.** [Join codes](docs/articles/join-codes.md#unity-relay-for-a-home-host-no-port-forwarding).

### Fixed

- A project that adds only the package line compiles and runs batch mode again (UM-T67). The test kit
  (`Unity.Multiplayer.Testing`) builds on NGO's test helpers, which only compile for a testable NGO, and its define
  constraint let it compile in every editor. It now also requires `MINDATTIC_NGO_TESTABLE`, which the editor script
  `TestKitDefine` sets while `Packages/manifest.json` lists NGO under `"testables"` (and removes otherwise). Without the
  testables line the kit is absent; with it, it appears on the next domain reload. The game template's test assembly
  carries the same constraint.
- A clean checkout of a game builds again (UM-T72). `MINDATTIC_NGO_TESTABLE` is set by an editor script, which only runs
  after a successful compile, so a game test asmdef that referenced the kit without the constraint failed the first
  compile (CS0234) and a batch build aborted; setting the define then also rewrote `ProjectSettings.asset`. `TestKitDefine`
  now adds the constraint to any asmdef under `Assets` that references the kit without it, and logs that
  `ProjectSettings/ProjectSettings.asset` must be committed whenever it changes the define; New Game says the same.
  `tests/run-new-game.ps1` batch-builds a clean copy of the generated game and fails if a committed file changes.

## [2.0.0] — 2026-10-21

The one-stop shop release. With MindAttic Components and Unity.Shared, the package is now one kit for a whole
multiplayer game: menus, the roster, votes, keyed objects, bots, a test kit and `mp-host game` all landed since 1.0.0,
and **MindAttic ▸ Multiplayer ▸ New Game…** puts every one of them into a fresh project as a working game. Upgrading
from 1.0.0 needs no code changes; the major version follows
[HOUSE-LAW-1](https://github.com/mindattic/MindAttic/blob/main/MindAttic.HouseRules.md#house-law-1--whole-number-versioning).
See [Getting started](docs/articles/getting-started.md), [The kit](docs/articles/kit.md) and
[Case study: Unity.Cursory](docs/articles/case-study-cursory.md).

### Added

- **New Game wizard and game template** (UM-T59) — **MindAttic ▸ Multiplayer ▸ New Game…** checks
  `Packages/manifest.json` and lists the exact lines the template needs: MindAttic Components, Unity.Shared (optional),
  the Unity Test Framework, and NGO under `testables`. The `file:` paths point at the repos beside Unity.Multiplayer,
  relative where possible. **Add missing lines** writes them, and never writes a path that does not exist. The wizard
  then copies the new `Samples~/GameTemplate` into `Assets/<Name>`, with the game's own namespace and assembly names and
  fresh asset GUIDs. It also puts the scene first in Build Settings, turns on Run In Background, and writes
  `mp-host.json` (server scene, smoke bots and scenario) and `.github/workflows/nightly.yml` (one `uses:` of
  `game-smoke.yml`). The template contains:
  - a scene with `NetBootstrap`, `NetFrontEnd` and `NetVotes`;
  - a `Player` `NetEntity` with a `[ToServer] Move` and a `[Synced]` position, drawn in its `NetRoster` name and colour;
  - crates spawned by key through `NetSession.Objects`;
  - a restart `VoteKind`;
  - a `[NetBotDriver]` `GameBot`;
  - EditMode tests (scene wiring, the prefab validator) and PlayMode tests on `NetSessionTest` and `NetBotTest`.

  `NewGame.CreateFromCommandLine` (`-addPackages`, `-checkOnly`, `-gameName`) runs the wizard in batch mode, and
  `NewGameManifest` is its plain-text manifest logic. `tests/run-new-game.ps1` proves it end to end: it creates a
  brand-new project in `%TEMP%`, runs the wizard as a game developer would, and runs the generated game's own suites
  (`-Smoke` adds `mp-host game smoke`). The template is also listed as a Package Manager sample (**Game Template**).
  New docs: [Getting started](docs/articles/getting-started.md), rewritten for a brand-new game;
  [The kit](docs/articles/kit.md); and [Case study: Unity.Cursory](docs/articles/case-study-cursory.md).
- **Networked objects by key** (UM-T60) — a stable, game-given name for any networked object, so clients never match
  objects by spawn order. Put a `NetKey` on a network prefab's root and spawn instances on the server with
  `NetSession.Objects.Spawn(prefab, key, position, rotation, parent, ownerClientId)`; every peer finds them in
  `NetSession.Objects` (`NetRegistry`, also `NetRegistry.Current`) with `TryGet<T>(key, out T)` (the `NetKey` or any
  component on the object), `TryGet(key, out GameObject)`, `Get<T>(key)`, `Contains`, `Keys`, `All`, and hears of them
  through `Spawned` / `Despawned`; `Despawn(key)` on the server frees the key at once. The key rides in the object's
  spawn (NGO's per-behaviour synchronization data), so it is there before any `OnSpawned`, for late joiners too, and it
  cannot change while the object is spawned. A key in use is refused by `Spawn` with an `ArgumentException` naming the
  key and its holder; an object spawned another way with a missing or duplicate key is logged as an error on every
  peer and left out. Core (engine-free) gets `NetKeys` (validation and messages) and `NetKeyTable<T>`. See
  [Networked objects by key](docs/articles/objects.md).
- **Bots and end-to-end smoke tests** (UM-T57, promoted from Unity.Cursory's UC-T14). `-bot <role>` makes any game a
  headless bot client. Core's `NetLaunch` never starts it by itself or gives it menus, and `NetBots` joins by `-joincode`,
  by `-joincodefile` (waiting for the file) or by `-address`. It then hands the connected session to the game's
  `IBotDriver`, which is either a class marked `[NetBotDriver]` or a component in the scene. The driver gets a `NetBot`
  with the role, the `-scenario`, `Pass`/`Fail`, and waits that resume on the main thread (`NextFrame`, `Seconds`,
  `Until`, `Spawned<T>`, `LocalPlayer<T>`). The verdict is the log line `[NetBot:<role>] OK:` or `FAIL:`; a failed
  player quits with exit code 1. A server launched with `-joincodefile` writes each join code it publishes there,
  atomically. `NetBotRunner` runs one bot on any session. Core gets `BotCommandLine` (`-bot`, `-joincode`,
  `-joincodefile`, `-scenario`, `-bottimeout`), `JoinCodeFile` and `BotLog`.
  `Unity.Multiplayer.Testing.NetBotTest` runs a dedicated server and N bots in one PlayMode test.
  `Unity.Multiplayer.Editor.BotPlayerBuild.BuildFromCommandLine` builds the one Linux player that plays every role.
  `mp-host game smoke` builds it and writes the stack to `Builds/Smoke`: a Dockerfile with `SDL_VIDEODRIVER=dummy` and
  a compose file holding the rendezvous service, the server and N bots. It runs the stack and exits 0 once every bot
  logs OK. `.github/workflows/game-smoke.yml` is the reusable nightly a game adopts with one job. The generated server
  package Dockerfile also sets `SDL_VIDEODRIVER=dummy`. See [Bots](docs/articles/bots.md).
- **Votes kit** (UM-T54) — server-authoritative votes any game can offer, promoted from Unity.Cursory. Core (engine-free)
  gets `VoteBox` (one vote at a time over a roster of client ids; passes when Yes reaches the quorum, rejected as soon
  as it cannot, lapses on the `ITickClock`; a leaver is dropped and the quorum recomputed), `VoteRules` (quorum fraction,
  default 2/3 rounded up; timeout, default 15 s; optional per-starter cooldown; who may start; the starter's own Yes),
  `VoteKind` (id, question and description worded from the request, `Check`, `MayVote`, the server-side `Passed`
  effect, per-kind quorum and time), `VoteRequest` (an optional `long` and `string` payload), `VoteStatus`,
  `VoteResult` and `VoteText` (the overlay's words). The adapter gets `VoteBoard`, a `NetEntity` spawned from the
  package's own prefab (`RegisterPrefab`, `Spawn`, `AddKind`, `StartVote`, `CastVote`, `Status`, `LocalChoice`,
  `VoteChanged`, `VoteEnded`, `VoteRefused`) with a built-in kick vote through `NetSession.Kick`
  (`AddKickKind`, `StartKickVote`), and `NetVotes`, the one component that sets it all up beside a `NetBootstrap`. The
  front end gets `VoteOverlay` (question, tally, quorum bar, Yes/No, result toasts), wired by itself when a `NetFrontEnd`
  has menus. See [Votes](docs/articles/votes.md).
- **Player roster and identity** (UM-T55) — `NetSession.Roster` (`NetRoster`, also `NetRoster.Current`) is who is in
  the game, on every peer: one `NetPlayer` per seat with `Seat`, `ClientId`, `DisplayName`, `Color`, `IsHost`,
  `IsConnected` (false while a dropped player's seat is held), `PingMs` and `IsLocal`; `All`, `Local`,
  `TryGet(clientId)`, `TryGetBySeat(seat)`, and `Joined` / `Left` / `Changed` (`NetPlayerChange` says what changed).
  The server fills it from the connection approval payload (names cleaned, an unpicked colour resolved to the seat's
  palette colour), sends each connection the whole roster once it is in (late joiners and reconnects too; a reconnect
  keeps seat and identity) and then only changes, over named messages, and measures every client's round trip once a
  second. `NetRoster.RequestChange(name, colour)` changes a player's name or colour at runtime: the server checks it
  (`PlayerNames.Validate`, `PlayerColors.Validate`, 5 at once then 1 a second) and answers with a
  `PlayerChangeResult`; an accepted change reaches every peer and is kept with the seat and in `NetSession.Identity`.
  `NetFrontEnd`'s lobby shows the roster, its Settings screen renames mid-game through the server, and an accepted
  change of the local player is saved back to its `IPlayerIdentitySource` (the MindAttic profile by default). Core
  gains `NetPlayer`, `PlayerNames`, `PlayerColors`, `PlayerRoster`, `RosterUpdate`, `RosterWire`,
  `PlayerChangeRequest` and `SeatRegistry.Rename`; the adapter `PlayerColorExtensions` (`ToColor32`, `ToPlayerColor`,
  `UnityColor`). See [Players, names and colours](docs/articles/players.md).
- **Front-end test kit** (UM-T62) — `Unity.Multiplayer.UI.Testing` (editor and test builds only, and only with
  MindAttic Components) lets a game's own PlayMode tests drive its `NetFrontEnd`: `FrontEndTesting.ForceMenus` builds
  the menus in a batch-mode run, `Bind`, `MainScreen`/`LobbyScreen`/`LobbyCode`/`LobbyRoster`/`BusyDialog`/
  `MessageDialog`, `Click`/`Buttons`/`HasButton`, `CleanUpUi`, and `FixedIdentitySource` (an in-memory name and colour).
  Before it, the menus override and `Bind` were internal to the package's own tests, so a game got no menus to test.
- **`mp-host game`** (UM-T58) — deploy any Unity.Multiplayer game with no per-game script:
  `mp-host game local|down|deploy|update|status|logs|destroy [--project <dir>] [--profile <p>] [--scene <path>] [--dry-run]`
  finds the editor from `ProjectVersion.txt` (Unity Hub's folders, editor list and install path, or `--unity` /
  `UNITY_EDITOR`), picks the server scene (`--scene`, `mp-host.json`, else the first Build Settings scene), defaults
  name/image/address/rendezvous from the project and vault, remembers deployed server ids, and runs the existing
  pipeline. `ServerPackageBuild.BuildFromCommandLine` takes `-serverScene` (else opens the first Build Settings scene
  in a fresh batch editor). `mp-host` packs as the `MindAttic.Unity.Multiplayer.Hosting.Cli` .NET tool
  (`tools/install-mp-host.ps1`). `mp-host local up --dry-run` no longer launches Unity, and every profile builds the
  Unity package profile it runs (`GenericVps` for the VPS/cloud providers and Local).
- **Drop-in front end** (UM-T48) — `NetFrontEnd`, in the new optional `Unity.Multiplayer.UI` assembly (compiled only
  when `com.mindattic.components.unity` is installed), gives any game its multiplayer menus from one component:
  a main menu (Host, Join by IP, Join by code, Settings with name and colour, Quit), a host lobby with the grouped
  join code, a Copy button and a live roster with colour swatches and remove, a cancellable connecting dialog, a
  plain-words dialog for every disconnect reason, an automatic reconnect to the same seat with progress, and an
  in-game Esc menu (Resume, Invite players, Settings, Leave). The player's name and colour come from an
  `IPlayerIdentitySource` (the MindAttic profile through `com.mindattic.unity.shared`, else PlayerPrefs) and go in
  the connection approval payload. Hooks: `SessionStarted`, `GameStarted`, `Left`, `Reconnected`, `StateChanged`,
  `AddScreen`, `AddMainMenuItems`, `AddPauseMenuItems`, `AddSettingsItems`, `MenusVisible`; every button is also a
  method. A headless or `-mode` launch never creates UI. Optional `NetFrontEndSettings` asset. **MindAttic ▸
  Multiplayer ▸ Add Front End** networks the scene and adds it, or names the exact manifest lines that are missing.
  Core gains `DisconnectText`, `ReconnectPolicy`, `HostAddress` and `NetLaunch.ShowsMenus`. New **Front End**
  sample; see [Drop-in front end](docs/articles/front-end.md).
- **Test kit: the `Unity.Multiplayer.Testing` assembly** (UM-T56) — what a game references from its own PlayMode
  test assembly to test its networking over a real connection. `NetSessionTest(Topology.Host | Server, clients,
  transport)` is a fixture (on NGO's `NetcodeIntegrationTest`) with a host or dedicated server and N client
  `NetSession`s on a free port, `StartServer` / `Join` / `StartAndJoinAll`, `CreatePrefab<T>` / `RegisterPrefab` /
  `SetPlayerPrefab` / `Spawn<T>`, and waits that fail saying what never happened: `WaitUntil`, `WaitForSpawn<T>`,
  `WaitForCopy<T>`, `WaitForSynced<T>`, `WaitFor(Task)`. Also usable alone: `FreePort.Udp()` / `Tcp()` / `For()` /
  `Assign()` (a port free right now, instead of a fixed one that collides with a parallel run),
  `TestSessions.Create(NetworkManager)` (a `NetSession` without reflecting into its internal constructor), `NetAwait`
  (the same waits as statics, plus `UntilAsync` / `ForSpawnAsync` / `ForSyncedAsync`), and
  `InProcessRendezvousServer` (the rendezvous API served in-process for join-by-code tests, now public, with
  `ForServer` / `ForClient` config helpers and a configurable secret). Its define constraint, `UNITY_EDITOR ||
  UNITY_INCLUDE_TESTS`, keeps it out of player builds; a game lists NGO, not this package, under `testables`. The package's own PlayMode
  suites and the Minimal sample's tests now run on it, each on a free port. See
  [Testing](docs/articles/testing.md#the-test-kit).

- **`NetBootstrap.Configure`** (UM-T33) — sets the bootstrap's default `NetConfig` in code (topology, address,
  port, transport, tick rate, max players, game version, join codes, conditions, …) before the session starts,
  instead of reflecting into its private serialized fields. `Configure(NetConfig)` copies and validates the
  config; `Configure(Action<NetConfig>)` edits a copy of the current defaults; both are refused while the
  session runs. `DefaultConfig` reads the defaults back. The environment and command line still override them.

### Fixed

- **`mp-host game status|logs|update|destroy` look at the profile the project last deployed with** (UM-T61) — they used
  to default to GenericVps, so after `mp-host game local` a bare `game status` or `game logs` looked for a VPS that
  doesn't exist. The deployment store now remembers each project folder's last profile (`game deploy`, `game local`,
  `game update`) beside the remembered server ids and these verbs default to it; `--profile` still wins, and
  `game deploy` itself still takes `mp-host.json`'s profile, else GenericVps.
- **A host with the default Join Codes uses a configured rendezvous URL** (UM-T63) — with `JoinCodes` left at `Auto`,
  `NetFrontEnd`'s Host and `NetSession.HostWithCode` tried only Unity Relay, so a game with a rendezvous URL but no
  Relay hosted with no code. `Auto` for a listen server is now Relay when it is installed, then the rendezvous service
  when a URL is set (`JoinCodeDefaults.HostingOrder` / `ForHosting`); the front end tries each in turn before hosting
  for joining by IP, and `PublishCode` on a running host uses the URL.
- **In-game menu items run once** (UM-T64) — `NetFrontEnd.AddPauseMenuItems` callbacks ran again on every refill of the
  in-game menu (each game entered, each join code published) on the same screen, so a `Shown` handler they hooked
  piled up one more each time. The in-game menu is now built once: each callback runs exactly once, its items stay,
  and only the built-in parts (subtitle, Invite players for a host, End game / Leave game) follow the session.
- **A server shutting down is reported as one, even with sends in flight** (UM-T65) — over WebSocket, a client still
  sending when the server stopped saw `TransportFailure` instead of `ServerShutdown`: the server closed the TCP
  connection with the client's data unread, which resets it, and the client lost NGO's shutdown reason with it. A Host
  or Server now tells every remote client first (`ShutdownNotice`, reliable); the client ends with `ServerShutdown` and
  closes its own connection, and the server shuts NGO down once they have gone (at most 2 s later). The roster's first
  round-trip probe, delayed 1 s after joining to keep a test green, now goes out at once.

## [1.0.0] — 2026-10-21

First stable release. Everything below was built across UM-T2 through UM-T19 and UM-T23 through UM-T46; full
per-ticket detail lives in [docs/USER_STORIES.md](docs/USER_STORIES.md) and [docs/BIBLE.md](docs/BIBLE.md), and
the design in [docs/rfc/0001-architecture.md](docs/rfc/0001-architecture.md).

### Core networking

- **Attribute-decorated RPCs and synced state** — `[ToServer]`, `[ToClients]`, `[ToOwner]`, `[ToClient]` and
  `[Synced]` on a `NetEntity` subclass; the package's own Roslyn source generator (netstandard2.0,
  `IIncrementalGenerator`) writes every send, stub and `NetworkVariable` binding, and its analyzer rejects a
  client write to shared state, a non-partial handler, an unsendable parameter or a generic `NetEntity` at
  compile time (diagnostics MAMP001–MAMP013).
- **Any topology, same code** — `NetConfig` and `SessionStateMachine` drive a listen server, dedicated server
  or client from one game codebase; `NetSession` wraps NetworkManager + Unity Transport (UDP or WebSocket) and
  a headless bootstrap with command-line and environment overrides.
- **Join codes** — a 9-character, typo-tolerant `JoinCode` (`ABC-EFG-HJK`, a Damm check digit that catches
  every single substitution and neighbour swap) resolved through Unity Relay for a listen server or the
  package's own self-hosted ASP.NET Core rendezvous service for a dedicated server, behind one
  `IJoinCodeService` interface.
- **Reconnect and seats** — `SeatRegistry` holds a dropped player's seat and player object for a configurable
  grace period, reclaims it on the same token's return, and can take over a stale connection; a kicked or
  replaced client is always told why before the server closes its connection.
- **Connection approval** — a versioned `ConnectionPayload` (game version, protocol hash, player token,
  display name, colour) is checked before a client is seated, with a typed rejection (version mismatch,
  protocol mismatch, banned, full) and a human-readable reason on the wire.
- **Network condition simulation** — built-in profiles (`Broadband`, `Mobile4G`, `Mobile3G`, `Lossy`, `Custom`)
  retune Unity Transport's simulator pipeline at runtime, from the command line or a live `NetSession`, for
  testing on a bad connection without leaving the editor.

### Tooling and samples

- **Editor tooling** — **MindAttic ▸ Multiplayer ▸ Set Up Scene**, **Validate Prefabs** and
  **Build Dedicated Server (Linux)** turn an un-networked scene into a validated, buildable dedicated server in
  one menu.
- **Minimal sample** — a 10-minute, Package Manager–importable sample (host, join by IP, join by code) built
  from one `[ToServer]` call, one `[ToClients]` call and one `[Synced]` field.
- **CI** — `dotnet test` across every engine-free assembly plus GameCI PlayMode/EditMode runs on every push, and
  a nightly Docker Compose smoke test exercising a real dedicated server, rendezvous service and two bot
  clients end to end.

### Hosting

- **`mp-host`** — one deploy pipeline (build the server package, push to GHCR, provision, open the firewall,
  health-check, read back the self-registered join code) now reaches fifteen-plus providers: a generic VPS over
  SSH + Docker Compose, RunPod, Hetzner Cloud, Vultr, DigitalOcean, Linode/Akamai, OVHcloud, AWS EC2, AWS
  Lightsail, Google Compute Engine, Azure VM, Fly.io, Oracle Cloud's Always Free tier, and free local/LAN
  hosting with best-effort UPnP port mapping — plus match-allocation platforms Edgegap, Gameye and AWS
  GameLift through a shared `IMatchAllocator` contract. Every credential resolves through `MindAttic.Vault`,
  never the repo; every provider call is covered by NUnit mocks, with opt-in `[Explicit]` smoke tests against
  the real APIs.
- **[Hosting guide](https://mindattic.github.io/Unity.Multiplayer/articles/hosting.html)** ties the whole
  provider table together, with one page per provider covering setup, sizing and cost.

### Documentation

- **[Docs site](https://mindattic.github.io/Unity.Multiplayer/)** — a DocFX site published to GitHub Pages:
  a 10-minute getting-started guide, topologies, the attributes reference, join codes, reconnect and seats,
  testing, hosting, porting an existing game, troubleshooting, and an API reference generated from this
  repository's XML doc comments.
- **README** rewritten to the house `README-STYLE.md` format and linked to the docs site.

### Fixes rolled into this release

A bug-hunt pass (UM-T36) and standalone reports found and fixed, each verified by a test:

- `mp-host`: a malformed ARM request URL in the Azure VM client (UM-T37); the generated Compose file mounting
  saves/logs at the wrong container path (UM-T38); `--dry-run` still blocking on a real health/join-code
  timeout over SSH (UM-T39); `--dry-run` still launching a real Unity batch-mode build (UM-T40); batch-mode
  server builds ignoring the open scene's port/transport (UM-T41); a literal `$` in a secret or environment
  value corrupted by Compose interpolation (UM-T42); SSH passphrase-protected keys silently failing under
  `BatchMode=yes` (UM-T43); `--dry-run status` always reporting `Stopped` over SSH (UM-T44); a missing
  credential file crashing the CLI with a raw stack trace instead of a clear message (UM-T45).
- Package: `Unity.Multiplayer.asmdef` missing its `Unity.Services.Relay` reference, breaking a
  `MINDATTIC_UGS` build (UM-T46).

### Known limits

- `noEngineReferences` keeps `Unity.Multiplayer.Core` free of `UnityEngine` and `Unity.Netcode`
  ([UM-LAW-2](docs/BIBLE.md#um-law-2--nothing-above-the-ngo-adapter-imports-unitynetcode)); everything above the
  NGO adapter sees only MindAttic types.
- A live Unity Relay allocation needs a linked Unity Gaming Services project and is not exercised by the test
  suite; `MINDATTIC_UGS` compiles and is covered by the rest of the PlayMode suite.
- Ampere/ARM hosts (Oracle Cloud's Always Free A1 shape) have no matching Unity dedicated-server build target
  today, so `OciProvisioner` defaults to the AMD shape (UM-T31).
