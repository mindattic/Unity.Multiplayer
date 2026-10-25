using System;
using System.Linq;
using Unity.Multiplayer.Core;
using NUnit.Framework;

namespace Unity.Multiplayer.Core.Tests
{
    [TestFixture]
    public class ConnectionAdmissionTests
    {
        private static readonly ProtocolHash Hash = new ProtocolHash(0xABCDEF0123456789UL);

        private static ConnectionAdmission Server(int maxPlayers = 2, double grace = 30, IBanList bans = null)
        {
            return new ConnectionAdmission(new ConnectionApprovalValidator("1.0", Hash, maxPlayers, bans), new SeatRegistry(maxPlayers, grace));
        }

        private static byte[] Request(string token, string version = "1.0", ProtocolHash? hash = null)
        {
            return ConnectionPayloadCodec.Encode(new ConnectionPayload(version, hash ?? Hash, token, "P-" + token, PlayerColor.White));
        }

        [Test]
        public void Approved_IsSeated()
        {
            Admission admission = Server().Admit(5, Request("a"), 0);
            Assert.That(admission.Approved, Is.True, admission.ToString());
            Assert.That(admission.Claim.Outcome, Is.EqualTo(SeatClaimOutcome.New));
            Assert.That(admission.Claim.Seat.ClientId, Is.EqualTo(5UL));
        }

        [Test]
        public void VersionMismatch_IsRejectedWithAReasonTheClientCanParse_AndNotSeated()
        {
            ConnectionAdmission server = Server();
            Admission admission = server.Admit(5, Request("a", version: "0.9"), 0);

            Assert.That(admission.Approved, Is.False);
            Assert.That(admission.Decision.Rejection, Is.EqualTo(ApprovalRejection.VersionMismatch));
            Assert.That(admission.Claim.Seated, Is.False);
            Assert.That(server.Seats.Count, Is.Zero);

            Assert.That(ApprovalDecision.TryParseWireReason(admission.Decision.ToWireReason(), out ApprovalRejection rejection, out string message), Is.True);
            Assert.That(rejection, Is.EqualTo(ApprovalRejection.VersionMismatch));
            Assert.That(message, Does.Contain("1.0").And.Contain("0.9"));
        }

        [Test]
        public void ProtocolMismatch_IsRejected()
        {
            Admission admission = Server().Admit(5, Request("a", hash: new ProtocolHash(1)), 0);
            Assert.That(admission.Decision.Rejection, Is.EqualTo(ApprovalRejection.ProtocolMismatch));
        }

        [Test]
        public void Garbage_IsBadPayload()
        {
            Admission admission = Server().Admit(5, new byte[] { 1, 2, 3 }, 0);
            Assert.That(admission.Decision.Rejection, Is.EqualTo(ApprovalRejection.BadPayload));
        }

        [Test]
        public void Banned_IsRejected()
        {
            Admission admission = Server(bans: new InMemoryBanList(new[] { "a" })).Admit(5, Request("a"), 0);
            Assert.That(admission.Decision.Rejection, Is.EqualTo(ApprovalRejection.Banned));
        }

        [Test]
        public void FullServer_RejectsANewcomer_ButLetsAHeldPlayerBackIn()
        {
            ConnectionAdmission server = Server(maxPlayers: 2);
            server.Admit(1, Request("a"), 0);
            server.Admit(2, Request("b"), 0);
            server.Seats.Release(2, 10);

            Admission newcomer = server.Admit(3, Request("c"), 11);
            Assert.That(newcomer.Decision.Rejection, Is.EqualTo(ApprovalRejection.Full));

            Admission back = server.Admit(4, Request("b"), 12);
            Assert.That(back.Approved, Is.True, back.ToString());
            Assert.That(back.Claim.Outcome, Is.EqualTo(SeatClaimOutcome.Reclaimed));
            Assert.That(back.Claim.Seat.Index, Is.EqualTo(1));
        }

        [Test]
        public void FullServer_StillLetsAPlayerTakeOverItsOwnSeat()
        {
            ConnectionAdmission server = Server(maxPlayers: 1);
            server.Admit(1, Request("a"), 0);

            Admission takeover = server.Admit(2, Request("a"), 1);
            Assert.That(takeover.Approved, Is.True, takeover.ToString());
            Assert.That(takeover.Claim.Outcome, Is.EqualTo(SeatClaimOutcome.TookOver));
            Assert.That(takeover.Claim.ReplacedClientId, Is.EqualTo(1UL));
        }

        [Test]
        public void ExpiredSeats_AreFreedBeforeDeciding_AndReported()
        {
            ConnectionAdmission server = Server(maxPlayers: 1, grace: 5);
            server.Admit(1, Request("a"), 0);
            server.Seats.Release(1, 0);

            Admission newcomer = server.Admit(2, Request("b"), 6);
            Assert.That(newcomer.Approved, Is.True, newcomer.ToString());
            Assert.That(newcomer.Expired.Single().PlayerToken, Is.EqualTo("a"));
        }

        [Test]
        public void AdmitLocal_SeatsTheHostWithoutValidation()
        {
            ConnectionAdmission server = Server();
            Admission host = server.AdmitLocal(0, new ConnectionPayload("other", ProtocolHash.None, "host", "Host", PlayerColor.White), 0);
            Assert.That(host.Approved, Is.True);
            Assert.That(host.Claim.Seat.Index, Is.Zero);
            Assert.Throws<ArgumentNullException>(() => server.AdmitLocal(0, null, 0));
        }

        [Test]
        public void JoinWithTheHostsOwnToken_IsRefused_AndTheHostKeepsItsSeat()
        {
            ConnectionAdmission server = Server();
            server.AdmitLocal(0, new ConnectionPayload("1.0", Hash, "host", "Host", PlayerColor.White), 0);

            // A second copy of the same build, launched from the same install folder, remembers the host's token
            // (UM-T77) and joins by IP as a remote client.
            Admission stray = server.Admit(7, Request("host"), 1);

            Assert.That(stray.Approved, Is.False);
            Assert.That(stray.Decision.Rejection, Is.EqualTo(ApprovalRejection.TokenInUse));
            Assert.That(stray.Claim.Seated, Is.False);

            Assert.That(server.Seats.TryGetByClient(0, out Seat hostSeat), Is.True);
            Assert.That(hostSeat.Status, Is.EqualTo(SeatStatus.Occupied));
            Assert.That(server.Seats.TryGetByClient(7, out _), Is.False);

            Assert.That(ApprovalDecision.TryParseWireReason(stray.Decision.ToWireReason(), out ApprovalRejection rejection, out _), Is.True);
            Assert.That(rejection, Is.EqualTo(ApprovalRejection.TokenInUse));
        }

        [Test]
        public void NullArguments_Throw()
        {
            Assert.Throws<ArgumentNullException>(() => new ConnectionAdmission(null, new SeatRegistry(1, 1)));
            Assert.Throws<ArgumentNullException>(() => new ConnectionAdmission(new ConnectionApprovalValidator("1", Hash, 1), null));
        }
    }
}
