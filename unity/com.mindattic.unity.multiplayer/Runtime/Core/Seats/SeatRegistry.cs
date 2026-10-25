using System;
using System.Collections.Generic;

namespace Unity.Multiplayer.Core
{
    /// <summary>
    /// The server's seating plan: maps each player token to a seat, so a player who drops and reconnects
    /// within the grace window gets the same seat (and, through the adapter, the same player object) back.
    /// </summary>
    /// <remarks>
    /// <para>Engine-free: time is passed in (any monotonic clock in seconds), so the adapter feeds it Unity's
    /// realtime clock and tests feed it numbers. Not thread-safe; the adapter calls it from Unity's main thread.</para>
    /// <para>Life of a seat: <see cref="Claim"/> occupies the lowest free seat for a new token. <see cref="Release"/>
    /// (the connection dropped) holds it for <see cref="GraceSeconds"/>; a <see cref="Claim"/> with the same token
    /// in that time takes it back. <see cref="Expire"/> frees held seats whose time ran out; <see cref="Free(string)"/>
    /// frees one at once (a kick). Held seats count as taken: a new player cannot sit in a seat someone may return to.</para>
    /// <para>The host's seat (claimed with <c>isHost: true</c>) is never taken over while occupied: a join presenting
    /// its token from a different connection is refused (<see cref="SeatClaimOutcome.Refused"/>), not treated as a
    /// stale-connection reconnect, since the host never legitimately comes back through <see cref="Claim"/> (UM-T77).</para>
    /// </remarks>
    public sealed class SeatRegistry
    {
        private readonly Dictionary<string, Entry> _byToken = new Dictionary<string, Entry>(StringComparer.Ordinal);
        private readonly Dictionary<ulong, Entry> _byClient = new Dictionary<ulong, Entry>();

        /// <summary>A registry with <paramref name="capacity"/> seats that holds a dropped player's seat for <paramref name="graceSeconds"/>.</summary>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="capacity"/> is below one or <paramref name="graceSeconds"/> is negative or NaN.</exception>
        public SeatRegistry(int capacity, double graceSeconds)
        {
            if (capacity < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "A registry needs at least one seat.");
            }

            if (double.IsNaN(graceSeconds) || graceSeconds < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(graceSeconds), graceSeconds, "The grace window cannot be negative.");
            }

            Capacity = capacity;
            GraceSeconds = graceSeconds;
        }

        /// <summary>The number of seats.</summary>
        public int Capacity { get; }

        /// <summary>How long a dropped player's seat is held, in seconds.</summary>
        public double GraceSeconds { get; }

        /// <summary>Seats taken, occupied or held.</summary>
        public int Count
        {
            get { return _byToken.Count; }
        }

        /// <summary>Seats with a connected client in them.</summary>
        public int OccupiedCount
        {
            get { return _byClient.Count; }
        }

        /// <summary>Seats held for a player who dropped.</summary>
        public int HeldCount
        {
            get { return _byToken.Count - _byClient.Count; }
        }

        /// <summary>A snapshot of every taken seat, by index.</summary>
        public IReadOnlyList<Seat> Seats
        {
            get
            {
                var seats = new List<Seat>(_byToken.Count);
                foreach (Entry entry in _byToken.Values)
                {
                    seats.Add(entry.Snapshot());
                }

                seats.Sort((a, b) => a.Index.CompareTo(b.Index));
                return seats;
            }
        }

        /// <summary>
        /// Seats taken by anyone other than <paramref name="playerToken"/>: the number of players a newcomer with
        /// that token competes with (what <see cref="ConnectionApprovalValidator"/> calls connected players).
        /// </summary>
        public int CountExcept(string playerToken)
        {
            return playerToken != null && _byToken.ContainsKey(playerToken) ? _byToken.Count - 1 : _byToken.Count;
        }

        /// <summary>The seat of a connected client.</summary>
        public bool TryGetByClient(ulong clientId, out Seat seat)
        {
            Entry entry;
            seat = _byClient.TryGetValue(clientId, out entry) ? entry.Snapshot() : null;
            return seat != null;
        }

        /// <summary>The seat (occupied or held) of a player token.</summary>
        public bool TryGetByToken(string playerToken, out Seat seat)
        {
            Entry entry;
            seat = playerToken != null && _byToken.TryGetValue(playerToken, out entry) ? entry.Snapshot() : null;
            return seat != null;
        }

        /// <summary>The seat (occupied or held) at <paramref name="index"/>.</summary>
        public bool TryGetByIndex(int index, out Seat seat)
        {
            foreach (Entry entry in _byToken.Values)
            {
                if (entry.Index == index)
                {
                    seat = entry.Snapshot();
                    return true;
                }
            }

            seat = null;
            return false;
        }

        /// <summary>
        /// Seats the connection <paramref name="clientId"/> for <paramref name="payload"/>'s player token: back in
        /// its held seat, in place of a stale connection with the same token, or in the lowest free seat.
        /// </summary>
        /// <param name="clientId">The new connection.</param>
        /// <param name="payload">The approved payload (token, name and colour).</param>
        /// <param name="now">The current time in seconds; held seats that ran out by then are freed first and do not come back.</param>
        /// <param name="isHost">
        /// True only for the host's own seat (<see cref="ConnectionAdmission.AdmitLocal"/>): marks the seat so a
        /// later join presenting the same token, from a different connection, is refused instead of taking the
        /// host's live seat over (UM-T77) — the host never goes through this method again to legitimately reclaim it.
        /// </param>
        /// <exception cref="ArgumentNullException"><paramref name="payload"/> is null.</exception>
        /// <exception cref="InvalidOperationException"><paramref name="clientId"/> already sits in another player's seat.</exception>
        public SeatClaim Claim(ulong clientId, ConnectionPayload payload, double now, bool isHost = false)
        {
            if (payload == null)
            {
                throw new ArgumentNullException(nameof(payload));
            }

            string token = payload.PlayerToken;
            Entry current;
            if (_byClient.TryGetValue(clientId, out current) && !string.Equals(current.Token, token, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Client " + clientId + " already sits in seat " + current.Index + " for another player.");
            }

            Entry entry;
            if (_byToken.TryGetValue(token, out entry) && entry.Status == SeatStatus.Held && entry.HeldUntil <= now)
            {
                Remove(entry); // its time ran out: the player starts over
                entry = null;
            }

            if (entry != null)
            {
                if (isHost)
                {
                    entry.MarkHost();
                }

                if (entry.Status == SeatStatus.Occupied && entry.IsHost && entry.ClientId != clientId)
                {
                    // The host's seat is connected and this is not the host reconnecting (the host never comes
                    // back through this path) - never bump it out, whatever the token says (UM-T77).
                    return new SeatClaim(SeatClaimOutcome.Refused, null, null);
                }

                SeatClaimOutcome outcome;
                ulong? replaced = null;
                if (entry.Status == SeatStatus.Held)
                {
                    outcome = SeatClaimOutcome.Reclaimed;
                }
                else if (entry.ClientId == clientId)
                {
                    outcome = SeatClaimOutcome.Reclaimed; // the same connection asking twice: nothing moves
                    entry.Update(payload);
                    return new SeatClaim(outcome, entry.Snapshot(), null);
                }
                else
                {
                    outcome = SeatClaimOutcome.TookOver;
                    replaced = entry.ClientId;
                    _byClient.Remove(entry.ClientId);
                }

                entry.Update(payload);
                entry.Occupy(clientId);
                entry.Reconnects++;
                _byClient[clientId] = entry;
                return new SeatClaim(outcome, entry.Snapshot(), replaced);
            }

            if (_byToken.Count >= Capacity)
            {
                return new SeatClaim(SeatClaimOutcome.Full, null, null);
            }

            entry = new Entry(LowestFreeIndex(), token);
            entry.Update(payload);
            entry.Occupy(clientId);
            if (isHost)
            {
                entry.MarkHost();
            }

            _byToken[token] = entry;
            _byClient[clientId] = entry;
            return new SeatClaim(SeatClaimOutcome.New, entry.Snapshot(), null);
        }

        /// <summary>
        /// The connection <paramref name="clientId"/> dropped: its seat is held until <paramref name="now"/> +
        /// <see cref="GraceSeconds"/> (freed at once when the grace is zero).
        /// </summary>
        /// <returns>The seat after the change (Held or Released), or null when the client had no seat (for example it was already replaced).</returns>
        public Seat Release(ulong clientId, double now)
        {
            Entry entry;
            if (!_byClient.TryGetValue(clientId, out entry))
            {
                return null;
            }

            _byClient.Remove(clientId);
            if (GraceSeconds <= 0)
            {
                _byToken.Remove(entry.Token);
                return entry.Released();
            }

            entry.Hold(now + GraceSeconds);
            return entry.Snapshot();
        }

        /// <summary>
        /// The player on connection <paramref name="clientId"/> now plays as <paramref name="displayName"/> in
        /// <paramref name="color"/> (an accepted rename, UM-T55); the seat keeps them across a reconnect too.
        /// </summary>
        /// <returns>The seat after the change, or null when the client has none.</returns>
        public Seat Rename(ulong clientId, string displayName, PlayerColor color)
        {
            Entry entry;
            if (!_byClient.TryGetValue(clientId, out entry))
            {
                return null;
            }

            entry.SetIdentity(displayName, color);
            return entry.Snapshot();
        }

        /// <summary>Frees the seat of the connection <paramref name="clientId"/> at once, with no grace window (a kick).</summary>
        /// <returns>The released seat, or null when the client had none.</returns>
        public Seat Free(ulong clientId)
        {
            Entry entry;
            return _byClient.TryGetValue(clientId, out entry) ? Remove(entry) : null;
        }

        /// <summary>Frees <paramref name="playerToken"/>'s seat at once, occupied or held.</summary>
        /// <returns>The released seat, or null when the token had none.</returns>
        public Seat Free(string playerToken)
        {
            Entry entry;
            return playerToken != null && _byToken.TryGetValue(playerToken, out entry) ? Remove(entry) : null;
        }

        /// <summary>Frees every held seat whose grace window has run out by <paramref name="now"/>.</summary>
        /// <returns>The seats freed (status Released), by index; empty when none.</returns>
        public IReadOnlyList<Seat> Expire(double now)
        {
            List<Entry> due = null;
            foreach (Entry entry in _byToken.Values)
            {
                if (entry.Status == SeatStatus.Held && entry.HeldUntil <= now)
                {
                    due = due ?? new List<Entry>();
                    due.Add(entry);
                }
            }

            if (due == null)
            {
                return Array.Empty<Seat>();
            }

            due.Sort((a, b) => a.Index.CompareTo(b.Index));
            var freed = new List<Seat>(due.Count);
            foreach (Entry entry in due)
            {
                freed.Add(Remove(entry));
            }

            return freed;
        }

        /// <summary>Empties the registry (the session ended).</summary>
        public void Clear()
        {
            _byToken.Clear();
            _byClient.Clear();
        }

        private Seat Remove(Entry entry)
        {
            _byToken.Remove(entry.Token);
            if (entry.Status == SeatStatus.Occupied)
            {
                _byClient.Remove(entry.ClientId);
            }

            return entry.Released();
        }

        private int LowestFreeIndex()
        {
            var taken = new bool[Capacity];
            foreach (Entry entry in _byToken.Values)
            {
                taken[entry.Index] = true;
            }

            for (int i = 0; i < taken.Length; i++)
            {
                if (!taken[i])
                {
                    return i;
                }
            }

            throw new InvalidOperationException("No free seat; the caller checks capacity first.");
        }

        private sealed class Entry
        {
            public Entry(int index, string token)
            {
                Index = index;
                Token = token;
            }

            public int Index { get; }

            public string Token { get; }

            public string DisplayName { get; private set; }

            public PlayerColor Color { get; private set; }

            public SeatStatus Status { get; private set; }

            public ulong ClientId { get; private set; }

            public double HeldUntil { get; private set; }

            public int Reconnects { get; set; }

            /// <summary>True once this seat has been claimed as the host's own (sticky: never cleared back to false).</summary>
            public bool IsHost { get; private set; }

            public void MarkHost()
            {
                IsHost = true;
            }

            public void Update(ConnectionPayload payload)
            {
                DisplayName = payload.DisplayName;
                Color = payload.Color;
            }

            public void SetIdentity(string displayName, PlayerColor color)
            {
                DisplayName = displayName;
                Color = color;
            }

            public void Occupy(ulong clientId)
            {
                Status = SeatStatus.Occupied;
                ClientId = clientId;
                HeldUntil = 0;
            }

            public void Hold(double until)
            {
                Status = SeatStatus.Held;
                HeldUntil = until;
            }

            public Seat Snapshot()
            {
                return new Seat(Index, Token, DisplayName, Color, Status, ClientId, HeldUntil, Reconnects);
            }

            public Seat Released()
            {
                return new Seat(Index, Token, DisplayName, Color, SeatStatus.Released, ClientId, 0, Reconnects);
            }
        }
    }
}
