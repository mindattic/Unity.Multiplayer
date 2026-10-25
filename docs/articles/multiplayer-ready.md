# Multiplayer-ready: the standard for every MindAttic Unity game

Every MindAttic Unity game is built so that co-op or PvP can be added later **without re-architecting**, even
when it ships single-player first. That does not mean every game is networked from day one. It means the game's
code already has the shape networking needs, so adding it is a matter of plugging in this kit rather than
rewriting the game.

This page is the standard: eight rules, a checklist to review a game against, the law text a game cites from its
BIBLE, how each rule maps onto the kit when the game does go online, and Unity.Cursory as the example of what
happens when a game is ported after the fact.

## The rules

### 1. Every player intent is a command

Anything a player does that changes the game (move, attack, play a card, pick an option, pause a shared game) is
a **command**: a small, serializable value that carries the **player id (seat)** of whoever issued it. Every
command goes through **one command pipeline** (`ISession.Submit`, rule 7) that validates it and hands it to the
authority. Input code never changes game state directly; it builds a command and submits it.

```csharp
public readonly struct MoveCommand : IGameCommand
{
    public MoveCommand(PlayerId player, Direction direction) { Player = player; Direction = direction; }
    public PlayerId Player { get; }
    public Direction Direction { get; }
}

// input code: never touches state
session.Submit(new MoveCommand(session.LocalPlayer, Direction.Left));
```

A command holds plain data only: numbers, enums, strings, ids. Never a `GameObject`, `Transform`, component or
other object reference, which cannot cross a wire.

### 2. One authority owns game state, apart from presentation

One object (the **authority**: a `GameState` plus the rules that change it) owns the game state. Only the authority
writes it, and only in response to commands and ticks. **Presentation** (`MonoBehaviour`s, views, UI, audio, VFX,
the camera) only **reads** state and listens to **events** the authority raises (`PieceMoved`, `PlayerScored`). A
view never writes state, and the authority never references a view.

The cleanest form is the one this kit itself uses: the rules live in an engine-free assembly
(`noEngineReferences`) tested by plain `dotnet test`, and the Unity assembly holds presentation and wiring.

### 3. No global "the player"

There is no `Player.Instance`, `GameManager.Player`, static `LocalPlayer` or single `score` field. Everything that
belongs to a player (position, health, hand, score, settings, camera target) is **keyed by player id**: a
dictionary, an array indexed by seat, or a field on a per-player record. Which player this machine controls is
answered by **one seam**, `ISession.LocalPlayer`, and only presentation and input ask it. Rules never do: to the
rules, every player is just an id.

### 4. Rules are deterministic

Given the same starting state and the same commands in the same order, the rules produce the same state.

- **Seeded RNG.** Randomness comes from a seeded generator that is part of the game state
  (`System.Random(seed)` or a small PCG/xorshift you own), never `UnityEngine.Random` in rules.
- **Fixed tick.** Rules advance in fixed steps (`Step(tick)` at a fixed rate, 30 Hz by default), never by
  `Time.deltaTime`, `Time.time` or frame count. Presentation may interpolate between ticks; rules do not see frames.
- **No hidden inputs.** Rules read no wall clock, no `Input`, no `Physics` queries on the presentation scene, and do
  not iterate an unordered collection where the order changes the result.

Engine physics (Physics2D / PhysX) is not deterministic across machines. A game whose rules need it runs it on the
authority only and replicates the results (that is fine under this kit, see rule 2), but it gives up replays from
commands alone, so prefer simple rule-owned motion where the design allows.

### 5. Stable entity ids

Every thing the rules track (units, pieces, pickups, doors, projectiles that matter) has a **stable id** assigned
by the authority: an int counter or a string key from level data (`crate:0`, `door:north`). Commands and events
refer to entities by id, never by object reference, list index or `GetInstanceID()`. Presentation maps ids to
`GameObject`s in one lookup.

### 6. State and commands serialize

`GameState` and every command can be written to bytes or JSON and read back equal. That gives a game, before any
networking: **save games** (a state snapshot), **replays** (a seed plus the command log), **bug reports** that
reproduce, and **tests** that start from a recorded position. Keep state as plain data (records, arrays, ids), with
no engine types in it beyond simple values you can convert (`Vector2` → two floats).

### 7. An `ISession` seam with a `LocalSession`

All of the above meet in one interface the game owns. Game code (input, presentation, the game's start-up) talks to
`ISession`, never to the authority directly:

```csharp
public interface ISession
{
    PlayerId LocalPlayer { get; }                  // the one seam for "who am I" (rule 3)
    IReadOnlyList<PlayerId> Players { get; }
    IGameStateView State { get; }                  // read-only view for presentation (rule 2)
    event Action<IGameEvent> EventRaised;          // what presentation listens to
    void Submit(IGameCommand command);             // the one command pipeline (rule 1)
}
```

The single-player game ships with **`LocalSession`**: it owns the authority in process, applies each submitted
command on the next tick, and raises events straight to presentation. Hot-seat or split-screen co-op is the same
`LocalSession` with more than one local player id. When the game goes online, a `NetGameSession : ISession` built
on this kit's `NetSession` replaces it (see [From ready to networked](#from-ready-to-networked)), and input,
presentation and rules do not change.

### 8. A test plays commands from two player ids

The game has at least one test that creates a `LocalSession` with **two players**, submits commands as each, steps
the ticks, and asserts on the state: both players acted, neither one's command touched the other's things, and a
command from the wrong player is refused. With rules 4 and 6 in place this is a plain, fast `dotnet test` or
EditMode test, and it is the proof that the game really is per-player and not secretly single-player.

```csharp
[Test]
public void TwoPlayersMoveIndependently()
{
    var session = LocalSession.Create(seed: 7, players: 2);
    var ann = session.Players[0];
    var bob = session.Players[1];

    session.Submit(new MoveCommand(ann, Direction.Left));
    session.Submit(new MoveCommand(bob, Direction.Up));
    session.Step(ticks: 1);

    Assert.That(session.State.PositionOf(ann), Is.EqualTo(new Cell(-1, 0)));
    Assert.That(session.State.PositionOf(bob), Is.EqualTo(new Cell(0, 1)));
}
```

## Checklist

Review a game against this before each release, and when adding any new mechanic.

- [ ] Every player action is a serializable command type that carries a player id.
- [ ] Every command goes through `ISession.Submit`; no input code writes game state.
- [ ] Commands hold only plain data and ids: no `GameObject`, component or object references.
- [ ] One authority writes game state; presentation only reads state and handles events.
- [ ] The authority references no view, `MonoBehaviour`, scene object or UI.
- [ ] No static or singleton "the player"; per-player data is keyed by player id.
- [ ] `ISession.LocalPlayer` is the only place that answers "which player is this machine", and rules never call it.
- [ ] Rules use a seeded RNG held in the state; no `UnityEngine.Random` in rules.
- [ ] Rules advance on a fixed tick; no `Time.deltaTime`, `Time.time` or frame count in rules.
- [ ] Every tracked entity has a stable id from the authority; commands and events use ids.
- [ ] `GameState` and every command round-trip through serialization equal.
- [ ] The game runs on a `LocalSession` behind `ISession`.
- [ ] A test plays commands from two player ids and asserts per-player results, including a refused wrong-player
      command.
- [ ] (Strongly preferred) Rules live in an engine-free assembly tested by `dotnet test`.

## The law games cite

Each MindAttic Unity game copies this law into its BIBLE's Laws section, numbered in its own scheme, and links
back here, so the rules have one home ([UM-LAW-4](https://github.com/mindattic/Unity.Multiplayer/blob/main/docs/BIBLE.md#um-law-4)):

> ### XX-LAW-n — Multiplayer-ready {#XX-LAW-n}
> This game is built so co-op or PvP can be added without re-architecting, per Unity.Multiplayer's
> [Multiplayer-ready standard](https://mindattic.github.io/Unity.Multiplayer/articles/multiplayer-ready.html):
> every player intent is a serializable command stamped with a player id and submitted through `ISession`; one
> authority owns game state and presentation only reads state and events; there is no global "the player", only
> player ids and `ISession.LocalPlayer`; rules are deterministic (seeded RNG, fixed tick, no `UnityEngine.Random` or
> frame time); entities have stable ids; state and commands serialize; the game runs on a `LocalSession` behind
> `ISession`; and a test plays commands from two player ids. A change that breaks any of these is a bug.

## From ready to networked

When the game goes online, each rule maps onto a piece of this kit. Nothing in the rules or presentation moves;
the work is writing `NetGameSession` and the networked objects that carry commands and state.

| Rule | In a multiplayer-ready game | With Unity.Multiplayer |
|---|---|---|
| 1. Commands | `ISession.Submit(command)` stamped with a player id | A [`NetEntity`](attributes.md) with a `[ToServer]` method per command (or one taking the serialized command). The generator builds the RPC; the server's handler receives the caller as `NetSender` and stamps the command with the sender's seat, never a seat the client claims. `RateLimit` and `RequireOwnership` are the first line of validation. |
| 2. One authority | The authority inside `LocalSession` | The server owns state ([UM-LAW-1](https://github.com/mindattic/Unity.Multiplayer/blob/main/docs/BIBLE.md#um-law-1)). The same authority runs only when `session.IsServer` (see Cursory's `LevelServer`, the template's `Arena.Serve`); clients get results through `[Synced]` fields and `[ToClients]` / `[ToOwner]` / `[ToClient]` events, which `NetGameSession` turns back into `IGameEvent`s. |
| 3. Per player id | `PlayerId`, `ISession.LocalPlayer` | The player id is the seat: `NetSender.SeatIndex` on the server, kept across reconnects ([Reconnect and seats](reconnect-seats.md)). [`NetRoster`](players.md) has one `NetPlayer` per seat with name, colour and `IsLocal`; `NetGameSession.LocalPlayer` is `session.Roster.Local`'s seat. |
| 4. Deterministic | Seeded RNG, fixed tick | The authority steps on the session's `ITickClock` (`NetSession.Clock`, `Ticked`, 30 Hz by default; `ManualTickClock` in tests). The seed goes into the state, so a reconnecting client or a replay agrees. |
| 5. Stable ids | Authority-assigned ids | [`NetKey`](objects.md) carries the id on the spawned object; `NetSession.Objects.Spawn(prefab, id)` on the server and `Objects.TryGet(id, out ...)` on every peer, late joiners included. |
| 6. Serialization | Snapshots and command logs | The serialized command is what a `[ToServer]` call sends; a state snapshot is what a late joiner or a reconnecting seat is sent (`[ToClient]` to that client id), and what a test starts from. |
| 7. `ISession` seam | `LocalSession` | `NetGameSession : ISession` wraps `NetSession`: `Submit` calls the `[ToServer]` method, `State` reads the synced view, `EventRaised` fires from the client RPCs. Start-up picks `LocalSession` or `NetGameSession`; [`NetFrontEnd`](front-end.md) gives the menus that start it. |
| 8. Two-player test | `LocalSession` with two player ids | The same scenario over the network in a [`NetSessionTest`](testing.md) (`Topology.Host`, `clients: 1`, or a dedicated server with two clients), and [`NetBotTest`](bots.md) for bots playing it end to end. Keep the `LocalSession` test too: it is the fast one. |

The [New Game wizard's](getting-started.md) template is laid out this way already: `Player` sends `[ToServer] Move`,
`Arena` holds the server's rules, crates are keyed by `NetKey`, and `GameTests` is a `NetSessionTest`. A
multiplayer-ready game that later adopts the kit ends up with the same shape.

## The counter-example: Unity.Cursory

[Unity.Cursory](case-study-cursory.md) is the kit's first real game, and it works. But it was **ported after the
fact**: the original Cursory was a Blazor Server game whose state, rules and room logic grew together in one
`GameState`, and none of this standard existed when it was written. The port shows what each missing rule cost:

- **No separate authority to lift (rule 2).** The engine-free Core (levels, goals, circuits, votes, rope geometry)
  had to be carved out of the original's `GameState.cs` as its own ticket before any networking work could start,
  and some features (switch tiles, doors, shape attachments) were dropped rather than ported.
- **Room-wide settings instead of per-player ones (rule 3).** Cursor collision and the segmented tether were
  room-wide booleans; the port had to make them each player's own `[Synced]` setting on `CursorAvatar`.
- **No local-player seam (rule 3).** Presentation and bots find their cursor through static lookups on the
  networked class (`CursorAvatar.Local(session)`, `ForClient`, `All`) rather than a game-owned session seam, so
  "which player am I" is tied to the network object.
- **Engine physics in the rules (rule 4).** The simulation runs on Physics2D on the server. It is authoritative and
  correct, but not deterministic, so Cursory has no command replays and its physics tests drive real Physics2D in
  PlayMode rather than replaying commands in `dotnet test`.
- **Levels as code (rules 5 and 6).** Levels were seed methods; the port turned them into level assets with ids
  so bodies could be keyed and reset by id.

A game that follows this standard from the start gets all of that for free: its authority is already separate,
its players are already ids, and going online is `NetGameSession` plus the networked objects in the table above.
