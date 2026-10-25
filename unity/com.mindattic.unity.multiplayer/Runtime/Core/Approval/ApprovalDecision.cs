using System;

namespace Unity.Multiplayer.Core
{
    /// <summary>Why the server turned a connection request down.</summary>
    public enum ApprovalRejection
    {
        /// <summary>Not rejected: the connection was approved.</summary>
        None = 0,

        /// <summary>The client runs a different game version than the server.</summary>
        VersionMismatch = 1,

        /// <summary>Same game version, but the RPC / synced-field protocol hash differs: the builds do not match.</summary>
        ProtocolMismatch = 2,

        /// <summary>Every seat is taken.</summary>
        Full = 3,

        /// <summary>The player token is on the server's ban list.</summary>
        Banned = 4,

        /// <summary>The connection data is missing, malformed or carries invalid fields.</summary>
        BadPayload = 5,

        /// <summary>The server has a password and the client gave none or a different one.</summary>
        WrongPassword = 6,

        /// <summary>The player token belongs to a seat that is occupied by someone else who is still connected
        /// (the host's own seat, which is never given up) rather than a dropped player's held seat.</summary>
        TokenInUse = 7,
    }

    /// <summary>
    /// The server's answer to a connection request: approved with the decoded payload, or rejected with an
    /// <see cref="ApprovalRejection"/> and a sentence the client can show the player.
    /// </summary>
    public readonly struct ApprovalDecision
    {
        private const char WireSeparator = '|';

        private ApprovalDecision(ApprovalRejection rejection, string message, ConnectionPayload payload)
        {
            Rejection = rejection;
            Message = message ?? string.Empty;
            Payload = payload;
        }

        /// <summary>True when the connection may proceed.</summary>
        public bool Approved
        {
            get { return Rejection == ApprovalRejection.None; }
        }

        /// <summary>Why the connection was rejected, or <see cref="ApprovalRejection.None"/>.</summary>
        public ApprovalRejection Rejection { get; }

        /// <summary>A sentence for the player; empty when approved.</summary>
        public string Message { get; }

        /// <summary>The decoded payload when it could be read (always when approved; null for most <see cref="ApprovalRejection.BadPayload"/> cases).</summary>
        public ConnectionPayload Payload { get; }

        /// <summary>An approval carrying the client's payload.</summary>
        /// <exception cref="ArgumentNullException"><paramref name="payload"/> is null.</exception>
        public static ApprovalDecision Approve(ConnectionPayload payload)
        {
            if (payload == null)
            {
                throw new ArgumentNullException(nameof(payload));
            }

            return new ApprovalDecision(ApprovalRejection.None, string.Empty, payload);
        }

        /// <summary>A rejection with its reason and message.</summary>
        /// <exception cref="ArgumentException"><paramref name="rejection"/> is <see cref="ApprovalRejection.None"/> or undefined.</exception>
        public static ApprovalDecision Reject(ApprovalRejection rejection, string message, ConnectionPayload payload = null)
        {
            if (rejection == ApprovalRejection.None || !Enum.IsDefined(typeof(ApprovalRejection), rejection))
            {
                throw new ArgumentException("A rejection needs a reason other than None; got " + rejection + ".", nameof(rejection));
            }

            return new ApprovalDecision(rejection, message, payload);
        }

        /// <summary>
        /// The reason string the transport sends back with a refused connection: <c>"&lt;Rejection&gt;|&lt;message&gt;"</c>,
        /// or empty when approved. <see cref="TryParseWireReason"/> reads it on the client.
        /// </summary>
        public string ToWireReason()
        {
            return Approved ? string.Empty : Rejection.ToString() + WireSeparator + Message;
        }

        /// <summary>
        /// Reads a reason string written by <see cref="ToWireReason"/>. False for empty or foreign text, in which
        /// case <paramref name="message"/> holds the raw text so it can still be shown.
        /// </summary>
        public static bool TryParseWireReason(string reason, out ApprovalRejection rejection, out string message)
        {
            rejection = ApprovalRejection.None;
            message = reason ?? string.Empty;
            if (string.IsNullOrEmpty(reason))
            {
                return false;
            }

            int separator = reason.IndexOf(WireSeparator);
            if (separator <= 0)
            {
                return false;
            }

            ApprovalRejection parsed;
            string name = reason.Substring(0, separator);
            if (!Enum.TryParse(name, false, out parsed) || parsed == ApprovalRejection.None
                || !Enum.IsDefined(typeof(ApprovalRejection), parsed) || name != parsed.ToString())
            {
                return false;
            }

            rejection = parsed;
            message = reason.Substring(separator + 1);
            return true;
        }

        /// <inheritdoc />
        public override string ToString()
        {
            return Approved ? "Approved " + Payload : "Rejected " + Rejection + ": " + Message;
        }
    }
}
