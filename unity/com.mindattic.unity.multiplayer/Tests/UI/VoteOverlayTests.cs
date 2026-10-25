using System.Collections;
using System.Linq;
using MindAttic.Components.Unity;
using NUnit.Framework;
using Unity.Multiplayer.Core;
using Unity.Multiplayer.Testing;
using Unity.Multiplayer.UI.Testing;
using UnityEngine;
using UnityEngine.TestTools;

namespace Unity.Multiplayer.UI.Tests
{
    /// <summary>
    /// UM-T54: the ready-made vote overlay over a real connection. A host and a client share a <see cref="VoteBoard"/>;
    /// the client's overlay shows the host's vote, its Yes button passes it, it hides and toasts the result; with a
    /// <see cref="NetFrontEnd"/> in game, the overlay wires itself.
    /// </summary>
    public class VoteOverlayTests : NetSessionTest
    {
        private VoteBoard _onHost;
        private VoteBoard _onClient;
        private int _resets;
        private GameObject _frontEndObject;

        public VoteOverlayTests() : base(Topology.Host, clients: 1)
        {
        }

        protected override string GameVersion => "um-t54-ui";

        protected override void OnSessionsCreated()
        {
            _resets = 0;
            Server.Identity = new PlayerIdentity("host-token", "Ann", PlayerColor.White);
            Client.Identity = new PlayerIdentity("client-token", "Bob", PlayerColor.White);
            foreach (NetSession session in Sessions)
            {
                VoteBoard.RegisterPrefab(session);
            }
        }

        protected override IEnumerator OnSessionsTearDown()
        {
            if (_frontEndObject != null)
            {
                Object.Destroy(_frontEndObject);
                _frontEndObject = null;
            }

            foreach (VoteOverlay overlay in Object.FindObjectsByType<VoteOverlay>(FindObjectsSortMode.None))
            {
                overlay.Close();
            }

            FrontEndTesting.ForceMenus = null;
            VoteOverlay.AutoWire = true;
            FrontEndTesting.CleanUpUi();
            yield return null;
        }

        private IEnumerator StartBoard()
        {
            yield return StartAndJoinAll();
            _onHost = VoteBoard.Spawn(Server).AddKind(new VoteKind("reset", "Reset the level?") { Description = _ => "Everything goes back to the start", Passed = _ => _resets++ });
            Awaiting<VoteBoard> copy = WaitForCopy(Client, _onHost);
            yield return copy;
            _onClient = copy.Result;
        }

        /// <summary>
        /// UM-T78: spawns the VoteBoard on the host before the client joins, so the client syncs the already-spawned board
        /// as part of its own connection handshake, while its session is still Connecting (not yet live: <c>IsClient</c> is
        /// false). Fails the test outright if that race is no longer reproduced, so a later change can't silently stop
        /// covering it.
        /// </summary>
        private IEnumerator StartBoardBeforeClientJoins()
        {
            yield return StartServer();
            _onHost = VoteBoard.Spawn(Server).AddKind(new VoteKind("reset", "Reset the level?") { Description = _ => "Everything goes back to the start", Passed = _ => _resets++ });

            VoteBoard spawnedOnClient = null;
            bool clientWasLiveAtSpawn = true;
            System.Action<VoteBoard> onSpawned = board =>
            {
                if (board.Session == Client)
                {
                    spawnedOnClient = board;
                    clientWasLiveAtSpawn = Client.IsLive;
                }
            };

            VoteBoard.Spawned += onSpawned;
            try
            {
                yield return Join(Client);
            }
            finally
            {
                VoteBoard.Spawned -= onSpawned;
            }

            Assert.That(spawnedOnClient, Is.Not.Null, "the VoteBoard already on the host was never synced to the joining client");
            Assert.That(clientWasLiveAtSpawn, Is.False,
                "the client's session was already live when its VoteBoard copy spawned; this no longer reproduces UM-T78's race");
            _onClient = spawnedOnClient;
        }

        /// <summary>A client front end with menus forced on, bound to <see cref="NetSessionTest.Client"/>, not yet started.</summary>
        private NetFrontEnd CreateClientFrontEnd()
        {
            FrontEndTesting.ForceMenus = true;
            _frontEndObject = new GameObject("Client front end");
            _frontEndObject.SetActive(false);
            NetFrontEnd frontEnd = _frontEndObject.AddComponent<NetFrontEnd>();
            FrontEndTesting.Bind(frontEnd, Client, Config);
            frontEnd.Identity = new FixedIdentitySource("Bob", Color.blue);
            return frontEnd;
        }

        [UnityTest]
        public IEnumerator The_overlay_shows_the_vote_and_its_Yes_button_passes_it()
        {
            yield return StartBoard();
            VoteOverlay overlay = VoteOverlay.Create(_onClient);
            Assert.That(VoteOverlay.For(_onClient), Is.SameAs(overlay));
            Assert.That(VoteOverlay.Create(_onClient), Is.SameAs(overlay), "one overlay per board");
            yield return null;
            Assert.That(overlay.Panel.Visible, Is.False, "hidden while no vote runs");

            _onHost.StartVote("reset");
            yield return WaitUntil(() => overlay.Panel.Visible, "the overlay never showed the vote");
            Assert.That(overlay.TitleText.Text, Is.EqualTo("Reset the level?"));
            Assert.That(overlay.DescriptionText.Text, Is.EqualTo("Everything goes back to the start"));
            Assert.That(overlay.TallyText.Text, Does.StartWith("Yes 1 · No 0 · needs 2 of 2"));
            Assert.That(overlay.StandingText.Text, Is.EqualTo("Your vote"));
            Assert.That(overlay.YesButton.Interactable && overlay.NoButton.Interactable, Is.True);
            Assert.That(FrontEndTesting.Buttons(overlay.Panel), Is.EqualTo(new[] { "Yes", "No" }));

            Assert.That(overlay.YesButton.Selected || overlay.NoButton.Selected, Is.False, "nothing picked before voting");
            FrontEndTesting.Click(overlay.Panel, "Yes");
            yield return WaitUntil(() => _resets == 1, "the client's Yes never passed the vote");
            yield return WaitUntil(() => !overlay.Panel.Visible, "the overlay never hid");
            yield return WaitUntil(() => MaToast.OnScreen.Any(t => t != null && t.Message == "Vote passed: Reset the level"),
                "no toast said the vote passed");
        }

        [UnityTest]
        public IEnumerator The_chosen_answer_is_marked_Selected_and_cleared_when_the_vote_ends()
        {
            yield return StartBoard();
            VoteOverlay overlay = VoteOverlay.Create(_onClient);
            yield return null;

            _onClient.StartVote("reset");
            yield return WaitUntil(() => overlay.Panel.Visible, "the overlay never showed the vote");
            yield return WaitUntil(() => overlay.YesButton.Selected, "the starter's Yes was never marked Selected");
            Assert.That(overlay.NoButton.Selected, Is.False, "only the chosen answer is picked");
            Assert.That(overlay.YesButton.Interactable, Is.False, "and it can't be changed");

            _onHost.CastVote(true);
            yield return WaitUntil(() => !overlay.Panel.Visible, "the overlay never hid");
            yield return null;
            Assert.That(overlay.YesButton.Selected || overlay.NoButton.Selected, Is.False, "the pick is cleared when the vote ends");
        }

        [UnityTest]
        public IEnumerator A_refused_start_is_a_toast()
        {
            yield return StartBoard();
            VoteOverlay.Create(_onClient);
            _onClient.StartVote("nope");
            yield return WaitUntil(() => MaToast.OnScreen.Any(t => t != null && t.Message != null && t.Message.Contains("isn't available")),
                "no toast said why");
        }

        [UnityTest]
        public IEnumerator With_a_front_end_in_game_the_overlay_wires_itself()
        {
            NetFrontEnd frontEnd = CreateClientFrontEnd();
            _frontEndObject.SetActive(true);
            yield return null;
            Assert.That(frontEnd.HasMenus, Is.True);

            yield return StartBoard();
            yield return WaitUntil(() => frontEnd.State == FrontEndState.InGame, () => "the front end never went in game (" + frontEnd.State + ")");
            yield return WaitUntil(() => VoteOverlay.For(_onClient) != null, "no overlay was wired for the client's board");
            VoteOverlay overlay = VoteOverlay.For(_onClient);

            _onHost.StartVote("reset");
            yield return WaitUntil(() => overlay.Panel.Visible, "the wired overlay never showed the vote");
            frontEnd.MenusVisible = true;
            overlay.ShowWhen = () => false;
            yield return null;
            Assert.That(overlay.Panel.Visible, Is.False, "ShowWhen can hide it");
        }

        [UnityTest]
        public IEnumerator Without_a_front_end_nothing_is_wired()
        {
            yield return StartBoard();
            yield return null;
            Assert.That(VoteOverlay.For(_onClient), Is.Null, "a game without NetFrontEnd builds its own UI, or calls Create");
        }

        /// <summary>
        /// UM-T78: a client joining a game that already has a running VoteBoard (the normal case for a second human joining
        /// a host) syncs that board while its own session is still Connecting, not yet live. The overlay must still wire
        /// itself then, not only for a board spawned after the client is already connected.
        /// </summary>
        [UnityTest]
        public IEnumerator A_board_that_spawns_before_the_client_is_live_still_gets_an_overlay()
        {
            NetFrontEnd frontEnd = CreateClientFrontEnd();
            _frontEndObject.SetActive(true);
            yield return null;
            Assert.That(frontEnd.HasMenus, Is.True);

            yield return StartBoardBeforeClientJoins();
            Assert.That(VoteOverlay.For(_onClient), Is.Not.Null,
                "no overlay was built for a board that spawned before the client's session went live (UM-T78)");
        }

        /// <summary>
        /// UM-T78: with the race in <see cref="A_board_that_spawns_before_the_client_is_live_still_gets_an_overlay"/>, the
        /// client's own overlay (wired despite not being live yet at Spawned) must still track the host's vote all the way
        /// through: show it, and toast how it ended.
        /// </summary>
        [UnityTest]
        public IEnumerator The_joining_clients_overlay_shows_the_hosts_vote_and_toasts_its_end()
        {
            NetFrontEnd frontEnd = CreateClientFrontEnd();
            _frontEndObject.SetActive(true);
            yield return null;

            yield return StartBoardBeforeClientJoins();
            yield return WaitUntil(() => frontEnd.State == FrontEndState.InGame, () => "the front end never went in game (" + frontEnd.State + ")");
            yield return WaitUntil(() => VoteOverlay.For(_onClient) != null,
                "no overlay was wired for a board that spawned before the client went live");
            VoteOverlay overlay = VoteOverlay.For(_onClient);

            _onHost.StartVote("reset");
            yield return WaitUntil(() => overlay.Panel.Visible, "the joining client's overlay never showed the host's vote");
            Assert.That(overlay.TitleText.Text, Is.EqualTo("Reset the level?"));

            _onClient.CastVote(true);
            yield return WaitUntil(() => _resets == 1, "the second Yes (from the client) never passed the vote");
            yield return WaitUntil(() => !overlay.Panel.Visible, "the joining client's overlay never hid once the vote ended");
            yield return WaitUntil(() => MaToast.OnScreen.Any(t => t != null && t.Message == "Vote passed: Reset the level"),
                "the joining client's overlay never toasted that the vote passed");
        }
    }
}
