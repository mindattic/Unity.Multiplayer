using System;

namespace Unity.Multiplayer.Core
{
    /// <summary>Where a <see cref="Seat"/> stands.</summary>
    public enum SeatStatus
    {
        /// <summary>A connected client sits in it.</summary>
        Occupied = 0,

        /// <summary>Its player dropped; the seat waits for the same player token until <see cref="Seat.HeldUntil"/>.</summary>
        Held = 1,

        /// <summary>Freed (the grace window ran out, or the player was kicked). A snapshot only; the registry no longer has it.</summary>
        Released = 2,
    }

    /// <summary>Why <see cref="SeatRegistry"/> gave or refused a seat.</summary>
    public enum SeatClaimOutcome
    {
        /// <summary>A new player got the lowest free seat.</summary>
        New = 0,

        /// <summary>A player whose seat was being held came back to it.</summary>
        Reclaimed = 1,

        /// <summary>
        /// The player's token was still seated on another connection (one the server has not yet noticed is
        /// dead, such as a killed client): the new connection takes the seat and the old one must be dropped.
        /// </summary>
        TookOver = 2,

        /// <summary>No seat is free.</summary>
        Full = 3,

        /// <summary>
        /// The token's seat is occupied by the host, who is never stale: a join presenting it is a stray copy of
        /// the install (for example, saved player-prefs shared by two copies of the same build), not the host
        /// reconnecting, so the connection is refused rather than bumping the host out of its own game.
        /// </summary>
        Refused = 4,
    }

    /// <summary>
    /// A snapshot of one player's slot on the server, kept across reconnects: the same
    /// <see cref="PlayerToken"/> always gets the same <see cref="Index"/> back while the seat lives.
    /// </summary>
    /// <remarks>Immutable; <see cref="SeatRegistry"/> hands out a new snapshot whenever a seat changes.</remarks>
    public sealed class Seat
    {
        internal Seat(int index, string playerToken, string displayName, PlayerColor color, SeatStatus status,
            ulong clientId, double heldUntil, int reconnects)
        {
            Index = index;
            PlayerToken = playerToken;
            DisplayName = displayName;
            Color = color;
            Status = status;
            ClientId = clientId;
            HeldUntil = heldUntil;
            Reconnects = reconnects;
        }

        /// <summary>The seat number, from 0 to capacity - 1. Stable for the seat's life.</summary>
        public int Index { get; }

        /// <summary>The player token that owns the seat.</summary>
        public string PlayerToken { get; }

        /// <summary>The name the player last connected with.</summary>
        public string DisplayName { get; }

        /// <summary>The colour the player last connected with.</summary>
        public PlayerColor Color { get; }

        /// <summary>Occupied, held for a reconnect, or released.</summary>
        public SeatStatus Status { get; }

        /// <summary>The client id of the connection in the seat, or of the last one when <see cref="Status"/> is not Occupied.</summary>
        public ulong ClientId { get; }

        /// <summary>While <see cref="SeatStatus.Held"/>: the time (on the registry's clock) the seat is freed. Otherwise 0.</summary>
        public double HeldUntil { get; }

        /// <summary>How many times the player has come back to this seat.</summary>
        public int Reconnects { get; }

        /// <summary>True while a connected client sits in the seat.</summary>
        public bool IsOccupied
        {
            get { return Status == SeatStatus.Occupied; }
        }

        /// <inheritdoc />
        public override string ToString()
        {
            string where = Status == SeatStatus.Occupied ? "client " + ClientId
                : Status == SeatStatus.Held ? "held until " + HeldUntil.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)
                : "released";
            return "Seat " + Index + " '" + DisplayName + "' (" + where + ")";
        }
    }

    /// <summary>The answer to <see cref="SeatRegistry.Claim"/>.</summary>
    public readonly struct SeatClaim
    {
        internal SeatClaim(SeatClaimOutcome outcome, Seat seat, ulong? replacedClientId)
        {
            Outcome = outcome;
            Seat = seat;
            ReplacedClientId = replacedClientId;
        }

        /// <summary>What happened.</summary>
        public SeatClaimOutcome Outcome { get; }

        /// <summary>The seat now occupied; null when <see cref="Outcome"/> is <see cref="SeatClaimOutcome.Full"/>.</summary>
        public Seat Seat { get; }

        /// <summary>For <see cref="SeatClaimOutcome.TookOver"/>: the stale connection the server should now drop. Otherwise null.</summary>
        public ulong? ReplacedClientId { get; }

        /// <summary>True when a seat was given.</summary>
        public bool Seated
        {
            get { return Seat != null; }
        }

        /// <summary>True when the player came back to a seat it already had (<see cref="SeatClaimOutcome.Reclaimed"/> or <see cref="SeatClaimOutcome.TookOver"/>).</summary>
        public bool IsReturn
        {
            get { return Outcome == SeatClaimOutcome.Reclaimed || Outcome == SeatClaimOutcome.TookOver; }
        }

        /// <inheritdoc />
        public override string ToString()
        {
            return Outcome + (Seat == null ? string.Empty : " " + Seat)
                + (ReplacedClientId.HasValue ? " replacing client " + ReplacedClientId.Value : string.Empty);
        }
    }
}
