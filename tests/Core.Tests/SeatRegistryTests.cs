using System;
using System.Linq;
using Unity.Multiplayer.Core;
using NUnit.Framework;

namespace Unity.Multiplayer.Core.Tests
{
    [TestFixture]
    public class SeatRegistryTests
    {
        private static ConnectionPayload Player(string token, string name = null, PlayerColor? color = null)
        {
            return new ConnectionPayload("1.0", ProtocolHash.None, token, name ?? token, color ?? PlayerColor.White);
        }

        [Test]
        public void NewPlayers_TakeTheLowestFreeSeats()
        {
            var seats = new SeatRegistry(4, 30);

            SeatClaim a = seats.Claim(10, Player("a"), 0);
            SeatClaim b = seats.Claim(11, Player("b"), 0);

            Assert.That(a.Outcome, Is.EqualTo(SeatClaimOutcome.New));
            Assert.That(a.Seat.Index, Is.EqualTo(0));
            Assert.That(b.Seat.Index, Is.EqualTo(1));
            Assert.That(b.Seat.Status, Is.EqualTo(SeatStatus.Occupied));
            Assert.That(b.Seat.ClientId, Is.EqualTo(11UL));
            Assert.That(seats.Count, Is.EqualTo(2));
            Assert.That(seats.OccupiedCount, Is.EqualTo(2));
            Assert.That(seats.Seats.Select(s => s.PlayerToken), Is.EqualTo(new[] { "a", "b" }));
        }

        [Test]
        public void DroppedPlayer_ReconnectingWithinGrace_GetsTheSameSeat()
        {
            var seats = new SeatRegistry(4, 30);
            seats.Claim(10, Player("a"), 0);
            seats.Claim(11, Player("b"), 0);

            Seat held = seats.Release(11, 100);
            Assert.That(held.Status, Is.EqualTo(SeatStatus.Held));
            Assert.That(held.HeldUntil, Is.EqualTo(130));
            Assert.That(seats.HeldCount, Is.EqualTo(1));
            Assert.That(seats.TryGetByClient(11, out _), Is.False);

            // A newcomer does not get the held seat.
            Assert.That(seats.Claim(12, Player("c"), 110).Seat.Index, Is.EqualTo(2));

            SeatClaim back = seats.Claim(13, Player("b", "Bea", new PlayerColor(1, 2, 3)), 129);
            Assert.That(back.Outcome, Is.EqualTo(SeatClaimOutcome.Reclaimed));
            Assert.That(back.IsReturn, Is.True);
            Assert.That(back.Seat.Index, Is.EqualTo(1));
            Assert.That(back.Seat.ClientId, Is.EqualTo(13UL));
            Assert.That(back.Seat.Reconnects, Is.EqualTo(1));
            Assert.That(back.Seat.DisplayName, Is.EqualTo("Bea"), "the latest name and colour are kept");
            Assert.That(back.Seat.Color, Is.EqualTo(new PlayerColor(1, 2, 3)));
            Assert.That(back.ReplacedClientId, Is.Null);
        }

        [Test]
        public void HeldSeat_ExpiresAfterGrace_AndThePlayerStartsOver()
        {
            var seats = new SeatRegistry(2, 30);
            seats.Claim(10, Player("a"), 0);
            seats.Claim(11, Player("b"), 0);
            seats.Release(10, 0);

            Assert.That(seats.Expire(29.9), Is.Empty);
            var freed = seats.Expire(30);
            Assert.That(freed.Single().PlayerToken, Is.EqualTo("a"));
            Assert.That(freed.Single().Status, Is.EqualTo(SeatStatus.Released));
            Assert.That(seats.Count, Is.EqualTo(1));

            SeatClaim again = seats.Claim(12, Player("a"), 31);
            Assert.That(again.Outcome, Is.EqualTo(SeatClaimOutcome.New));
            Assert.That(again.Seat.Reconnects, Is.Zero);
        }

        [Test]
        public void Claim_AfterGraceRanOut_IsNewEvenWithoutAnExpireCall()
        {
            var seats = new SeatRegistry(4, 10);
            seats.Claim(10, Player("a"), 0);
            seats.Claim(11, Player("b"), 0);
            seats.Release(10, 0);

            SeatClaim late = seats.Claim(12, Player("a"), 10);
            Assert.That(late.Outcome, Is.EqualTo(SeatClaimOutcome.New));
            Assert.That(late.Seat.Index, Is.EqualTo(0), "the freed seat is the lowest free one again");
        }

        [Test]
        public void SameTokenOnANewConnection_TakesTheSeatOverAndNamesTheStaleOne()
        {
            var seats = new SeatRegistry(4, 30);
            seats.Claim(10, Player("a"), 0);

            SeatClaim takeover = seats.Claim(20, Player("a"), 5);

            Assert.That(takeover.Outcome, Is.EqualTo(SeatClaimOutcome.TookOver));
            Assert.That(takeover.Seat.Index, Is.EqualTo(0));
            Assert.That(takeover.Seat.ClientId, Is.EqualTo(20UL));
            Assert.That(takeover.ReplacedClientId, Is.EqualTo(10UL));
            Assert.That(seats.TryGetByClient(10, out _), Is.False);

            // The stale connection's later disconnect no longer touches the seat.
            Assert.That(seats.Release(10, 6), Is.Null);
            Assert.That(seats.TryGetByClient(20, out Seat seat), Is.True);
            Assert.That(seat.Status, Is.EqualTo(SeatStatus.Occupied));
        }

        [Test]
        public void SameTokenAsTheHosts_NeverTakesTheHostsSeatOver()
        {
            var seats = new SeatRegistry(4, 30);
            seats.Claim(0, Player("host"), 0, isHost: true);

            SeatClaim stray = seats.Claim(20, Player("host"), 5);

            Assert.That(stray.Outcome, Is.EqualTo(SeatClaimOutcome.Refused));
            Assert.That(stray.Seated, Is.False);
            Assert.That(stray.ReplacedClientId, Is.Null);

            // The host keeps its seat, untouched.
            Assert.That(seats.TryGetByClient(0, out Seat hostSeat), Is.True);
            Assert.That(hostSeat.Status, Is.EqualTo(SeatStatus.Occupied));
            Assert.That(hostSeat.ClientId, Is.EqualTo(0UL));
            Assert.That(seats.TryGetByClient(20, out _), Is.False);
        }

        [Test]
        public void HostClaimingItsOwnSeatAgain_ChangesNothing()
        {
            var seats = new SeatRegistry(4, 30);
            seats.Claim(0, Player("host"), 0, isHost: true);

            SeatClaim again = seats.Claim(0, Player("host", "Host", new PlayerColor(9, 9, 9)), 1, isHost: true);

            Assert.That(again.Outcome, Is.EqualTo(SeatClaimOutcome.Reclaimed));
            Assert.That(again.Seat.ClientId, Is.EqualTo(0UL));
        }

        [Test]
        public void HostsSeatHeldAfterItDrops_CanStillBeReclaimedByTheRealHost()
        {
            var seats = new SeatRegistry(4, 30);
            seats.Claim(0, Player("host"), 0, isHost: true);
            seats.Release(0, 1);

            SeatClaim back = seats.Claim(0, Player("host"), 2, isHost: true);

            Assert.That(back.Outcome, Is.EqualTo(SeatClaimOutcome.Reclaimed));
            Assert.That(back.Seat.ClientId, Is.EqualTo(0UL));
        }

        [Test]
        public void SameConnectionClaimingTwice_ChangesNothing()
        {
            var seats = new SeatRegistry(4, 30);
            seats.Claim(10, Player("a"), 0);
            SeatClaim again = seats.Claim(10, Player("a"), 1);
            Assert.That(again.Seat.Index, Is.EqualTo(0));
            Assert.That(again.ReplacedClientId, Is.Null);
            Assert.That(seats.Count, Is.EqualTo(1));
        }

        [Test]
        public void ConnectionSeatedForAnotherToken_Throws()
        {
            var seats = new SeatRegistry(4, 30);
            seats.Claim(10, Player("a"), 0);
            Assert.Throws<InvalidOperationException>(() => seats.Claim(10, Player("b"), 0));
        }

        [Test]
        public void HeldSeatsCountAsTaken_SoTheRegistryCanBeFull()
        {
            var seats = new SeatRegistry(2, 30);
            seats.Claim(10, Player("a"), 0);
            seats.Claim(11, Player("b"), 0);
            seats.Release(11, 0);

            Assert.That(seats.Claim(12, Player("c"), 1).Outcome, Is.EqualTo(SeatClaimOutcome.Full));
            Assert.That(seats.CountExcept("c"), Is.EqualTo(2));
            Assert.That(seats.CountExcept("b"), Is.EqualTo(1), "a returning player does not compete with its own seat");
            Assert.That(seats.Claim(13, Player("b"), 1).Outcome, Is.EqualTo(SeatClaimOutcome.Reclaimed));
        }

        [Test]
        public void ZeroGrace_FreesTheSeatOnRelease()
        {
            var seats = new SeatRegistry(2, 0);
            seats.Claim(10, Player("a"), 0);
            Seat released = seats.Release(10, 0);
            Assert.That(released.Status, Is.EqualTo(SeatStatus.Released));
            Assert.That(seats.Count, Is.Zero);
        }

        [Test]
        public void Free_RemovesAtOnceByClientOrToken()
        {
            var seats = new SeatRegistry(4, 30);
            seats.Claim(10, Player("a"), 0);
            seats.Claim(11, Player("b"), 0);
            seats.Release(11, 0);

            Assert.That(seats.Free(10).PlayerToken, Is.EqualTo("a"));
            Assert.That(seats.Free("b").Index, Is.EqualTo(1));
            Assert.That(seats.Free("b"), Is.Null);
            Assert.That(seats.Free(99UL), Is.Null);
            Assert.That(seats.Count, Is.Zero);
        }

        [Test]
        public void Lookups_ByTokenAndIndex()
        {
            var seats = new SeatRegistry(4, 30);
            seats.Claim(10, Player("a"), 0);
            seats.Claim(11, Player("b"), 0);
            seats.Release(10, 0);

            Assert.That(seats.TryGetByToken("a", out Seat a), Is.True);
            Assert.That(a.Status, Is.EqualTo(SeatStatus.Held));
            Assert.That(seats.TryGetByIndex(1, out Seat one), Is.True);
            Assert.That(one.PlayerToken, Is.EqualTo("b"));
            Assert.That(seats.TryGetByIndex(2, out _), Is.False);
            Assert.That(seats.TryGetByToken(null, out _), Is.False);
        }

        [Test]
        public void Clear_EmptiesEverything()
        {
            var seats = new SeatRegistry(4, 30);
            seats.Claim(10, Player("a"), 0);
            seats.Clear();
            Assert.That(seats.Count, Is.Zero);
            Assert.That(seats.Claim(11, Player("b"), 0).Seat.Index, Is.Zero);
        }

        [Test]
        public void BadArguments_Throw()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new SeatRegistry(0, 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => new SeatRegistry(1, -1));
            Assert.Throws<ArgumentOutOfRangeException>(() => new SeatRegistry(1, double.NaN));
            Assert.Throws<ArgumentNullException>(() => new SeatRegistry(1, 1).Claim(1, null, 0));
        }
    }
}
