using System;
using System.Collections.Generic;

namespace Unity.Multiplayer.Core
{
    /// <summary>The outcome of <see cref="ConnectionAdmission.Admit"/>: the approval decision and, when approved, the seat.</summary>
    public readonly struct Admission
    {
        internal Admission(ApprovalDecision decision, SeatClaim claim, IReadOnlyList<Seat> expired)
        {
            Decision = decision;
            Claim = claim;
            Expired = expired ?? Array.Empty<Seat>();
        }

        /// <summary>Approved, or rejected with a reason the client is shown.</summary>
        public ApprovalDecision Decision { get; }

        /// <summary>The seat given when approved; default (no seat) when rejected.</summary>
        public SeatClaim Claim { get; }

        /// <summary>Held seats whose grace window ran out while this request was handled (freed before deciding).</summary>
        public IReadOnlyList<Seat> Expired { get; }

        /// <summary>True when the connection may proceed; <see cref="Claim"/> then holds its seat.</summary>
        public bool Approved
        {
            get { return Decision.Approved; }
        }

        /// <inheritdoc />
        public override string ToString()
        {
            return Decision.Approved ? "Admitted: " + Claim : Decision.ToString();
        }
    }

    /// <summary>
    /// The server's whole admission rule, engine-free: <see cref="ConnectionApprovalValidator"/> decides whether a
    /// connection request may join, counting seats from <see cref="SeatRegistry"/> (a returning player's own held
    /// seat does not count against it), and an approved request is seated in the same step. The NGO adapter's
    /// approval callback is a thin wrapper around <see cref="Admit"/>.
    /// </summary>
    public sealed class ConnectionAdmission
    {
        /// <summary>Joins the rule and the seating plan.</summary>
        /// <exception cref="ArgumentNullException">Either argument is null.</exception>
        public ConnectionAdmission(ConnectionApprovalValidator validator, SeatRegistry seats)
        {
            Validator = validator ?? throw new ArgumentNullException(nameof(validator));
            Seats = seats ?? throw new ArgumentNullException(nameof(seats));
        }

        /// <summary>The approval rule.</summary>
        public ConnectionApprovalValidator Validator { get; }

        /// <summary>The seating plan.</summary>
        public SeatRegistry Seats { get; }

        /// <summary>
        /// Decides on a remote client's connection request and, when it is approved, seats it. Held seats that ran
        /// out by <paramref name="now"/> are freed first.
        /// </summary>
        /// <param name="clientId">The connection asking to join.</param>
        /// <param name="connectionData">The bytes it sent (<see cref="ConnectionPayloadCodec.Encode"/>).</param>
        /// <param name="now">The current time in seconds, on the registry's clock.</param>
        public Admission Admit(ulong clientId, byte[] connectionData, double now)
        {
            IReadOnlyList<Seat> expired = Seats.Expire(now);

            PayloadDecodeResult decoded = ConnectionPayloadCodec.TryDecode(connectionData);
            ApprovalDecision decision = decoded.Success
                ? Validator.Validate(decoded.Payload, Seats.CountExcept(decoded.Payload.PlayerToken))
                : Validator.Validate(connectionData, Seats.Count);

            if (!decision.Approved)
            {
                return new Admission(decision, default(SeatClaim), expired);
            }

            SeatClaim claim = Seats.Claim(clientId, decision.Payload, now);
            if (!claim.Seated)
            {
                decision = claim.Outcome == SeatClaimOutcome.Refused
                    // The token's seat is the host's own and still connected: never bump the host (UM-T77).
                    ? ApprovalDecision.Reject(ApprovalRejection.TokenInUse,
                        "That player is already connected to this game.", decision.Payload)
                    // Otherwise the validator counted seats a moment ago, so this only happens if the two disagree on capacity.
                    : ApprovalDecision.Reject(ApprovalRejection.Full,
                        "The server is full (" + Seats.Capacity + " of " + Seats.Capacity + " players).", decision.Payload);
            }

            return new Admission(decision, claim, expired);
        }

        /// <summary>
        /// Seats the host's own player. A host cannot refuse itself (NGO approves it whatever the answer), so only
        /// seating happens; the host's player still takes one of the seats.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="payload"/> is null.</exception>
        public Admission AdmitLocal(ulong clientId, ConnectionPayload payload, double now)
        {
            if (payload == null)
            {
                throw new ArgumentNullException(nameof(payload));
            }

            IReadOnlyList<Seat> expired = Seats.Expire(now);
            SeatClaim claim = Seats.Claim(clientId, payload, now, isHost: true);
            return new Admission(ApprovalDecision.Approve(payload), claim, expired);
        }
    }
}
