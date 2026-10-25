using System;
using System.Collections.Generic;
using MindAttic.Components.Unity;
using MindAttic.Components.Unity.Core;
using TMPro;
using Unity.Multiplayer.Core;
using UnityEngine;
using UnityEngine.UI;

namespace Unity.Multiplayer.UI
{
    /// <summary>
    /// The ready-made vote overlay (UM-T54), built from MindAttic Components and bound to any <c>VoteBoard</c>:
    /// while a vote runs, a panel at the top of the screen shows its question and description, the tally ("Yes 1 · No 0 ·
    /// needs 2 of 3 · 12 s"), a bar towards the quorum, where the local player stands, and Yes / No buttons; when it ends,
    /// a toast says how; a refused start is a toast too.
    /// </summary>
    /// <remarks>
    /// <para>With a <see cref="NetFrontEnd"/> that has menus, every board that spawns on a playing peer gets one by itself
    /// (<see cref="AutoWire"/>), shown only in game. Without a front end, call <see cref="Create"/> yourself. A headless
    /// peer (a dedicated server, a bot) never gets one.</para>
    /// <para>Its words come from Core's <see cref="VoteText"/>, so a game's own UI can say the same thing.</para>
    /// </remarks>
    public sealed class VoteOverlay : MonoBehaviour
    {
        /// <summary>The overlay canvas's default sorting order: above a game's HUD, below the front end's menus (500).</summary>
        public const int DefaultSortingOrder = 400;

        /// <summary>The panel's default distance from the top of the screen, in canvas units (clear of a toolbar).</summary>
        public const float DefaultTopOffset = 128f;

        private static readonly List<VoteOverlay> s_Overlays = new List<VoteOverlay>();

        private IVoteBoard _board;
        private Func<bool> _showWhen;
        private float _topOffset = DefaultTopOffset;

        /// <summary>
        /// True (the default) to give every <c>VoteBoard</c> that spawns on a peer with a <see cref="NetFrontEnd"/>
        /// showing menus its own overlay. Set false before the board spawns to build your own.
        /// </summary>
        public static bool AutoWire { get; set; } = true;

        /// <summary>Raised when an overlay is created (by hand or auto-wired), so a game can restyle or move it.</summary>
        public static event Action<VoteOverlay> Created;

        /// <summary>The board it shows and votes on.</summary>
        public IVoteBoard Board => _board;

        /// <summary>The overlay's own canvas.</summary>
        public Canvas Canvas { get; private set; }

        /// <summary>The panel; visible only while a vote runs (and <see cref="ShowWhen"/> allows).</summary>
        public MaPanel Panel { get; private set; }

        /// <summary>The question ("Switch to Level 3?").</summary>
        public MaText TitleText { get; private set; }

        /// <summary>The kind's optional second line; hidden when empty.</summary>
        public MaText DescriptionText { get; private set; }

        /// <summary>"Yes 1 · No 0 · needs 2 of 3 · 12 s".</summary>
        public MaText TallyText { get; private set; }

        /// <summary>Yes votes towards the quorum.</summary>
        public MaProgressBar QuorumBar { get; private set; }

        /// <summary>Where the local player stands ("Your vote", "You voted Yes").</summary>
        public MaText StandingText { get; private set; }

        /// <summary>Votes Yes; disabled once the local player has voted or is not a voter, and <see cref="MaButton.Selected"/> when Yes is their answer.</summary>
        public MaButton YesButton { get; private set; }

        /// <summary>Votes No; disabled once the local player has voted or is not a voter, and <see cref="MaButton.Selected"/> when No is their answer.</summary>
        public MaButton NoButton { get; private set; }

        /// <summary>True (the default) to toast how each vote ended and why a start was refused.</summary>
        public bool Toasts { get; set; } = true;

        /// <summary>
        /// When the panel may show at all (a vote must also be running). Default: in game when a <see cref="NetFrontEnd"/>
        /// exists (not over its menus), else always.
        /// </summary>
        public Func<bool> ShowWhen
        {
            get { return _showWhen ?? DefaultShowWhen; }
            set { _showWhen = value; }
        }

        /// <summary>The panel's distance from the top of the screen, in canvas units.</summary>
        public float TopOffset
        {
            get { return _topOffset; }
            set
            {
                _topOffset = value;
                if (Panel != null)
                {
                    Panel.RectTransform.anchoredPosition = new Vector2(0f, -value);
                }
            }
        }

        /// <summary>The overlay showing <paramref name="board"/>, or null when it has none.</summary>
        public static VoteOverlay For(IVoteBoard board)
        {
            foreach (VoteOverlay overlay in s_Overlays)
            {
                if (overlay != null && overlay._board == board)
                {
                    return overlay;
                }
            }

            return null;
        }

        /// <summary>Builds an overlay for <paramref name="board"/> on its own canvas (or returns the one it already has).</summary>
        /// <exception cref="ArgumentNullException"><paramref name="board"/> is null.</exception>
        public static VoteOverlay Create(IVoteBoard board, int sortingOrder = DefaultSortingOrder)
        {
            if (board == null)
            {
                throw new ArgumentNullException(nameof(board));
            }

            VoteOverlay existing = For(board);
            if (existing != null)
            {
                return existing;
            }

            Canvas canvas = MaCanvas.Create("Vote Overlay", sortingOrder);
            VoteOverlay overlay = canvas.gameObject.AddComponent<VoteOverlay>();
            overlay.Build(canvas, board);
            Created?.Invoke(overlay);
            return overlay;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void InstallAutoWire()
        {
            s_Overlays.Clear();
            VoteBoards.Spawned -= OnBoardSpawned;
            VoteBoards.Spawned += OnBoardSpawned;
            VoteBoards.Despawned -= OnBoardDespawned;
            VoteBoards.Despawned += OnBoardDespawned;
        }

        private static void OnBoardSpawned(IVoteBoard board)
        {
            if (!AutoWire || board == null)
            {
                return;
            }

            NetFrontEnd frontEnd = FrontEndOf(board);
            NetSession session = board.Session;
            if (frontEnd == null || !frontEnd.HasMenus || session == null)
            {
                return;
            }

            // Not IsClient: a VoteBoard the host already had spawns on a joining client during its initial
            // sync, before the session machine reaches Connected (IsClient needs IsLive). Skip only a peer with
            // no local player at all: a dedicated server or bot (serving without being the host), or a session
            // that is not active (already ended, or somehow never started).
            if (!session.IsActive || (session.IsServer && !session.IsHost))
            {
                return;
            }

            Create(board);
        }

        private static void OnBoardDespawned(IVoteBoard board)
        {
            VoteOverlay overlay = For(board);
            if (overlay != null)
            {
                overlay.Close();
            }
        }

        /// <summary>The front end driving <paramref name="board"/>'s session, else the scene's first.</summary>
        private static NetFrontEnd FrontEndOf(IVoteBoard board)
        {
            foreach (NetFrontEnd frontEnd in FindObjectsByType<NetFrontEnd>(FindObjectsSortMode.None))
            {
                if (frontEnd != null && frontEnd.Session != null && frontEnd.Session == board.Session)
                {
                    return frontEnd;
                }
            }

            return NetFrontEnd.Instance;
        }

        /// <summary>Removes the overlay and its canvas.</summary>
        public void Close()
        {
            if (this != null && gameObject != null)
            {
                Destroy(gameObject);
            }
        }

        private void Build(Canvas canvas, IVoteBoard board)
        {
            Canvas = canvas;
            _board = board;
            s_Overlays.Add(this);

            Panel = MaPanel.Create(canvas, PanelStyle.Surface, "Vote Panel");
            RectTransform rt = Panel.RectTransform;
            rt.anchorMin = new Vector2(0.5f, 1f);
            rt.anchorMax = new Vector2(0.5f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.anchoredPosition = new Vector2(0f, -_topOffset);
            rt.sizeDelta = new Vector2(760f, 0f);
            Column(Panel.gameObject);

            TitleText = MaText.Create(Panel, string.Empty, TextStyle.Heading, TextAlignmentOptions.Center);
            DescriptionText = MaText.Create(Panel, string.Empty, TextStyle.Muted, TextAlignmentOptions.Center);
            TallyText = MaText.Create(Panel, string.Empty, TextStyle.Label, TextAlignmentOptions.Center);
            QuorumBar = MaProgressBar.Create(Panel);
            StandingText = MaText.Create(Panel, string.Empty, TextStyle.Caption, TextAlignmentOptions.Center);
            MaRow buttons = MaRow.Create(Panel);
            YesButton = buttons.AddButton("Yes", () => Cast(true), ButtonVariant.Primary, MaIcon.Check);
            NoButton = buttons.AddButton("No", () => Cast(false), ButtonVariant.Danger, MaIcon.Close);
            Panel.Visible = false;

            _board.VoteEnded += OnVoteEnded;
            _board.VoteRefused += OnVoteRefused;
            Refresh();
        }

        private void Update()
        {
            if (_board == null)
            {
                Close();
                return;
            }

            Refresh();
        }

        private void OnDestroy()
        {
            s_Overlays.Remove(this);
            if (_board != null)
            {
                _board.VoteEnded -= OnVoteEnded;
                _board.VoteRefused -= OnVoteRefused;
            }
        }

        /// <summary>Mirrors the board now (called every frame).</summary>
        public void Refresh()
        {
            VoteStatus vote = _board != null && _board.IsSpawned ? _board.Status : null;
            bool show = vote != null && ShowWhen();
            if (Panel.Visible != show)
            {
                Panel.Visible = show;
            }

            if (vote == null)
            {
                YesButton.Selected = false;
                NoButton.Selected = false;
                return;
            }

            VoteChoice mine = _board.LocalChoice;
            SetText(TitleText, vote.Title);
            SetText(DescriptionText, vote.Description);
            DescriptionText.Visible = vote.Description.Length > 0;
            SetText(TallyText, VoteText.Tally(vote));
            QuorumBar.Value = vote.Quorum > 0 ? Mathf.Clamp01((float)vote.Yes / vote.Quorum) : 0f;
            SetText(StandingText, VoteText.Standing(mine));
            bool canVote = VoteText.CanVote(mine);
            YesButton.Interactable = canVote;
            NoButton.Interactable = canVote;
            YesButton.Selected = mine == VoteChoice.Yes;
            NoButton.Selected = mine == VoteChoice.No;
        }

        private void Cast(bool yes)
        {
            if (_board != null && _board.IsSpawned)
            {
                _board.CastVote(yes);
            }
        }

        private void OnVoteEnded(VoteResult result)
        {
            if (Toasts && ShowWhen())
            {
                MaToast.Show(VoteText.Ended(result), result.Passed ? ToastKind.Success : ToastKind.Info);
            }
        }

        private void OnVoteRefused(VoteRefusal refusal, string reason)
        {
            if (Toasts)
            {
                MaToast.Show(string.IsNullOrEmpty(reason) ? VoteText.Refused(refusal) : reason, ToastKind.Warning);
            }
        }

        private bool DefaultShowWhen()
        {
            NetFrontEnd frontEnd = _board != null ? FrontEndOf(_board) : NetFrontEnd.Instance;
            return frontEnd == null || frontEnd.State == FrontEndState.InGame;
        }

        private static void SetText(MaText text, string value)
        {
            if (text.Text != value)
            {
                text.Text = value;
            }
        }

        /// <summary>A padded vertical stack that grows to fit its children.</summary>
        private static void Column(GameObject go)
        {
            var layout = go.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(48, 48, 32, 32);
            layout.spacing = 16f;
            layout.childAlignment = TextAnchor.UpperCenter;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
            var fit = go.AddComponent<ContentSizeFitter>();
            fit.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        }
    }
}
