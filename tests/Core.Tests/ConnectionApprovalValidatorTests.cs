using System;
using Unity.Multiplayer.Core;
using NUnit.Framework;

namespace Unity.Multiplayer.Core.Tests
{
    [TestFixture]
    public class ConnectionApprovalValidatorTests
    {
        private static readonly ProtocolHash ServerHash = new ProtocolHash(0x0102030405060708UL);

        private static ConnectionApprovalValidator Server(int maxPlayers = 4, IBanList bans = null)
        {
            return new ConnectionApprovalValidator("1.2.0", ServerHash, maxPlayers, bans);
        }

        private static byte[] Encode(ConnectionPayload payload)
        {
            return ConnectionPayloadCodec.Encode(payload);
        }

        [Test]
        public void MatchingClient_IsApprovedWithItsPayload()
        {
            ApprovalDecision decision = Server().Validate(Encode(ConnectionPayloadCodecTests.Sample()), 1);

            Assert.That(decision.Approved, Is.True, decision.Message);
            Assert.That(decision.Rejection, Is.EqualTo(ApprovalRejection.None));
            Assert.That(decision.Payload.DisplayName, Is.EqualTo("Ryan"));
            Assert.That(decision.Message, Is.Empty);
            Assert.That(decision.ToWireReason(), Is.Empty);
        }

        [Test]
        public void VersionCompare_IgnoresSurroundingWhitespaceOnly()
        {
            var server = new ConnectionApprovalValidator(" 1.2.0 ", ServerHash, 4);
            Assert.That(server.Validate(ConnectionPayloadCodecTests.Sample(version: "1.2.0 "), 0).Approved, Is.True);
            Assert.That(server.Validate(ConnectionPayloadCodecTests.Sample(version: "1.2.0b"), 0).Rejection, Is.EqualTo(ApprovalRejection.VersionMismatch));
        }

        [Test]
        public void DifferentVersion_IsVersionMismatchNamingBothVersions()
        {
            ApprovalDecision decision = Server().Validate(Encode(ConnectionPayloadCodecTests.Sample(version: "1.1.0")), 0);

            Assert.That(decision.Rejection, Is.EqualTo(ApprovalRejection.VersionMismatch));
            Assert.That(decision.Message, Does.Contain("1.2.0").And.Contain("1.1.0"));
            Assert.That(decision.Payload, Is.Not.Null);
        }

        [Test]
        public void DifferentProtocolHash_IsProtocolMismatch()
        {
            var payload = new ConnectionPayload("1.2.0", new ProtocolHash(99), "tok", "Ryan", PlayerColor.White);

            ApprovalDecision decision = Server().Validate(Encode(payload), 0);

            Assert.That(decision.Rejection, Is.EqualTo(ApprovalRejection.ProtocolMismatch));
            Assert.That(decision.Message, Does.Contain(ServerHash.ToString()));
        }

        [Test]
        public void BannedToken_IsBanned()
        {
            var bans = new InMemoryBanList(new[] { "3f2a9c1e-77b4-4d0e-9a51-0c6d2b8e4f10" });
            ApprovalDecision decision = Server(bans: bans).Validate(Encode(ConnectionPayloadCodecTests.Sample()), 0);
            Assert.That(decision.Rejection, Is.EqualTo(ApprovalRejection.Banned));
        }

        [Test]
        public void NoFreeSeat_IsFull()
        {
            ApprovalDecision decision = Server(maxPlayers: 4).Validate(Encode(ConnectionPayloadCodecTests.Sample()), 4);
            Assert.That(decision.Rejection, Is.EqualTo(ApprovalRejection.Full));
            Assert.That(decision.Message, Does.Contain("4 of 4"));
        }

        [Test]
        public void LastSeat_IsApproved()
        {
            Assert.That(Server(maxPlayers: 4).Validate(Encode(ConnectionPayloadCodecTests.Sample()), 3).Approved, Is.True);
        }

        [Test]
        public void GarbageBytes_AreBadPayload()
        {
            ApprovalDecision decision = Server().Validate(new byte[] { 1, 2, 3, 4 }, 0);
            Assert.That(decision.Rejection, Is.EqualTo(ApprovalRejection.BadPayload));
            Assert.That(decision.Payload, Is.Null);
            Assert.That(decision.Message, Is.Not.Empty);
        }

        [Test]
        public void NoBytes_AreBadPayload()
        {
            Assert.That(Server().Validate((byte[])null, 0).Rejection, Is.EqualTo(ApprovalRejection.BadPayload));
            Assert.That(Server().Validate((ConnectionPayload)null, 0).Rejection, Is.EqualTo(ApprovalRejection.BadPayload));
        }

        [Test]
        public void InvalidDecodedPayloadObject_IsBadPayload()
        {
            ApprovalDecision decision = Server().Validate(ConnectionPayloadCodecTests.Sample(name: ""), 0);
            Assert.That(decision.Rejection, Is.EqualTo(ApprovalRejection.BadPayload));
            Assert.That(decision.Message, Does.Contain("DisplayName"));
        }

        [Test]
        public void NewerFormatFromNewerGame_IsVersionMismatchNotBadPayload()
        {
            byte[] bytes = Encode(ConnectionPayloadCodecTests.Sample(version: "2.0.0"));
            bytes[2] = 3;

            ApprovalDecision decision = Server().Validate(bytes, 0);

            Assert.That(decision.Rejection, Is.EqualTo(ApprovalRejection.VersionMismatch));
            Assert.That(decision.Message, Does.Contain("2.0.0"));
        }

        [Test]
        public void NewerFormatWithSameGameVersion_IsBadPayload()
        {
            byte[] bytes = Encode(ConnectionPayloadCodecTests.Sample(version: "1.2.0"));
            bytes[2] = 3;
            Assert.That(Server().Validate(bytes, 0).Rejection, Is.EqualTo(ApprovalRejection.BadPayload));
        }

        [Test]
        public void ChecksRunInOrder_VersionBeforeProtocolBeforeBanBeforeFull()
        {
            var bans = new InMemoryBanList(new[] { "tok" });
            var server = Server(maxPlayers: 1, bans: bans);

            var everythingWrong = new ConnectionPayload("0.9", new ProtocolHash(5), "tok", "Ryan", PlayerColor.White);
            var protocolBanFull = new ConnectionPayload("1.2.0", new ProtocolHash(5), "tok", "Ryan", PlayerColor.White);
            var banFull = new ConnectionPayload("1.2.0", ServerHash, "tok", "Ryan", PlayerColor.White);

            Assert.That(server.Validate(everythingWrong, 1).Rejection, Is.EqualTo(ApprovalRejection.VersionMismatch));
            Assert.That(server.Validate(protocolBanFull, 1).Rejection, Is.EqualTo(ApprovalRejection.ProtocolMismatch));
            Assert.That(server.Validate(banFull, 1).Rejection, Is.EqualTo(ApprovalRejection.Banned));
        }

        [Test]
        public void FromConfig_UsesGameVersionAndMaxPlayers()
        {
            var config = new NetConfig { GameVersion = "1.2.0", MaxPlayers = 2 };
            var server = ConnectionApprovalValidator.FromConfig(config, ServerHash);

            Assert.That(server.GameVersion, Is.EqualTo("1.2.0"));
            Assert.That(server.MaxPlayers, Is.EqualTo(2));
            Assert.That(server.Validate(ConnectionPayloadCodecTests.Sample(), 2).Rejection, Is.EqualTo(ApprovalRejection.Full));
            Assert.Throws<ArgumentNullException>(() => ConnectionApprovalValidator.FromConfig(null, ServerHash));
        }

        [Test]
        public void Constructor_RejectsEmptyVersionAndZeroSeats()
        {
            Assert.Throws<ArgumentException>(() => new ConnectionApprovalValidator(" ", ServerHash, 4));
            Assert.Throws<ArgumentException>(() => new ConnectionApprovalValidator("1", ServerHash, 0));
        }

        [TestCase(ApprovalRejection.VersionMismatch)]
        [TestCase(ApprovalRejection.ProtocolMismatch)]
        [TestCase(ApprovalRejection.Full)]
        [TestCase(ApprovalRejection.Banned)]
        [TestCase(ApprovalRejection.BadPayload)]
        [TestCase(ApprovalRejection.WrongPassword)]
        [TestCase(ApprovalRejection.TokenInUse)]
        public void WireReason_RoundTrips(ApprovalRejection rejection)
        {
            ApprovalDecision decision = ApprovalDecision.Reject(rejection, "Some text | with a bar.");

            ApprovalRejection parsed;
            string message;
            Assert.That(ApprovalDecision.TryParseWireReason(decision.ToWireReason(), out parsed, out message), Is.True);
            Assert.That(parsed, Is.EqualTo(rejection));
            Assert.That(message, Is.EqualTo("Some text | with a bar."));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("Kicked by admin")]
        [TestCase("None|nope")]
        [TestCase("3|numeric")]
        [TestCase("Nonsense|text")]
        public void WireReason_ForeignTextIsNotParsed(string reason)
        {
            ApprovalRejection parsed;
            string message;
            Assert.That(ApprovalDecision.TryParseWireReason(reason, out parsed, out message), Is.False);
            Assert.That(parsed, Is.EqualTo(ApprovalRejection.None));
            Assert.That(message, Is.EqualTo(reason ?? string.Empty));
        }

        [Test]
        public void Reject_RequiresAReason()
        {
            Assert.Throws<ArgumentException>(() => ApprovalDecision.Reject(ApprovalRejection.None, "x"));
            Assert.Throws<ArgumentException>(() => ApprovalDecision.Reject((ApprovalRejection)42, "x"));
            Assert.Throws<ArgumentNullException>(() => ApprovalDecision.Approve(null));
        }

        [Test]
        public void InMemoryBanList_BansAndUnbans()
        {
            var bans = new InMemoryBanList();
            Assert.That(bans.Ban("a"), Is.True);
            Assert.That(bans.Ban("a"), Is.False);
            Assert.That(bans.Ban(""), Is.False);
            Assert.That(bans.IsBanned("a"), Is.True);
            Assert.That(bans.IsBanned("A"), Is.False);
            Assert.That(bans.IsBanned(null), Is.False);
            Assert.That(bans.Count, Is.EqualTo(1));
            Assert.That(bans.Unban("a"), Is.True);
            Assert.That(bans.IsBanned("a"), Is.False);
        }
    }
}
