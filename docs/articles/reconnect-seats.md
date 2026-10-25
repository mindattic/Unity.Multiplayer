# Reconnect and seats

A player who drops gets the same spot back: the same seat index, the same player object, not a new one spawned
from scratch. This is built on an engine-free `SeatRegistry` (testable with plain `dotnet test`, no Unity
required) plus the adapter wiring that keeps a live session in step with it.

## Connection approval

Before a client is seated at all, it's approved. Both the host's own player and every joining client send an
identity (a stable player token, display name, colour), the game's version and a protocol hash (one hash per
assembly, computed over every RPC and `[Synced]` signature — see
[Attributes reference](attributes.md#how-a-call-is-handled-and-what-the-generator-writes)). `NetSession`'s
approval callback checks, in order:

1. **Payload readable** — malformed bytes are rejected outright.
2. **Game version** — `NetConfig.GameVersion` must match.
3. **Protocol hash** — the generated RPC/synced shapes must match: a client built against different networked
   code is refused before it can call anything unexpected.
4. **Ban list** — `IBanList` / `InMemoryBanList`, or your own implementation set on `NetSession.BanList`.
5. **Capacity** — `NetConfig.MaxPlayers`, with a returning player's own held seat excluded from the count.
6. **Seating** — once past the checks above, `SeatRegistry.Claim` seats the connection; a token whose seat is the
   host's own and still connected is refused here instead (`TokenInUse`, see below).

A rejection carries a typed `ApprovalRejection` (`BadPayload`, `VersionMismatch`, `ProtocolMismatch`, `Banned`,
`Full`, `TokenInUse`) and a wire reason string; the client's `NetSession.LastDisconnect` reports it in plain
language, and `NetSession.ConnectionRejected` fires server-side.

## Seats

A `Seat` is a player's slot on the server: `Index` (stable for the seat's life), the `PlayerToken` that owns it,
`DisplayName`/`Color` as last seen, and a `Status`:

```csharp
public enum SeatStatus
{
    Occupied, // a connected client sits in it
    Held,     // its player dropped; waiting for the same token until Seat.HeldUntil
    Released, // freed — a snapshot only, the registry no longer has it
}
```

When a connection drops, its seat isn't freed immediately: it goes `Held` until `NetConfig.ReconnectGraceSeconds`
(default 60) passes, and a held seat still counts against capacity (so nobody else takes a dropped player's
spot the instant they lag out). The same player token reconnecting within that window reclaims the seat — same
`Index`, and the adapter respawns the **same player object** for the new connection instead of spawning another
one (`SeatReclaimed`). A token still seated on a connection the server hasn't yet noticed is dead (a killed
client restarted, for example) **takes the seat over** from the stale connection, which the server then drops
(`SeatClaimOutcome.TookOver`); this is the one case where the server proactively closes a connection it hasn't
heard disconnect yet.

The host's own seat is the one exception: it is never taken over while connected. A player token is remembered
per install folder (see below), so a second copy of the same build launched from that folder and joined by IP
sends the *host's own token* — not a stale reconnect, since the host never legitimately comes back through the
join path. That join is refused (`SeatClaimOutcome.Refused`, `ApprovalRejection.TokenInUse`) rather than bumping
the host out of its own lobby and game (UM-T77).

```csharp
session.SeatTaken    += seat => { /* a new player took the lowest free seat */ };
session.SeatReclaimed += seat => { /* came back within the grace window */ };
session.SeatHeld     += seat => { /* dropped; held until seat.HeldUntil */ };
session.SeatReleased += seat => { /* grace ran out, or the player was kicked */ };
```

`session.Seats` lists every current seat; `session.TryGetSeat(clientId, out seat)` and
`session.GetSeatPlayerObject(seatIndex)` look one up. Seats live on the server only; every peer, clients included,
sees the players through `session.Roster` ([Players, names and colours](players.md)), where a held seat is a player
with `IsConnected` false who keeps their record, seat and name when they come back. `session.Kick(clientId, reason)` releases a seat
immediately and sends the reason before closing the connection.

### Remembering a player across restarts

`NetBootstrap`'s **Remember Player** option (on by default) keeps the player's token in `PlayerPrefs`, keyed per
install folder, so quitting and relaunching the game still reclaims the same seat; `-playertoken <token>` on
the command line overrides it (useful for the nightly smoke test's bot clients, which need a stable identity
without `PlayerPrefs`).

## Disconnect reasons

`Disconnection` on the client side, and `PlayerDisconnected` server-side, carry a typed `DisconnectReason`:
`ClientLeft` (a clean leave), `Kicked` (with the server's reason text), `TimedOut`, a rejected `Connection`
(one of the approval reasons above), or a transport failure. A kick or a stale-connection takeover sends the
reason as its own small reliable message before closing the connection, rather than relying on the transport's
own disconnect signal — which can arrive with no payload at all on a lossy link — so the reason is never lost
even under [simulated packet loss](topologies.md#testing-a-bad-connection).

## Where this is tested

`dotnet test tests/Core.Tests` covers `SeatRegistry`, `ConnectionAdmission` and `ConnectionApprovalValidator`
with no Unity at all. The PlayMode suite's `ApprovalAndSeatTests` and `LossyNetworkTests` exercise the whole
path end to end — drop and reconnect to the same seat, a killed client's restart taking its seat over, grace
expiry, and a kick's reason surviving a lossy link. See [Testing](testing.md) for how to run them.
