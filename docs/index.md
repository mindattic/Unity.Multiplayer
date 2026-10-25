---
_disableToc: true
---

# Unity.Multiplayer

> **v2.0.0**: the one-stop shop release. **MindAttic ▸ Multiplayer ▸ New Game…** makes a complete networked game in
> a fresh project. See the [changelog](https://github.com/mindattic/Unity.Multiplayer/blob/main/CHANGELOG.md)
> and [all releases](https://github.com/mindattic/Unity.Multiplayer/releases).

The one-stop shop for multiplayer Unity 6 games. With its two sibling packages, MindAttic Components and Unity.Shared,
it is [one kit](articles/kit.md) that covers the whole game: attribute-decorated RPCs and synced fields, a listen server
or a dedicated server from the same code, menus, player names and colours, votes, objects found by key, bots, a test
kit, and one command to build and deploy a server.

```csharp
public partial class MinimalPlayer : NetEntity
{
    [ToServer(RateLimit = 20)]
    public partial void SetDirection(Vector2 direction);

    [ToClients]
    public partial void Bumped();

    [Synced] private Vector2 _position;

    private void OnSetDirection(Vector2 direction) => _direction = Vector2.ClampMagnitude(direction, 1f);
    private void OnBumped() => _flashUntil = Time.time + 0.2f;
}
```

That is the whole networked surface of the Minimal sample: one client-to-server
call, one server-to-everyone call and one server-owned field. The package's source generator writes the rest.

## Start here

- **[Getting started: a new game](articles/getting-started.md)**: from an empty folder to a networked game with
  menus, votes, bots, tests and a server running in Docker, in minutes, with every step copy-pasteable.
- **[The kit](articles/kit.md)**: Unity.Multiplayer, MindAttic Components and Unity.Shared, and which piece does
  which job.
- **[Multiplayer-ready](articles/multiplayer-ready.md)**: the standard every MindAttic Unity game follows so co-op
  or PvP can be added later without re-architecting, with a checklist and the law a game cites from its BIBLE.
- **[Case study: Unity.Cursory](articles/case-study-cursory.md)**: how a full co-op physics game uses every piece.
- **[Drop-in front end](articles/front-end.md)** — `NetFrontEnd`: main menu, lobby with join code, connection
  dialogs, reconnect and an in-game Esc menu from one component, with hooks for the game's own options.
- **[Topologies](articles/topologies.md)** — listen server vs. dedicated server, and how the same game code runs
  as either.
- **[Attributes reference](articles/attributes.md)** — `[ToServer]`, `[ToClients]`, `[ToOwner]`, `[ToClient]`,
  `[Synced]`, and the rules the generator and analyzers enforce.
- **[Join codes](articles/join-codes.md)** — Unity Relay vs. the self-hosted rendezvous service, and which one a
  session picks by default.
- **[Reconnect and seats](articles/reconnect-seats.md)** — how a dropped player gets the same seat and player
  object back.
- **[Testing](articles/testing.md)** — the test suites, what each one covers, and how to add your own.
- **[Hosting](articles/hosting.md)** — the full provider table: pick a path, read one page per provider.
- **[Porting a game](articles/porting.md)** — moving an existing game onto `NetEntity` / `NetSession`.
- **[Troubleshooting](articles/troubleshooting.md)** — the errors and symptoms this package produces most, and
  what causes them.

## API reference

The **[API reference](xref:Unity.Multiplayer.Core)** is generated from this repository's XML doc comments
for `Unity.Multiplayer.Core` — the engine-free assembly that holds the attributes, `NetConfig`, `JoinCode`,
seats, connection approval and the rendezvous client: the part of the surface a game calls directly. `NetSession`,
`NetEntity`, `NetBootstrap` and the editor commands live in the NGO adapter and Editor assemblies, which reference
`UnityEngine` / `Unity.Netcode` / `UnityEditor` and so aren't reflected here; they're documented with full
signatures and examples in the guides above, in particular [Getting started](articles/getting-started.md) and
[Topologies](articles/topologies.md).

## Project links

- [GitHub repository](https://github.com/mindattic/Unity.Multiplayer)
- [docs/BIBLE.md](https://github.com/mindattic/Unity.Multiplayer/blob/main/docs/BIBLE.md) — the project's rules
- [RFC 0001](https://github.com/mindattic/Unity.Multiplayer/blob/main/docs/rfc/0001-architecture.md) — the design
  and build order
