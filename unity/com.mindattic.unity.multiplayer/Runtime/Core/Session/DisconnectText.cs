using System;

namespace Unity.Multiplayer.Core
{
    /// <summary>
    /// What to tell a player about why their session ended: a short title, a plain sentence, and the server's own detail
    /// when it said something more specific. Made by <see cref="DisconnectText.For"/>; a front end shows it in a dialog.
    /// </summary>
    public readonly struct DisconnectMessage
    {
        /// <summary>Creates a message.</summary>
        public DisconnectMessage(string title, string body, string detail)
        {
            Title = title ?? string.Empty;
            Body = body ?? string.Empty;
            Detail = detail ?? string.Empty;
        }

        /// <summary>A short heading, for example "Game full".</summary>
        public string Title { get; }

        /// <summary>One or two sentences in plain words: what happened and what to do.</summary>
        public string Body { get; }

        /// <summary>The server's own sentence when it adds something (a kick reason, the versions); empty otherwise.</summary>
        public string Detail { get; }

        /// <inheritdoc />
        public override string ToString()
        {
            return Detail.Length == 0 ? Title + ": " + Body : Title + ": " + Body + " (" + Detail + ")";
        }
    }

    /// <summary>
    /// The one place a <see cref="Disconnection"/> becomes words a player understands, for every
    /// <see cref="DisconnectReason"/> and every <see cref="ApprovalRejection"/>. Engine-free, so any front end (the
    /// package's own, or a game's) shows the same text.
    /// </summary>
    public static class DisconnectText
    {
        /// <summary>
        /// True when the player should be told why the session ended: not for their own leave, not when nothing ended,
        /// and not for <see cref="DisconnectReason.ClientLeft"/> (a server-side record of someone else leaving).
        /// </summary>
        public static bool ShouldShow(Disconnection disconnection)
        {
            switch (disconnection.Reason)
            {
                case DisconnectReason.None:
                case DisconnectReason.LocalLeave:
                case DisconnectReason.ClientLeft:
                    return false;
                default:
                    return true;
            }
        }

        /// <summary>The title, sentence and server detail for <paramref name="disconnection"/>.</summary>
        public static DisconnectMessage For(Disconnection disconnection)
        {
            string server = disconnection.Message.Trim();
            switch (disconnection.Reason)
            {
                case DisconnectReason.None:
                    return new DisconnectMessage("Connected", "The session has not ended.", null);
                case DisconnectReason.LocalLeave:
                    return new DisconnectMessage("Left the game", "You left the game.", null);
                case DisconnectReason.ServerShutdown:
                    return new DisconnectMessage("Game ended", "The host closed the game.", null);
                case DisconnectReason.ConnectFailed:
                    return new DisconnectMessage("Couldn't connect",
                        "No game answered. Check the address or code, and that the host is still running.", null);
                case DisconnectReason.TimedOut:
                    return new DisconnectMessage("Connection lost",
                        "The game stopped responding. Check your internet connection and try again.", null);
                case DisconnectReason.Kicked:
                    return Kicked(server);
                case DisconnectReason.TransportFailure:
                    return new DisconnectMessage("Network error",
                        "The network connection failed. Check your connection and try again.", server);
                case DisconnectReason.ClientLeft:
                    return new DisconnectMessage("Player left", "The player closed their game.", null);
                case DisconnectReason.Rejected:
                    return Rejected(disconnection.Rejection, server);
                default:
                    return new DisconnectMessage("Disconnected", "The game ended.", server);
            }
        }

        /// <summary>The title and sentence for a refused connection, with the server's sentence as the detail.</summary>
        public static DisconnectMessage Rejected(ApprovalRejection rejection, string serverMessage)
        {
            string detail = serverMessage ?? string.Empty;
            switch (rejection)
            {
                case ApprovalRejection.VersionMismatch:
                    return new DisconnectMessage("Different version",
                        "Your game is a different version from the host's. Update both to the same version to play together.", detail);
                case ApprovalRejection.ProtocolMismatch:
                    return new DisconnectMessage("Different build",
                        "Your game and the host's were built from different code. Install the same build on both.", null);
                case ApprovalRejection.Full:
                    return new DisconnectMessage("Game full", "Every seat in this game is taken. Try again later.", detail);
                case ApprovalRejection.Banned:
                    return new DisconnectMessage("Can't join", "You are banned from this game.", null);
                case ApprovalRejection.WrongPassword:
                    return new DisconnectMessage("Wrong password",
                        "This game needs a password, and the one given did not match.", detail);
                case ApprovalRejection.BadPayload:
                    return new DisconnectMessage("Couldn't join",
                        "The host could not read your join request. Update both games to the same version.", detail);
                case ApprovalRejection.TokenInUse:
                    return new DisconnectMessage("Already connected",
                        "That player is already connected to this game. If this is a second copy of the game, sign in as a different player.", detail);
                default:
                    return new DisconnectMessage("Couldn't join", "The host turned the connection down.", detail);
            }
        }

        private static DisconnectMessage Kicked(string reason)
        {
            // NGO's own "removed" wording and an empty reason add nothing to the sentence.
            bool generic = reason.Length == 0 || string.Equals(reason, "You were removed from the server.", StringComparison.Ordinal);
            return new DisconnectMessage("Removed from the game", "The host removed you from the game.", generic ? null : reason);
        }
    }
}
