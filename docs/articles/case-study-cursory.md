# Case study: Unity.Cursory

[Unity.Cursory](https://github.com/mindattic/Unity.Cursory) is Cursory, the co-op physics puzzle, rebuilt in Unity 6
with a dedicated server and host multiplayer. Each player is a cursor on a rope; heavy blocks need several players
pulling together to reach their pads. It is the first game built on the kit, and every piece of this library was
promoted out of it or proved in it. This page walks through how Cursory uses each piece, with links to its files, so
you can see the kit in a real game rather than the [template's](getting-started.md) toy.

Links below point at `main` in [mindattic/Unity.Cursory](https://github.com/mindattic/Unity.Cursory). Paths are short
for `Assets/Unity.Cursory/...`.

## The manifest and the assemblies

Cursory's [Packages/manifest.json](https://github.com/mindattic/Unity.Cursory/blob/main/Packages/manifest.json) holds
the [kit](kit.md)'s three packages by `file:` path, as siblings: `com.mindattic.unity.multiplayer`,
`com.mindattic.components.unity` and `com.mindattic.unity.shared`, with `com.unity.netcode.gameobjects` under
`testables` for the test kit.

Its code is split the way the library's is:

| Assembly | What | References |
|---|---|---|
| [Core/Unity.Cursory.Core.asmdef](https://github.com/mindattic/Unity.Cursory/blob/main/Assets/Unity.Cursory/Core/Unity.Cursory.Core.asmdef) | Engine-free rules: levels, goals, circuits, rope geometry, vote kinds, the bot plan | nothing (`noEngineReferences`); `Tests/Core.Tests` compiles the same files for plain `dotnet test` |
| [Runtime/Unity.Cursory.Runtime.asmdef](https://github.com/mindattic/Unity.Cursory/blob/main/Assets/Unity.Cursory/Runtime/Unity.Cursory.Runtime.asmdef) | Networking, physics, presentation, bots | Core, `Unity.Multiplayer` (and `.Core`, `.UI`), Netcode, MindAttic Components, URP |
| [Tests/PlayMode](https://github.com/mindattic/Unity.Cursory/tree/main/Assets/Unity.Cursory/Tests/PlayMode) | PlayMode suites | Runtime, `Unity.Multiplayer.Testing`, `Unity.Multiplayer.UI.Testing` |

## One scene for players, the server and bots

[Scenes/Main.unity](https://github.com/mindattic/Unity.Cursory/blob/main/Assets/Unity.Cursory/Scenes/Main.unity) has
one `NetworkManager` GameObject holding `NetworkManager`, Unity Transport, **`NetBootstrap`** (port 7777, tick rate 30,
eight players, serve when headless, read the command line and the server package's `server.json`), Cursory's
`LevelServer`, and **`NetFrontEnd`**. The same scene is the player's game, the dedicated server (a headless or
`-mode server` launch serves by itself) and a bot (`-bot A`). Nothing in Cursory decides which: `NetLaunch` does.

The scene was wired by the library's **Add Front End** wizard, called from Cursory's own setup script
([Editor/ProjectSetup.cs](https://github.com/mindattic/Unity.Cursory/blob/main/Assets/Unity.Cursory/Editor/ProjectSetup.cs),
`NetFrontEndSetup.Run`).

## The server's rules: `LevelServer`

[Runtime/Server/LevelServer.cs](https://github.com/mindattic/Unity.Cursory/blob/main/Assets/Unity.Cursory/Runtime/Server/LevelServer.cs)
sits beside the `NetBootstrap` with `[DefaultExecutionOrder(-1000)]`, so it registers its network prefabs and the player
prefab before the bootstrap starts a session. On `session.Started`, and only when `session.IsServer`, it loads the
level, builds the physics world, binds the simulation to the session's tick clock (`ITickClock`), and spawns the level
state, the vote board and the whistle board. `Serve(session)` does the same for a test's own session. The template's
`Arena.Serve` is the same shape.

This is the library's one rule ([UM-LAW-1](https://github.com/mindattic/Unity.Multiplayer/blob/main/docs/BIBLE.md#um-law-1--the-server-owns-state)) in practice: the server owns the game state, and
clients send what they want to happen.

## Networked entities and attributes

| Class | Attributes | What |
|---|---|---|
| [Runtime/Net/CursorAvatar.cs](https://github.com/mindattic/Unity.Cursory/blob/main/Assets/Unity.Cursory/Runtime/Net/CursorAvatar.cs) (the player prefab) | `[ToServer(Delivery = Unreliable, RateLimit = 30)] Move`; `[ToServer]` `Grab`, `GrabShape`, `GrabWall`, `GrabWireEnd`, `Release`, settings toggles; `[ToOwner] ReceiveGeometry`; `[Synced]` position, seat, rope | The cursor: input up, rope state down |
| [Runtime/Levels/LevelStateSync.cs](https://github.com/mindattic/Unity.Cursory/blob/main/Assets/Unity.Cursory/Runtime/Levels/LevelStateSync.cs) | `[Synced(OnChanged = ...)]` level number, solved goals, bulb, `LevelSolved`; `[ToClients] WiresMoved`; `[ToClient] WiresFor`; `[ToServer(RequireOwnership = false, RateLimit = 2)] RequestWires` | The level's progress, every direction of RPC in one class |
| [Runtime/Whistles/WhistleBoard.cs](https://github.com/mindattic/Unity.Cursory/blob/main/Assets/Unity.Cursory/Runtime/Whistles/WhistleBoard.cs) | `[ToServer(RequireOwnership = false, RateLimit = 8)] Whistle` (with the `NetSender`), `[ToClients] BroadcastWhistle` | A server-wide object any player calls |

See [Attributes reference](attributes.md) for every option used here.

## Players: names and colours from the roster

`CursorAvatar` keeps only its owner's seat as a `[Synced]` field
([CursorAvatar.Identity.cs](https://github.com/mindattic/Unity.Cursory/blob/main/Assets/Unity.Cursory/Runtime/Net/CursorAvatar.Identity.cs)).
Its name and colour come from `session.Roster.TryGetBySeat(seat)`, falling back to `PlayerColors.ForSeat`. The cursor,
its rope ([TetherRenderer.cs](https://github.com/mindattic/Unity.Cursory/blob/main/Assets/Unity.Cursory/Runtime/Presentation/TetherRenderer.cs)),
its name tag, its minimap dot, its whistle ripples and the lobby all follow a mid-game rename with no Cursory code
copying names around. The name and colour start in the player's Unity.Shared profile, through the front end's
`ProfileIdentitySource`; Cursory never touches Unity.Shared itself. See [Players](players.md).

## Menus: the drop-in front end

Cursory has no menu code. `NetFrontEnd` gives it the main menu, Join by IP and by code, the lobby with the code and
roster, connection dialogs, reconnect and the Esc menu, styled by
[Settings/FrontEndSettings.asset](https://github.com/mindattic/Unity.Cursory/blob/main/Assets/Unity.Cursory/Settings/FrontEndSettings.asset)
(title "Cursory", five reconnect attempts). Its one hook is `AddPauseMenuItems`, in
[Runtime/Presentation/CursoryClient.cs](https://github.com/mindattic/Unity.Cursory/blob/main/Assets/Unity.Cursory/Runtime/Presentation/CursoryClient.cs),
which adds two MindAttic Components toggles from
[PlayerSettingsItems.cs](https://github.com/mindattic/Unity.Cursory/blob/main/Assets/Unity.Cursory/Runtime/Presentation/PlayerSettingsItems.cs).
The HUD shows itself while `frontEnd.State` is `InGame` and opens the front end's pause menu from its own pause button.
See [Drop-in front end](front-end.md).

## The HUD: MindAttic Components

[Runtime/Presentation/CursoryHud.cs](https://github.com/mindattic/Unity.Cursory/blob/main/Assets/Unity.Cursory/Runtime/Presentation/CursoryHud.cs)
is built from the same design system as the menus: an `MaCanvas`, an `MaToolbar` with an `MaSelect` level picker, a
Reset `MaButton`, an `MaStatusPill` for the connection and an `MaIconButton` for pause, an `MaBanner` for "Level 3",
and `MaToast` when a level is solved.
[MinimapView.cs](https://github.com/mindattic/Unity.Cursory/blob/main/Assets/Unity.Cursory/Runtime/Presentation/MinimapView.cs)
wraps `MaMinimap`, which fits the level and pans the camera on a click. Because the menus, the vote overlay and the HUD
share one theme, they read as one game.

## Votes: reset and change level

[Core/Levels/LevelVotes.cs](https://github.com/mindattic/Unity.Cursory/blob/main/Assets/Unity.Cursory/Core/Levels/LevelVotes.cs)
defines Cursory's two kinds in engine-free code: `reset` ("Reset the level?") and `level` ("Switch to Level N?", with a
`Check` that the level exists). `LevelServer` spawns the vote board with `VoteBoard.Spawn(session)` and adds the two kinds
(`AddVoteKinds`); their `Passed` effects reset or switch the level on the server. The HUD's level picker and Reset
button call `StartVote`, and `VoteOverlay` shows the question, tally and Yes / No. (A scene can do the same with the
`NetVotes` component, as the template does.) See [Votes](votes.md).

## Networked objects by key

Every block and shape in a level has an id in the level data. On the server,
[Runtime/Net/NgoBodySync.cs](https://github.com/mindattic/Unity.Cursory/blob/main/Assets/Unity.Cursory/Runtime/Net/NgoBodySync.cs)
spawns each body with `session.Objects.Spawn(prefab, id, ...)` from a prefab that carries a `NetKey`
(`NetworkedBody.prefab`), and despawns them by key when a level resets or switches, so the same ids can be spawned again
in the same tick. On a client,
[ClientBodies.cs](https://github.com/mindattic/Unity.Cursory/blob/main/Assets/Unity.Cursory/Runtime/Presentation/ClientBodies.cs)
finds each block's body with `objects.TryGet(id, out Transform body)`. Late joiners and resets need no special case. See
[Networked objects by key](objects.md).

## Bots: two cursors solving level 2

[Runtime/Bots/CursoryBot.cs](https://github.com/mindattic/Unity.Cursory/blob/main/Assets/Unity.Cursory/Runtime/Bots/CursoryBot.cs)
is Cursory's `[NetBotDriver]` `IBotDriver`: the only bot code it has. The library joins the bot by code, with no menus,
and hands over the session. The driver waits for its cursor and the level's geometry, looks the heavy block up by id in
`NetSession.Objects`, and plays its half of a plan from
[Core/Bots/BotPlan.cs](https://github.com/mindattic/Unity.Cursory/blob/main/Assets/Unity.Cursory/Core/Bots/BotPlan.cs):
role A grabs one end of the block, role B the other, and they pull it around a wall onto the pad. It passes on the
server's own synced `LevelSolved`. `LevelServer` reads `-scenario 2` (`NetBots.Scenario`) to start on level 2.
See [Bots](bots.md).

## Tests

- **Plain `dotnet test`**: [Tests/Core.Tests](https://github.com/mindattic/Unity.Cursory/tree/main/Tests/Core.Tests)
  covers the engine-free Core: levels, goals, circuits, rope geometry, vote kinds, the bot plan, and solvability.
- **PlayMode on the test kit**:
  [BotEndToEndTests.cs](https://github.com/mindattic/Unity.Cursory/blob/main/Assets/Unity.Cursory/Tests/PlayMode/BotEndToEndTests.cs)
  is a `NetBotTest`. It serves `LevelServer` on the fixture's server and runs two `CursoryBot`s, which join by code
  through an in-process rendezvous service and solve level 2.
  [WhistleBoardTests.cs](https://github.com/mindattic/Unity.Cursory/blob/main/Assets/Unity.Cursory/Tests/PlayMode/WhistleBoardTests.cs)
  is a `NetSessionTest` (a host and two clients).
  [FrontEndTests.cs](https://github.com/mindattic/Unity.Cursory/blob/main/Assets/Unity.Cursory/Tests/PlayMode/FrontEndTests.cs)
  drives Cursory's real menus with `FrontEndTesting` (Host, Join by code, a rename reaching the host's cursor and lobby).
  See [Testing](testing.md).

## Hosting and the nightly smoke

Cursory has no build or deploy code of its own.
[mp-host.json](https://github.com/mindattic/Unity.Cursory/blob/main/mp-host.json) holds its defaults
(`"name": "cursory"`, `"image": "mindattic/unity-cursory"`, `"bots": "A,B"`, `"scenario": "2"`), and
[run.ps1](https://github.com/mindattic/Unity.Cursory/blob/main/run.ps1) is a thin call to
`mp-host game deploy | local | down --project .`, kept so MindAttic's `/deploy` router finds it. `mp-host game` finds
the editor, builds the server package from `Main.unity` with the library's batch build, and deploys it. Credentials live
in MindAttic.Vault. See Cursory's [docs/hosting.md](https://github.com/mindattic/Unity.Cursory/blob/main/docs/hosting.md)
and [Hosting](hosting.md).

Its [nightly workflow](https://github.com/mindattic/Unity.Cursory/blob/main/.github/workflows/nightly.yml) runs the
dotnet and PlayMode suites. Its end-to-end job is one `uses:` of this library's
[game-smoke.yml](bots.md#the-nightly-workflow), with Cursory's sibling repos listed. That job runs the Linux player as a
dedicated server, the rendezvous service, and the two bots under Docker Compose until both log `OK:`. Locally,
`mp-host game smoke` does the same.

## What to copy

| In your game | Copy from Cursory | Or start from |
|---|---|---|
| Server rules that start with the session | `LevelServer` (`Serve(session)`, `Started` + `IsServer`) | the template's `Arena.Serve` |
| Engine-free rules tested with `dotnet test` | `Core/` + `Tests/Core.Tests` | — |
| Names and colours everywhere | `CursorAvatar.Identity.cs` (seat → roster) | the template's `Player.DisplayName` / `Color` |
| Objects found by id | `NgoBodySync` + `ClientBodies` | the template's crates |
| Votes with a payload and a check | `LevelVotes` | the template's restart vote |
| A bot with a real strategy | `CursoryBot` + `BotPlan` | the template's `GameBot` |
| An end-to-end test | `BotEndToEndTests` | the template's `BotTests` |
