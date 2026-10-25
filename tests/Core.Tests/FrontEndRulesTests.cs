using System;
using Unity.Multiplayer.Core;
using NUnit.Framework;

namespace Unity.Multiplayer.Core.Tests
{
    /// <summary>
    /// UM-T48: the engine-free rules the drop-in front end follows: the words for every disconnect reason, when a lost
    /// connection is retried and how often, reading a typed host address, and when menus may appear at all.
    /// </summary>
    [TestFixture]
    public class FrontEndRulesTests
    {
        // ---- DisconnectText ----------------------------------------------------------------------------------------

        [TestCase(DisconnectReason.None, false)]
        [TestCase(DisconnectReason.LocalLeave, false)]
        [TestCase(DisconnectReason.ClientLeft, false)]
        [TestCase(DisconnectReason.ServerShutdown, true)]
        [TestCase(DisconnectReason.ConnectFailed, true)]
        [TestCase(DisconnectReason.TimedOut, true)]
        [TestCase(DisconnectReason.Rejected, true)]
        [TestCase(DisconnectReason.Kicked, true)]
        [TestCase(DisconnectReason.TransportFailure, true)]
        public void ShouldShow_OnlyWhenThePlayerDidNotChooseIt(DisconnectReason reason, bool shown)
        {
            Assert.That(DisconnectText.ShouldShow(new Disconnection(reason, "x")), Is.EqualTo(shown));
        }

        [Test]
        public void EveryReasonAndRejection_HasATitleAndASentence()
        {
            foreach (DisconnectReason reason in Enum.GetValues(typeof(DisconnectReason)))
            {
                foreach (ApprovalRejection rejection in Enum.GetValues(typeof(ApprovalRejection)))
                {
                    DisconnectMessage message = DisconnectText.For(new Disconnection(reason, "server said", rejection));
                    Assert.That(message.Title, Is.Not.Empty, reason + "/" + rejection);
                    Assert.That(message.Body, Does.EndWith("."), reason + "/" + rejection);
                }
            }
        }

        [Test]
        public void Rejections_EachGetTheirOwnTitle_AndKeepTheServersSpecifics()
        {
            DisconnectMessage version = DisconnectText.For(new Disconnection(DisconnectReason.Rejected,
                "The server runs game version 2; you have 1. Install the same version to join.", ApprovalRejection.VersionMismatch));
            Assert.That(version.Title, Is.EqualTo("Different version"));
            Assert.That(version.Body, Does.Contain("different version"));
            Assert.That(version.Detail, Does.Contain("version 2; you have 1"));

            Assert.That(DisconnectText.For(new Disconnection(DisconnectReason.Rejected, "h", ApprovalRejection.ProtocolMismatch)).Title, Is.EqualTo("Different build"));
            Assert.That(DisconnectText.For(new Disconnection(DisconnectReason.Rejected, "full", ApprovalRejection.Full)).Title, Is.EqualTo("Game full"));
            Assert.That(DisconnectText.For(new Disconnection(DisconnectReason.Rejected, "b", ApprovalRejection.Banned)).Body, Does.Contain("banned"));
            Assert.That(DisconnectText.For(new Disconnection(DisconnectReason.Rejected, "p", ApprovalRejection.WrongPassword)).Title, Is.EqualTo("Wrong password"));
            Assert.That(DisconnectText.For(new Disconnection(DisconnectReason.Rejected, "p", ApprovalRejection.BadPayload)).Title, Is.EqualTo("Couldn't join"));
            Assert.That(DisconnectText.For(new Disconnection(DisconnectReason.Rejected, "t", ApprovalRejection.TokenInUse)).Title, Is.EqualTo("Already connected"));
            Assert.That(DisconnectText.For(new Disconnection(DisconnectReason.Rejected, "p", ApprovalRejection.ProtocolMismatch)).Detail,
                Is.Empty, "a protocol hash means nothing to a player");
        }

        [Test]
        public void Kicked_ShowsTheHostsReason_ButNotTheGenericOne()
        {
            DisconnectMessage withReason = DisconnectText.For(new Disconnection(DisconnectReason.Kicked, "Spamming the chat"));
            Assert.That(withReason.Title, Is.EqualTo("Removed from the game"));
            Assert.That(withReason.Detail, Is.EqualTo("Spamming the chat"));

            Assert.That(DisconnectText.For(new Disconnection(DisconnectReason.Kicked, "You were removed from the server.")).Detail, Is.Empty);
            Assert.That(DisconnectText.For(new Disconnection(DisconnectReason.Kicked, null)).Detail, Is.Empty);
        }

        [Test]
        public void ServerShutdownAndTimeout_ReadAsPlainSentences()
        {
            Assert.That(DisconnectText.For(new Disconnection(DisconnectReason.ServerShutdown, "Disconnected due to host shutting down.")).ToString(),
                Is.EqualTo("Game ended: The host closed the game."));
            Assert.That(DisconnectText.For(new Disconnection(DisconnectReason.TimedOut, null)).Title, Is.EqualTo("Connection lost"));
        }

        // ---- ReconnectPolicy ---------------------------------------------------------------------------------------

        [TestCase(DisconnectReason.TimedOut, true)]
        [TestCase(DisconnectReason.TransportFailure, true)]
        [TestCase(DisconnectReason.ServerShutdown, false)]
        [TestCase(DisconnectReason.Kicked, false)]
        [TestCase(DisconnectReason.Rejected, false)]
        [TestCase(DisconnectReason.LocalLeave, false)]
        [TestCase(DisconnectReason.ConnectFailed, false)]
        public void ShouldReconnect_OnlyAfterALostConnection(DisconnectReason reason, bool expected)
        {
            var policy = new ReconnectPolicy();
            Assert.That(policy.ShouldReconnect(new Disconnection(reason, null), wasConnected: true), Is.EqualTo(expected));
            Assert.That(policy.ShouldReconnect(new Disconnection(reason, null), wasConnected: false), Is.False, "a join that never got in is not retried");
        }

        [Test]
        public void ShouldReconnect_OffWhenThereAreNoAttempts()
        {
            var policy = new ReconnectPolicy { MaxAttempts = 0 };
            Assert.That(policy.ShouldReconnect(new Disconnection(DisconnectReason.TimedOut, null), true), Is.False);
        }

        [Test]
        public void IsRetryable_KeepsTryingUnreachable_ButStopsOnARefusal()
        {
            Assert.That(ReconnectPolicy.IsRetryable(new Disconnection(DisconnectReason.ConnectFailed, null)), Is.True);
            Assert.That(ReconnectPolicy.IsRetryable(new Disconnection(DisconnectReason.TimedOut, null)), Is.True);
            Assert.That(ReconnectPolicy.IsRetryable(new Disconnection(DisconnectReason.Rejected, "full", ApprovalRejection.Full)), Is.False);
            Assert.That(ReconnectPolicy.IsRetryable(new Disconnection(DisconnectReason.Kicked, null)), Is.False);
        }

        [Test]
        public void DelayBefore_DoublesAndCaps()
        {
            var policy = new ReconnectPolicy { FirstDelaySeconds = 1, MaxDelaySeconds = 5 };
            Assert.That(policy.DelayBefore(1), Is.EqualTo(1));
            Assert.That(policy.DelayBefore(2), Is.EqualTo(2));
            Assert.That(policy.DelayBefore(3), Is.EqualTo(4));
            Assert.That(policy.DelayBefore(4), Is.EqualTo(5));
            Assert.That(policy.DelayBefore(50), Is.EqualTo(5));
            Assert.That(new ReconnectPolicy { FirstDelaySeconds = 0 }.DelayBefore(3), Is.EqualTo(0));
            Assert.Throws<ArgumentOutOfRangeException>(() => policy.DelayBefore(0));
        }

        // ---- HostAddress -------------------------------------------------------------------------------------------

        [TestCase("192.168.1.20", "192.168.1.20", 7777)]
        [TestCase("  192.168.1.20:47000 ", "192.168.1.20", 47000)]
        [TestCase("game.example.com:9000", "game.example.com", 9000)]
        [TestCase("localhost", "localhost", 7777)]
        [TestCase("::1", "::1", 7777)]
        [TestCase("[::1]", "::1", 7777)]
        [TestCase("[2001:db8::7]:7000", "2001:db8::7", 7000)]
        public void HostAddress_ReadsWhatPlayersType(string text, string host, int port)
        {
            Assert.That(HostAddress.TryParse(text, 7777, out HostAddress address, out string error), Is.True, error);
            Assert.That(address.Host, Is.EqualTo(host));
            Assert.That(address.Port, Is.EqualTo(port));
        }

        [TestCase("", "Type the host's address")]
        [TestCase("   ", "Type the host's address")]
        [TestCase(":7777", "no host")]
        [TestCase("1.2.3.4:", "port must be")]
        [TestCase("1.2.3.4:0", "port must be")]
        [TestCase("1.2.3.4:70000", "port must be")]
        [TestCase("1.2.3.4:abc", "port must be")]
        [TestCase("[::1", "no ']'")]
        [TestCase("[::1]7777", "after the ']'")]
        [TestCase("my host", "spaces")]
        public void HostAddress_ExplainsWhatIsWrong(string text, string expected)
        {
            Assert.That(HostAddress.TryParse(text, 7777, out _, out string error), Is.False);
            Assert.That(error, Does.Contain(expected));
        }

        [Test]
        public void HostAddress_ToString_BracketsIPv6()
        {
            Assert.That(new HostAddress("10.0.0.2", 7777).ToString(), Is.EqualTo("10.0.0.2:7777"));
            Assert.That(new HostAddress("::1", 7777).ToString(), Is.EqualTo("[::1]:7777"));
        }

        // ---- NetLaunch.ShowsMenus ----------------------------------------------------------------------------------

        [Test]
        public void ShowsMenus_NeverHeadless_NeverWhenTheCommandLinePickedARole()
        {
            NetCommandLineResult none = NetCommandLine.Apply(new NetConfig(), new string[0]);
            NetCommandLineResult bot = NetCommandLine.Apply(new NetConfig(), new[] { "-mode", "client", "-address", "10.0.0.2" });
            NetCommandLineResult server = NetCommandLine.Apply(new NetConfig(), new[] { "-mode", "server" });

            Assert.That(NetLaunch.ShowsMenus(none, headless: false), Is.True);
            Assert.That(NetLaunch.ShowsMenus(none, headless: true), Is.False, "a dedicated server or CI run");
            Assert.That(NetLaunch.ShowsMenus(bot, headless: false), Is.False, "a bot client");
            Assert.That(NetLaunch.ShowsMenus(server, headless: false), Is.False);
            Assert.Throws<ArgumentNullException>(() => NetLaunch.ShowsMenus(null, false));
        }
    }
}
