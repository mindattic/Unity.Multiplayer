using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Unity.Multiplayer.Core;
using Unity.Collections;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEngine;

namespace Unity.Multiplayer
{
    /// <summary>
    /// One multiplayer session: host it, serve it, join it, leave it. The facade game code uses instead of
    /// NGO's <c>NetworkManager</c> (BIBLE UM-LAW-2); it drives a NetworkManager and its Unity Transport and
    /// keeps the engine-free <see cref="SessionStateMachine"/> in step with what the transport reports.
    /// </summary>
    /// <remarks>
    /// <para>Get one from <see cref="NetBootstrap.Session"/>. Host and Server go
    /// <c>Idle → Starting → Running → Stopping → Idle</c>; a Client goes
    /// <c>Idle → Connecting → Connected → Disconnected</c> and can join again from there.</para>
    /// <para>Every start takes a <see cref="NetConfig"/>; the session copies it, so changing the object
    /// afterwards does nothing. The config's transport (<see cref="TransportKind.Udp"/> or
    /// <see cref="TransportKind.WebSocket"/>), address, port and tick rate are applied to the NetworkManager
    /// and its Unity Transport before NGO starts.</para>
    /// <para>Connection approval is always on. A Client (and a Host's own player) sends a
    /// <see cref="ConnectionPayload"/> built from <see cref="Identity"/>, the config's game version and
    /// <see cref="ProtocolHash"/>. A Host or Server admits it through <see cref="ConnectionAdmission"/>: a version or
    /// protocol mismatch, a ban, a bad payload or a full server is refused with a reason the client shows
    /// (<see cref="Disconnection.Rejection"/>), and an approved player gets a <see cref="Seat"/>. A player who drops
    /// keeps its seat and its player object for <see cref="NetConfig.ReconnectGraceSeconds"/>; reconnecting with the
    /// same player token within that window gives both back (<see cref="SeatReclaimed"/>).</para>
    /// <para>Join codes (RFC 0001): <see cref="HostWithCode"/> hosts and publishes a code (Unity Relay by default, since
    /// it gets through NAT), a dedicated server with a <see cref="NetConfig.RendezvousUrl"/> registers itself with the
    /// rendezvous service as soon as it is running (and keeps the registration alive), <see cref="PublishCode"/>
    /// publishes one for a session already running, and <see cref="JoinByCode"/> joins with a code a player typed. A
    /// published code is withdrawn when the session ends.</para>
    /// <para>Network conditions (UM-T14): <see cref="NetConfig.Conditions"/> makes this peer's link slow and lossy for
    /// testing (latency, jitter, packet loss through Unity Transport's simulator stage, editor and development builds
    /// only), from the moment it starts; <see cref="SetConditions"/> changes them while it runs.</para>
    /// <para>Per-match allocation (UM-T29): a matchmaker asks an <see cref="IMatchAllocator"/> (Edgegap, Gameye, ...) for
    /// a server, and <see cref="JoinAllocatedServerAsync"/> joins what it hands back directly -- the endpoint is already
    /// known, so there is no code to resolve first, unlike <see cref="JoinByCode"/>.</para>
    /// <para>Events are raised on Unity's main thread, from NGO's callbacks. An exception thrown by a handler
    /// is logged and does not stop the session's own bookkeeping.</para>
    /// </remarks>
    public sealed class NetSession : IDisposable
    {
        /// <summary>Prefix NGO puts on a disconnect reason it generated from a transport event (as opposed to a reason the server sent).</summary>
        private const string TransportReasonPrefix = "[Disconnect Event]";

        /// <summary>The reasons NGO 2.x sends every client when the server shuts down (NetworkManager: "Disconnected due to {host|server} shutting down.").</summary>
        private static readonly string[] ServerShutdownReasons =
        {
            "Disconnected due to host shutting down.",
            "Disconnected due to server shutting down.",
        };

        private static readonly ulong[] NoClients = new ulong[0];

        private static readonly Seat[] NoSeats = new Seat[0];

        /// <summary>What a replaced connection is told when the same player connects again from elsewhere.</summary>
        internal const string ReplacedMessage = "You connected again from another session, which took over your seat.";

        /// <summary>
        /// The named message a server tells a client it is being removed with (kicked, or replaced by a new connection of
        /// the same player), carrying the reason. The client ends its session on receipt and closes its own connection.
        /// </summary>
        internal const string DisconnectNoticeMessage = "MindAttic.Multiplayer.DisconnectNotice";

        /// <summary>
        /// How long a server waits, after sending <see cref="DisconnectNoticeMessage"/>, for the client to close its own
        /// connection before closing it from this side: long enough for the notice to get through a slow, lossy link.
        /// </summary>
        internal const double LeaveGraceSeconds = 5.0;

        /// <summary>
        /// The named message a stopping Host or Server tells every remote client it is shutting down with (UM-T65), carrying
        /// the reason. The client ends its session with <see cref="DisconnectReason.ServerShutdown"/> on receipt and closes
        /// its own connection, so the server never closes one with the client's sends still arriving: over WebSocket that
        /// resets the TCP connection, the client loses NGO's own shutdown reason with it and saw a transport failure.
        /// </summary>
        internal const string ShutdownNoticeMessage = "MindAttic.Multiplayer.ShutdownNotice";

        /// <summary>
        /// How long a stopping Host or Server waits, after sending <see cref="ShutdownNoticeMessage"/>, for its clients to
        /// close their own connections before NGO shuts down and closes what is left.
        /// </summary>
        internal const double ShutdownGraceSeconds = 2.0;

        private readonly NetworkManager _manager;
        private readonly SessionStateMachine _machine = new SessionStateMachine();
        private NetConfig _config;
        private NetworkTickClock _clock;
        private bool _transportFailed;
        private bool _disposed;
        private PlayerIdentity _identity = PlayerIdentity.CreateNew();
        private ProtocolHash? _protocolHash;
        private ConnectionAdmission _admission;
        private readonly Dictionary<int, NetworkObject> _seatObjects = new Dictionary<int, NetworkObject>();
        private readonly Dictionary<ulong, int> _reclaimOnConnect = new Dictionary<ulong, int>();
        private readonly Dictionary<ulong, Disconnection> _dropReasons = new Dictionary<ulong, Disconnection>();
        private readonly Dictionary<ulong, double> _leaving = new Dictionary<ulong, double>();
        private readonly HashSet<ulong> _rejected = new HashSet<ulong>();
        private readonly RpcGate _gate = new RpcGate();
        private readonly NetSceneFlow _scenes;
        private readonly NetPrefabRegistry _prefabs;
        private readonly NetRoster _roster;
        private readonly NetRegistry _objects;
        private readonly SynchronizationContext _mainThread;
        private readonly RelayJoinCodeService _relay = new RelayJoinCodeService();
        private RendezvousJoinCodeService _rendezvous;
        private string _rendezvousKey;
        private IJoinCodeService _joinCodeOverride;
        private JoinCode _publishedCode;
        private IJoinCodeService _publishedBy;
        private int _publishGeneration;
        private NetConditions _conditions;
        private NetworkTickSystem _shutdownTicks;
        private double _shutdownDeadline;

        /// <summary>Every live session by the NetworkManager it drives, so a <see cref="NetEntity"/> can find its own.</summary>
        private static readonly Dictionary<NetworkManager, NetSession> s_ByManager = new Dictionary<NetworkManager, NetSession>();

        /// <summary>Wraps <paramref name="manager"/>. The session owns its start, stop and transport settings from now on.</summary>
        /// <exception cref="ArgumentNullException"><paramref name="manager"/> is null.</exception>
        internal NetSession(NetworkManager manager)
        {
            if (manager == null)
            {
                throw new ArgumentNullException(nameof(manager));
            }

            _manager = manager;
            _mainThread = SynchronizationContext.Current;
            _scenes = new NetSceneFlow(manager);
            _prefabs = new NetPrefabRegistry(manager);
            _roster = new NetRoster(this);
            _objects = new NetRegistry(this);
            s_ByManager[manager] = this;
            _machine.StateChanged += OnMachineStateChanged;
            _manager.OnServerStarted += OnServerStarted;
            _manager.OnServerStopped += OnServerStopped;
            _manager.OnClientStopped += OnClientStopped;
            _manager.OnClientConnectedCallback += OnClientConnected;
            _manager.OnClientDisconnectCallback += OnClientDisconnected;
            _manager.OnTransportFailure += OnTransportFailure;
        }

        /// <summary>Raised after every state change.</summary>
        public event Action<SessionStateChange> StateChanged;

        /// <summary>Raised when the session is fully up: a Host or Server is Running, a Client is Connected. <see cref="Clock"/> is ticking by then.</summary>
        public event Action Started;

        /// <summary>Raised when the session is over (Host/Server back to Idle, Client Disconnected), with why.</summary>
        public event Action<Disconnection> Ended;

        /// <summary>Server side (Host or Server): a client finished connecting. On a Host this includes the host's own local client.</summary>
        public event Action<ulong> ClientConnected;

        /// <summary>Server side (Host or Server): a remote client left or was dropped.</summary>
        public event Action<ulong> ClientDisconnected;

        /// <summary>Server side: a seated player's connection ended, with why and what became of the seat.</summary>
        public event Action<PlayerDisconnect> PlayerDisconnected;

        /// <summary>Server side: connection approval refused a client (it is told the same reason).</summary>
        public event Action<ConnectionRejection> ConnectionRejected;

        /// <summary>Server side: a new player was given a seat (raised when its connection completes).</summary>
        public event Action<Seat> SeatTaken;

        /// <summary>
        /// Server side: a player came back to its seat within the grace window (or replaced a connection of its own
        /// the server had not yet seen die). Its held player object, if any, is its player object again.
        /// </summary>
        public event Action<Seat> SeatReclaimed;

        /// <summary>Server side: a player dropped and its seat (and player object) is held until <see cref="Seat.HeldUntil"/>.</summary>
        public event Action<Seat> SeatHeld;

        /// <summary>Server side: a seat was freed (grace window over, or kicked); its held player object has been despawned.</summary>
        public event Action<Seat> SeatReleased;

        /// <summary>
        /// Server side: a <see cref="ToServerAttribute"/> call was dropped (not the owner, over its rate limit), or a client
        /// received or tried to send a server-to-client call it should not have. The call's handler did not run.
        /// </summary>
        public event Action<RpcRejection> RpcRejected;

        /// <summary>
        /// Host or Server: a join code was published for this session (by <see cref="HostWithCode"/>, <see cref="PublishCode"/>
        /// or a dedicated server's self-registration), or replaced after the rendezvous service lost the old one.
        /// </summary>
        public event Action<JoinCode> JoinCodePublished;

        /// <summary>
        /// A join-code operation failed, with a message fit to show: a code that does not parse or that no game is using,
        /// a rendezvous service that cannot be reached or refused the registration, Relay not installed.
        /// </summary>
        public event Action<string> JoinCodeFailed;

        /// <summary>Server-driven scene changes: the server loads, every client follows.</summary>
        public NetSceneFlow Scenes
        {
            get { return _scenes; }
        }

        /// <summary>The prefabs this session can spawn over the network. Register them before the session starts.</summary>
        public NetPrefabRegistry Prefabs
        {
            get { return _prefabs; }
        }

        /// <summary>
        /// Who is in the game, on every peer: each player's name, colour, seat, round trip and host flag, kept in step by
        /// the server, with <see cref="NetRoster.Joined"/> / <see cref="NetRoster.Left"/> / <see cref="NetRoster.Changed"/>
        /// and <see cref="NetRoster.RequestChange"/> for a runtime rename (UM-T55).
        /// </summary>
        public NetRoster Roster
        {
            get { return _roster; }
        }

        /// <summary>
        /// The spawned objects that carry a <see cref="NetKey"/>, by their game-given key, on this peer: look one up with
        /// <see cref="NetRegistry.TryGet{T}(string, out T)"/>, hear of them through <see cref="NetRegistry.Spawned"/> /
        /// <see cref="NetRegistry.Despawned"/>, and spawn one on the server with <see cref="NetRegistry.Spawn"/> (UM-T60).
        /// </summary>
        public NetRegistry Objects
        {
            get { return _objects; }
        }

        /// <summary>Server side: the player on <paramref name="clientId"/> now plays as <paramref name="displayName"/> in <paramref name="color"/> (an accepted rename).</summary>
        internal Seat RenameSeat(ulong clientId, string displayName, PlayerColor color)
        {
            return _admission != null ? _admission.Seats.Rename(clientId, displayName, color) : null;
        }

        /// <summary>The NetworkManager this session drives (the test kit reads spawned objects and the transport through it).</summary>
        internal NetworkManager Manager
        {
            get { return _manager; }
        }

        /// <summary>The checks generated RPC stubs run (ownership, per-sender rate); cleared with each session.</summary>
        internal RpcGate Gate
        {
            get { return _gate; }
        }

        /// <summary>The session driving <paramref name="manager"/>, or null when none does.</summary>
        internal static NetSession For(NetworkManager manager)
        {
            NetSession session;
            return manager != null && s_ByManager.TryGetValue(manager, out session) ? session : null;
        }

        /// <summary>
        /// Who <paramref name="clientId"/> is, as a <see cref="NetSender"/>: on the server, its seat and display name when it
        /// is seated; otherwise just the id.
        /// </summary>
        public NetSender GetSender(ulong clientId)
        {
            Seat seat;
            if (_admission != null && _admission.Seats.TryGetByClient(clientId, out seat))
            {
                return new NetSender(clientId, seat.Index, seat.DisplayName);
            }

            return new NetSender(clientId, -1, clientId == _manager.LocalClientId ? _identity.DisplayName : string.Empty);
        }

        /// <summary>Raises <see cref="RpcRejected"/> and logs the drop (a rate-limited flood logs on its 1st, 10th, 100th... refusal).</summary>
        internal void ReportRejected(RpcRejection rejection)
        {
            if (rejection.Verdict != RpcVerdict.RateLimited || ShouldLogRateLimited(_gate.Limiter.RejectedCount(rejection.SenderClientId)))
            {
                Debug.LogWarning("[MindAttic.Multiplayer] Dropped RPC " + rejection);
            }

            Raise(RpcRejected, rejection);
        }

        /// <summary>True for the 1st, 10th, 100th, ... refusal: enough to see a flood without the log becoming one.</summary>
        internal static bool ShouldLogRateLimited(long count)
        {
            if (count <= 1)
            {
                return true;
            }

            while (count % 10 == 0)
            {
                count /= 10;
            }

            return count == 1;
        }

        /// <summary>Where the session is in its life.</summary>
        public SessionState State
        {
            get { return _machine.State; }
        }

        /// <summary>The topology of the current or last session; null before the first start.</summary>
        public Core.Topology? Topology
        {
            get { return _machine.Topology; }
        }

        /// <summary>Why the last session ended; <see cref="Disconnection.None"/> while one is live or before any has ended.</summary>
        public Disconnection LastDisconnect
        {
            get { return _machine.LastDisconnect; }
        }

        /// <summary>True from a start until the session has ended.</summary>
        public bool IsActive
        {
            get { return _machine.IsActive; }
        }

        /// <summary>True while the session is fully up (Running or Connected).</summary>
        public bool IsLive
        {
            get { return _machine.IsLive; }
        }

        /// <summary>True when this process runs the authoritative server of a live session (Host or Server).</summary>
        public bool IsServer
        {
            get { return _machine.IsLive && _manager.IsServer; }
        }

        /// <summary>True when this process is a Host (server plus local player) of a live session.</summary>
        public bool IsHost
        {
            get { return _machine.IsLive && _manager.IsHost; }
        }

        /// <summary>True when this process has a connected local player (Host or Client) in a live session.</summary>
        public bool IsClient
        {
            get { return _machine.IsLive && _manager.IsClient; }
        }

        /// <summary>
        /// True when a new session can start now: none is active and NGO has finished shutting down the last
        /// one (a shutdown completes at the end of the frame it was asked for).
        /// </summary>
        public bool CanStart
        {
            get { return !_disposed && !_machine.IsActive && !_manager.IsListening && !_manager.ShutdownInProgress; }
        }

        /// <summary>This process's client id in the session (the server's is 0).</summary>
        public ulong LocalClientId
        {
            get { return _manager.LocalClientId; }
        }

        /// <summary>
        /// Server side: the ids of every connected client (on a Host, the host's own client too). Empty on a
        /// client and when no session is running.
        /// </summary>
        public IReadOnlyList<ulong> ConnectedClientIds
        {
            get { return _manager.IsServer && _manager.IsListening ? _manager.ConnectedClientsIds : NoClients; }
        }

        /// <summary>
        /// The network conditions this peer is simulating (<see cref="NetConfig.Conditions"/> at start, or the last
        /// <see cref="SetConditions"/>); <see cref="NetConditions.None"/> when nothing is simulated or the build cannot.
        /// </summary>
        public NetConditions Conditions
        {
            get { return _conditions; }
        }

        /// <summary>
        /// Changes the simulated network conditions of the live session at once, on connections already open too (for
        /// example to make a running game lossy from a debug menu). The next start takes <see cref="NetConfig.Conditions"/>
        /// again. Editor and development builds only (see <see cref="NetConditions"/>).
        /// </summary>
        /// <returns>True when they now apply; false (with a warning) when no session is running or this build has no simulator.</returns>
        /// <exception cref="ArgumentException"><paramref name="conditions"/> are out of range (see <see cref="NetConditions.Validate"/>).</exception>
        public bool SetConditions(NetConditions conditions)
        {
            if (!conditions.IsValid)
            {
                throw new ArgumentException("Invalid NetConditions: " + string.Join(" ", conditions.Validate()), nameof(conditions));
            }

            if (!_manager.IsListening)
            {
                Debug.LogWarning("[MindAttic.Multiplayer] SetConditions needs a running session; set NetConfig.Conditions before starting instead.");
                return false;
            }

            return ApplyConditions(_manager.NetworkConfig.NetworkTransport as UnityTransport, conditions, false);
        }

        /// <summary>
        /// Puts <paramref name="conditions"/> on the started transport and records them in <see cref="Conditions"/>. At start,
        /// no simulation leaves the stage alone (so Multiplayer Tools' Network Simulator, if used instead, keeps control).
        /// </summary>
        private bool ApplyConditions(UnityTransport transport, NetConditions conditions, bool atStart)
        {
            if (atStart && conditions.IsNone)
            {
                _conditions = NetConditions.None;
                return true;
            }

            string problem;
            if (!TransportConditions.TryApply(transport, conditions, out problem))
            {
                Debug.LogWarning("[MindAttic.Multiplayer] Simulated network conditions (" + conditions + ") were not applied: " + problem + ".");
                if (atStart)
                {
                    _conditions = NetConditions.None;
                }

                return false;
            }

            _conditions = conditions;
            if (!conditions.IsNone)
            {
                Debug.Log("[MindAttic.Multiplayer] Simulating network conditions on this " + (_config != null ? _config.Topology.ToString() : "peer") + ": " + conditions + ".");
            }

            return true;
        }

        /// <summary>The fixed-rate clock of the live session; null when no session is live.</summary>
        public ITickClock Clock
        {
            get { return _clock; }
        }

        /// <summary>
        /// Who this process plays as: the player token a server seats it by, and its name and colour. Defaults to
        /// a fresh random identity; set a stored one (see <see cref="NetBootstrap"/>) so a restarted game gets its
        /// seat back. Read when a Client or Host starts.
        /// </summary>
        /// <exception cref="ArgumentNullException">Set to null.</exception>
        public PlayerIdentity Identity
        {
            get { return _identity; }
            set { _identity = value ?? throw new ArgumentNullException(nameof(value)); }
        }

        /// <summary>
        /// This build's network protocol hash, sent by clients and required by servers. Defaults to the hash the
        /// generator emits into the loaded assemblies (<see cref="Core.ProtocolHash.FromAssemblies(System.Reflection.Assembly[])"/>);
        /// set it to override. Read when a session starts.
        /// </summary>
        public ProtocolHash ProtocolHash
        {
            get
            {
                if (!_protocolHash.HasValue)
                {
                    _protocolHash = Core.ProtocolHash.FromAssemblies(AppDomain.CurrentDomain.GetAssemblies());
                }

                return _protocolHash.Value;
            }

            set { _protocolHash = value; }
        }

        /// <summary>Server side: the bans connection approval enforces; null for none. Read when a Host or Server starts.</summary>
        public IBanList BanList { get; set; }

        /// <summary>Server side: every taken seat (occupied or held), by index. Empty on a client and when no server is running.</summary>
        public IReadOnlyList<Seat> Seats
        {
            get { return _admission != null ? _admission.Seats.Seats : NoSeats; }
        }

        /// <summary>Server side: the seat of a connected client.</summary>
        public bool TryGetSeat(ulong clientId, out Seat seat)
        {
            seat = null;
            return _admission != null && _admission.Seats.TryGetByClient(clientId, out seat);
        }

        /// <summary>
        /// Server side: the player object kept with a seat (spawned for the seat's first connection and handed back
        /// on a reconnect), or null when the seat has none.
        /// </summary>
        public GameObject GetSeatPlayerObject(int seatIndex)
        {
            NetworkObject playerObject;
            return _seatObjects.TryGetValue(seatIndex, out playerObject) && playerObject != null ? playerObject.gameObject : null;
        }

        /// <summary>
        /// Server side: removes a client now, frees its seat with no grace window (its player object is despawned)
        /// and tells it <paramref name="reason"/>; it ends with <see cref="DisconnectReason.Kicked"/>.
        /// </summary>
        /// <remarks>
        /// The reason goes first, as a reliable message the client leaves on; the server closes the connection itself only
        /// if the client has not gone within <see cref="LeaveGraceSeconds"/>. (NGO's own <c>DisconnectClient(reason)</c>
        /// closes the connection right after queuing the reason, so on a lossy link the reason could be lost and the client
        /// would see only a closed connection.)
        /// </remarks>
        /// <returns>False when this is not a running server or the client is not connected (or is the host itself, or is already being removed).</returns>
        public bool Kick(ulong clientId, string reason = null)
        {
            if (!IsServer || clientId == NetworkManager.ServerClientId || !_manager.ConnectedClients.ContainsKey(clientId)
                || _leaving.ContainsKey(clientId))
            {
                return false;
            }

            string message = string.IsNullOrWhiteSpace(reason) ? "You were removed from the server." : reason;
            Seat freed = _admission != null ? _admission.Seats.Free(clientId) : null;
            if (freed != null)
            {
                ReleaseSeatObject(freed);
            }

            Remove(clientId, message);
            if (freed != null)
            {
                Raise(SeatReleased, freed);
            }

            _roster.Refresh();
            return true;
        }

        /// <summary>A copy of the config the current or last session started with; null before the first start.</summary>
        public NetConfig Config
        {
            get { return _config == null ? null : _config.Clone(); }
        }

        /// <summary>Hosts a listen server: this process serves and plays. <paramref name="config"/>'s topology is ignored.</summary>
        /// <returns>True when NGO started; the session is then Starting or already Running.</returns>
        /// <exception cref="ArgumentException">The config is invalid (see <see cref="NetConfig.Validate"/>).</exception>
        /// <exception cref="InvalidOperationException">A session is already active.</exception>
        public bool StartHost(NetConfig config = null)
        {
            return Start(WithTopology(config, Core.Topology.Host));
        }

        /// <summary>Runs a dedicated server: no local player. <paramref name="config"/>'s topology is ignored.</summary>
        /// <returns>True when NGO started; the session is then Starting or already Running.</returns>
        /// <exception cref="ArgumentException">The config is invalid (see <see cref="NetConfig.Validate"/>).</exception>
        /// <exception cref="InvalidOperationException">A session is already active.</exception>
        public bool StartServer(NetConfig config = null)
        {
            return Start(WithTopology(config, Core.Topology.Server));
        }

        /// <summary>
        /// Joins the host or server at <paramref name="address"/>:<paramref name="port"/>. The rest of the
        /// settings (transport, tick rate, game version) come from <paramref name="config"/> when given.
        /// </summary>
        /// <returns>True when NGO began connecting; the session is then Connecting until <see cref="Started"/> or <see cref="Ended"/>.</returns>
        /// <exception cref="ArgumentException">The address, port or config is invalid.</exception>
        /// <exception cref="InvalidOperationException">A session is already active.</exception>
        public bool JoinByAddress(string address, ushort port, NetConfig config = null)
        {
            NetConfig join = WithTopology(config, Core.Topology.Client);
            join.Address = address;
            join.Port = port;
            return Start(join);
        }

        /// <summary>Starts whatever <paramref name="config"/>'s topology says: host, serve or join.</summary>
        /// <returns>
        /// True when NGO started. False when NGO refused (the session then ends at once with
        /// <see cref="DisconnectReason.TransportFailure"/> and the reason is logged) or when the last session
        /// is still shutting down (see <see cref="CanStart"/>; nothing changes).
        /// </returns>
        /// <exception cref="ArgumentNullException"><paramref name="config"/> is null.</exception>
        /// <exception cref="ArgumentException">The config is invalid (see <see cref="NetConfig.Validate"/>).</exception>
        /// <exception cref="InvalidOperationException">A session is already active.</exception>
        /// <exception cref="ObjectDisposedException">The session was disposed.</exception>
        public bool Start(NetConfig config)
        {
            return StartCore(config, null);
        }

        /// <summary>
        /// <see cref="Start"/>, with <paramref name="transportSetup"/> run on the configured transport just before NGO
        /// starts (Relay points it at an allocation there). A setup that returns false aborts the start.
        /// </summary>
        private bool StartCore(NetConfig config, Func<UnityTransport, bool> transportSetup)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(NetSession));
            }

            if (config == null)
            {
                throw new ArgumentNullException(nameof(config));
            }

            NetConfig copy = config.Clone();
            copy.EnsureValid();

            if (_machine.IsActive)
            {
                throw new InvalidOperationException(
                    "Cannot start a " + copy.Topology + " session while the session is " + _machine.State + "; call Leave first.");
            }

            if (_manager.IsListening || _manager.ShutdownInProgress)
            {
                Debug.LogWarning("[MindAttic.Multiplayer] The last session is still shutting down; start again once CanStart is true.");
                return false;
            }

            UnityTransport configured = Configure(copy);
            if (transportSetup != null && !transportSetup(configured))
            {
                Debug.LogWarning("[MindAttic.Multiplayer] The transport could not be set up for this " + copy.Topology + "; not starting.");
                return false;
            }

            ForgetPublishedCode();
            JoinedAllocation = null;
            _roster.Reset();
            _objects.Reset();
            _config = copy;
            _transportFailed = false;
            _machine.Start(copy.Topology);

            bool started;
            Exception failure = null;
            try
            {
                switch (copy.Topology)
                {
                    case Core.Topology.Host:
                        started = _manager.StartHost();
                        break;
                    case Core.Topology.Server:
                        started = _manager.StartServer();
                        break;
                    default:
                        started = _manager.StartClient();
                        break;
                }
            }
            catch (Exception ex)
            {
                failure = ex;
                started = false;
            }

            if (!started)
            {
                // NGO returning false with nothing thrown really does mean the transport could not bind (the port is
                // taken); an exception is a different problem (for example NGO's own setup not finished yet), so that
                // real reason is reported instead of guessing at a port conflict.
                string message = failure != null
                    ? "NGO could not start a " + copy.Topology + " on " + copy.EffectiveAddress + ":" + copy.Port
                        + " over " + copy.Transport + ": " + failure.GetType().Name + ": " + failure.Message
                    : "NGO could not start a " + copy.Topology + " on " + copy.EffectiveAddress + ":" + copy.Port
                        + " over " + copy.Transport + " (is the port already in use?).";
                if (failure != null)
                {
                    Debug.LogException(failure);
                }

                Debug.LogWarning("[MindAttic.Multiplayer] " + message);
                if (_machine.IsActive)
                {
                    _machine.NotifyDisconnected(DisconnectReason.TransportFailure, message);
                }

                return false;
            }

            // The driver exists now and no pipelined packet has gone out yet (a client has only sent its connect request).
            ApplyConditions(configured, copy.EffectiveConditions, true);
            if (copy.Topology == Core.Topology.Client && _manager.CustomMessagingManager != null)
            {
                // NGO makes a new messaging manager with every start, so the handler is registered with every join.
                _manager.CustomMessagingManager.RegisterNamedMessageHandler(DisconnectNoticeMessage, OnDisconnectNotice);
                _manager.CustomMessagingManager.RegisterNamedMessageHandler(ShutdownNoticeMessage, OnShutdownNotice);
            }

            _roster.Attach(copy.Topology);
            _scenes.Attach();
            return true;
        }

        /// <summary>
        /// Leaves the session. A Client is Disconnected at once with <see cref="DisconnectReason.LocalLeave"/>;
        /// a Host or Server goes to Stopping and reaches Idle when NGO has shut down: at once with no remote clients,
        /// otherwise once every client, told first with <see cref="ShutdownNoticeMessage"/>, has closed its own connection
        /// (normally one round trip; at most <see cref="ShutdownGraceSeconds"/>).
        /// </summary>
        /// <returns>False when there was nothing to leave.</returns>
        public bool Leave()
        {
            if (!_machine.Stop())
            {
                return false;
            }

            if (_manager.IsListening)
            {
                if (!AnnounceShutdown())
                {
                    _manager.Shutdown();
                }
            }
            else
            {
                // NGO is not running (it never got going), so no stop callback will come: finish here.
                _machine.NotifyStopped();
            }

            return true;
        }

        /// <summary>Leaves any active session and stops listening to the NetworkManager. Safe to call more than once.</summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            Leave();
            FinishShutdown(); // no waiting for clients to go: nothing would be listening to finish it

            // NGO finishes the shutdown by itself at the end of the frame, but this session stops listening
            // now, so it closes its own books: a disposed session always ends Idle (or Disconnected).
            _machine.NotifyStopped();
            _disposed = true;
            if (For(_manager) == this)
            {
                s_ByManager.Remove(_manager);
            }

            _scenes.Detach();
            _manager.OnServerStarted -= OnServerStarted;
            _manager.OnServerStopped -= OnServerStopped;
            _manager.OnClientStopped -= OnClientStopped;
            _manager.OnClientConnectedCallback -= OnClientConnected;
            _manager.OnClientDisconnectCallback -= OnClientDisconnected;
            _manager.OnTransportFailure -= OnTransportFailure;
            if (_manager.ConnectionApprovalCallback == (Action<NetworkManager.ConnectionApprovalRequest, NetworkManager.ConnectionApprovalResponse>)OnApproval)
            {
                _manager.ConnectionApprovalCallback = null;
            }

            StopClock();
            ResetSeats();
            WaitForPendingRevoke();
            if (_rendezvous != null)
            {
                // Stops heartbeats and deletes what is still registered (best effort; anything left expires by itself).
                _rendezvous.RegistrationLost -= OnRegistrationLost;
                _rendezvous.Dispose();
                _rendezvous = null;
            }
        }

        /// <summary>
        /// The join-code service to use instead of the defaults (Relay for a listen server, the rendezvous service for a
        /// dedicated server): for a custom backend, or <see cref="InMemoryJoinCodeService"/> in tests. Null (the default)
        /// uses <see cref="NetConfig.JoinCodes"/>. An override publishes endpoint targets, so it suits a reachable server.
        /// </summary>
        public IJoinCodeService JoinCodes
        {
            get { return _joinCodeOverride; }
            set { _joinCodeOverride = value; }
        }

        /// <summary>The Relay service this session uses (its <see cref="RelayJoinCodeService.Region"/> can be set).</summary>
        public RelayJoinCodeService Relay
        {
            get { return _relay; }
        }

        /// <summary>The code published for the live session; <see cref="JoinCode.Empty"/> when there is none.</summary>
        public JoinCode PublishedCode
        {
            get { return _publishedCode; }
        }

        /// <summary><see cref="PublishedCode"/> grouped for display (<c>ABC-DEF-GHJ</c>); empty when there is none.</summary>
        public string PublishedCodeText
        {
            get { return _publishedCode.IsEmpty ? string.Empty : JoinCodeFormat.Format(_publishedCode.Value); }
        }

        /// <summary>
        /// Hosts a listen server and publishes a join code for it. The provider is <paramref name="config"/>'s
        /// <see cref="NetConfig.JoinCodes"/>, where Auto means Unity Relay when it is installed (it gets through NAT, so
        /// friends can join a game hosted at home with no port forwarding), else the rendezvous service when a
        /// <see cref="NetConfig.RendezvousUrl"/> is set (<see cref="JoinCodeDefaults.ForHosting"/>); Rendezvous publishes this
        /// host's address, which works only when players can reach it. <see cref="JoinCodes"/>, when set, is used instead.
        /// </summary>
        /// <returns>
        /// The code (also in <see cref="PublishedCode"/> and raised as <see cref="JoinCodePublished"/>), or
        /// <see cref="JoinCode.Empty"/> when the host did not start or the code could not be published (the session then
        /// is left, and <see cref="JoinCodeFailed"/> says why).
        /// </returns>
        /// <exception cref="ArgumentException">The config is invalid.</exception>
        /// <exception cref="InvalidOperationException">A session is already active, or join codes are off (<see cref="JoinCodeProvider.None"/>).</exception>
        public async Task<JoinCode> HostWithCode(NetConfig config = null)
        {
            NetConfig host = WithTopology(config, Core.Topology.Host);
            host.EnsureValid();
            if (_machine.IsActive)
            {
                throw new InvalidOperationException("Cannot host while the session is " + _machine.State + "; call Leave first.");
            }

            JoinCodeProvider provider = _joinCodeOverride != null
                ? JoinCodeProvider.Rendezvous
                : JoinCodeDefaults.ForHosting(host.JoinCodes, RelayJoinCodeService.IsInstalled, !string.IsNullOrWhiteSpace(host.RendezvousUrl));
            if (provider == JoinCodeProvider.None)
            {
                throw new InvalidOperationException("Join codes are off (NetConfig.JoinCodes = None); use StartHost.");
            }

            if (provider != JoinCodeProvider.Relay)
            {
                // A reachable host: start, then publish its address like a dedicated server would.
                if (!StartHost(host) || !await WhenLive())
                {
                    return JoinCode.Empty;
                }

                return await PublishOrLeave();
            }

            if (!RelayJoinCodeService.IsInstalled)
            {
                Fail(RelayJoinCodeService.MissingPackageMessage);
                return JoinCode.Empty;
            }

            JoinTarget relayTarget;
            try
            {
                relayTarget = await _relay.AllocateAsync(host.MaxPlayers - 1, host.Transport);
            }
            catch (Exception ex)
            {
                // A setup problem (not linked, Relay off, offline) carries its own sentence saying what to fix.
                Fail(RelayJoinCodeService.Describe(ex, "Could not get a Unity Relay allocation"));
                return JoinCode.Empty;
            }

            if (!StartCore(host, transport => _relay.ApplyHost(host.Transport, transport)))
            {
                return JoinCode.Empty;
            }

            JoinCode code = await _relay.PublishAsync(relayTarget);
            SetPublished(code, _relay);
            return code;
        }

        /// <summary>
        /// Publishes a join code for the Host or Server already running: registers its public endpoint
        /// (<see cref="ServerAdvertisement.For"/>: <see cref="NetConfig.PublicAddress"/>/<see cref="NetConfig.PublicPort"/>,
        /// RunPod's mapped port, else the listen port at the address the service sees) with the rendezvous service and keeps
        /// it alive, or publishes it through <see cref="JoinCodes"/> when set. Returns the code already published, if any.
        /// A dedicated server with a <see cref="NetConfig.RendezvousUrl"/> does this by itself when it starts.
        /// </summary>
        /// <exception cref="InvalidOperationException">
        /// No Host or Server is running; or its provider is Relay (a Relay allocation is made before the host starts: use
        /// <see cref="HostWithCode"/>); or no rendezvous URL is configured.
        /// </exception>
        /// <exception cref="RendezvousException">The rendezvous service refused the registration or could not be reached.</exception>
        public async Task<JoinCode> PublishCode()
        {
            if (!IsServer || _config == null)
            {
                throw new InvalidOperationException("Only a running Host or Server can publish a join code.");
            }

            if (!_publishedCode.IsEmpty)
            {
                return _publishedCode;
            }

            int generation = _publishGeneration;
            IJoinCodeService service;
            JoinCode code;
            if (_joinCodeOverride != null)
            {
                service = _joinCodeOverride;
                ServerAdvertisement custom = ServerAdvertisement.For(_config, EnvironmentReader);
                string address = custom.Address ?? (_config.EffectiveAddress == NetConfig.AnyAddress ? NetConfig.LoopbackAddress : _config.EffectiveAddress);
                code = await service.PublishAsync(JoinTarget.ForEndpoint(address, custom.Port, custom.Transport));
            }
            else
            {
                // A running host can only publish through the rendezvous service (a Relay allocation is made before it starts).
                JoinCodeProvider provider = _config.Topology == Core.Topology.Host
                    ? JoinCodeDefaults.ForHosting(_config.JoinCodes, false, !string.IsNullOrWhiteSpace(_config.RendezvousUrl))
                    : JoinCodeDefaults.ForPublishing(_config.Topology, _config.JoinCodes);
                if (provider == JoinCodeProvider.Relay)
                {
                    throw new InvalidOperationException("A Relay join code is set up when the host starts: use HostWithCode, or set NetConfig.JoinCodes = Rendezvous.");
                }

                if (provider == JoinCodeProvider.None)
                {
                    throw new InvalidOperationException("Join codes are off (NetConfig.JoinCodes = None).");
                }

                RendezvousJoinCodeService rendezvous = RendezvousFor(_config);
                ServerAdvertisement advertisement = ServerAdvertisement.For(_config, EnvironmentReader);
                for (int i = 0; i < advertisement.Warnings.Count; i++)
                {
                    Debug.LogWarning("[MindAttic.Multiplayer] " + advertisement.Warnings[i]);
                }

                service = rendezvous;
                code = await rendezvous.RegisterAsync(advertisement);
                Debug.Log("[MindAttic.Multiplayer] Registered " + advertisement + " with the rendezvous service as " + JoinCodeFormat.Format(code.Value) + ".");
            }

            if (generation != _publishGeneration || !IsServer)
            {
                // The session ended (or restarted) while the code was being published: take it back.
                await RevokeQuietly(service, code);
                return JoinCode.Empty;
            }

            if (!_publishedCode.IsEmpty)
            {
                // Another publish finished first; keep that one.
                await RevokeQuietly(service, code);
                return _publishedCode;
            }

            SetPublished(code, service);
            return code;
        }

        /// <summary>
        /// Joins the game behind a code a player typed. Case, spaces and dashes are ignored, and a single misread
        /// character (0 for O, 1 for I) is corrected. The code is looked up with <see cref="JoinCodes"/> when set, else by
        /// <paramref name="config"/>'s <see cref="NetConfig.JoinCodes"/>: Rendezvous asks the service at
        /// <see cref="NetConfig.RendezvousUrl"/>, Relay asks Unity Relay, and Auto tries the rendezvous service (when a URL
        /// is set) and then Relay (when installed and the code could be a Relay code). An address target is joined like
        /// <see cref="JoinByAddress"/>, over the transport the server registered; a Relay target through the allocation.
        /// </summary>
        /// <returns>
        /// True when NGO began connecting (the session is then Connecting, as after <see cref="JoinByAddress"/>). False when
        /// the code does not parse, no game is using it or it could not be looked up; <see cref="JoinCodeFailed"/> carries
        /// a message to show the player.
        /// </returns>
        /// <exception cref="ArgumentException">The config is invalid.</exception>
        /// <exception cref="InvalidOperationException">A session is already active.</exception>
        public async Task<bool> JoinByCode(string code, NetConfig config = null)
        {
            NetConfig join = WithTopology(config, Core.Topology.Client);
            join.EnsureValid();
            if (_machine.IsActive)
            {
                throw new InvalidOperationException("Cannot join while the session is " + _machine.State + "; call Leave first.");
            }

            JoinCodeParseResult parsed;
            if (!JoinCode.TryParse(code, out parsed))
            {
                Fail(parsed.Message);
                return false;
            }

            List<IJoinCodeService> services = ResolversFor(join, parsed.Code);
            if (services.Count == 0)
            {
                Fail(join.JoinCodes == JoinCodeProvider.None
                    ? "Join codes are off (NetConfig.JoinCodes = None)."
                    : join.JoinCodes == JoinCodeProvider.Relay && !RelayJoinCodeService.IsInstalled
                        ? RelayJoinCodeService.MissingPackageMessage
                        : "No join-code service is set up: set NetConfig.RendezvousUrl, or install com.unity.services.multiplayer for Relay.");
                return false;
            }

            JoinTarget target = null;
            Exception lookupError = null;
            foreach (IJoinCodeService service in services)
            {
                try
                {
                    target = await service.ResolveAsync(parsed.Code);
                }
                catch (Exception ex)
                {
                    lookupError = ex;
                    Debug.LogWarning("[MindAttic.Multiplayer] Looking up join code with " + service.GetType().Name + " failed: " + ex.Message);
                }

                if (target != null)
                {
                    break;
                }
            }

            string shown = JoinCodeFormat.Format(parsed.Code.Value);
            if (target == null)
            {
                Fail(lookupError != null
                    ? "Could not look up code " + shown + ": " + lookupError.Message
                    : "No game is using code " + shown + ". It may have ended, or the code may have expired.");
                return false;
            }

            if (_disposed || _machine.IsActive)
            {
                Fail("Another session started while code " + shown + " was being looked up.");
                return false;
            }

            Debug.Log("[MindAttic.Multiplayer] Join code " + shown + " leads to " + target + ".");
            join.Transport = target.Transport;
            if (target.Kind == JoinTargetKind.Relay)
            {
                return StartCore(join, transport => _relay.ApplyJoin(target, join.Transport, transport));
            }

            join.Address = target.Address;
            join.Port = target.Port;
            return StartCore(join, null);
        }

        /// <summary>
        /// The server <see cref="JoinAllocatedServerAsync"/> most recently joined through, or null before that is called
        /// (or after a plain <see cref="JoinByAddress"/>/<see cref="JoinByCode"/>). Hand this to the allocator's
        /// <see cref="IMatchAllocator.ReleaseAsync"/> once the match ends.
        /// </summary>
        public MatchAllocation JoinedAllocation { get; private set; }

        /// <summary>
        /// Requests a server for <paramref name="request"/> from <paramref name="allocator"/> and joins the endpoint it
        /// returns directly -- unlike <see cref="JoinByCode"/>, there is no code to resolve, since the allocator already
        /// handed back the resolved <see cref="JoinTarget"/> in its <see cref="MatchAllocation"/>. The rest of the
        /// settings (transport, tick rate, game version) come from <paramref name="config"/> when given; the transport
        /// is always overridden from the allocation's endpoint.
        /// </summary>
        /// <returns>
        /// True when NGO began connecting (the session is then Connecting, as after <see cref="JoinByAddress"/>). False
        /// when the allocator could not provide a server; <see cref="JoinCodeFailed"/> carries a message to show.
        /// </returns>
        /// <exception cref="ArgumentNullException"><paramref name="allocator"/> or <paramref name="request"/> is null.</exception>
        /// <exception cref="ArgumentException">The config is invalid.</exception>
        /// <exception cref="InvalidOperationException">A session is already active.</exception>
        public async Task<bool> JoinAllocatedServerAsync(IMatchAllocator allocator, MatchRequest request, NetConfig config = null)
        {
            if (allocator == null)
            {
                throw new ArgumentNullException(nameof(allocator));
            }

            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            NetConfig join = WithTopology(config, Core.Topology.Client);
            join.EnsureValid();
            if (_machine.IsActive)
            {
                throw new InvalidOperationException("Cannot join while the session is " + _machine.State + "; call Leave first.");
            }

            MatchAllocation allocation;
            try
            {
                allocation = await allocator.AllocateAsync(request);
            }
            catch (Exception ex)
            {
                Fail("Could not get a server for " + request + " from " + allocator.DisplayName + ": " + ex.Message);
                return false;
            }

            if (_disposed || _machine.IsActive)
            {
                Fail("Another session started while a server for " + request + " was being allocated.");
                return false;
            }

            Debug.Log("[MindAttic.Multiplayer] " + allocator.DisplayName + " allocated " + allocation + " for " + request + ".");
            JoinTarget target = allocation.Endpoint;
            join.Transport = target.Transport;
            bool started = target.Kind == JoinTargetKind.Relay
                ? StartCore(join, transport => _relay.ApplyJoin(target, join.Transport, transport))
                : StartCore(SetEndpoint(join, target), null);

            // Set after StartCore, which clears it as part of every start (JoinByAddress, JoinByCode, ...);
            // the allocation exists either way and callers release it even if the join itself failed.
            JoinedAllocation = allocation;
            return started;
        }

        private static NetConfig SetEndpoint(NetConfig config, JoinTarget target)
        {
            config.Address = target.Address;
            config.Port = target.Port;
            return config;
        }

        /// <summary>Reads environment variables for <see cref="ServerAdvertisement.For"/>; tests swap it. Null reads the process environment.</summary>
        internal Func<string, string> EnvironmentReader { get; set; }

        /// <summary>The services <see cref="JoinByCode"/> asks, in order.</summary>
        private List<IJoinCodeService> ResolversFor(NetConfig join, JoinCode code)
        {
            var services = new List<IJoinCodeService>();
            if (_joinCodeOverride != null)
            {
                services.Add(_joinCodeOverride);
                return services;
            }

            bool hasRendezvous = !string.IsNullOrWhiteSpace(join.RendezvousUrl);
            string relayCode;
            switch (join.JoinCodes)
            {
                case JoinCodeProvider.Rendezvous:
                    if (hasRendezvous)
                    {
                        services.Add(RendezvousFor(join));
                    }

                    break;
                case JoinCodeProvider.Relay:
                    if (RelayJoinCodeService.IsInstalled)
                    {
                        services.Add(_relay);
                    }

                    break;
                case JoinCodeProvider.Auto:
                    if (hasRendezvous)
                    {
                        services.Add(RendezvousFor(join));
                    }

                    if (RelayJoinCodeService.IsInstalled && RelayCodes.TryGetRelayCode(code, out relayCode))
                    {
                        services.Add(_relay);
                    }

                    break;
            }

            return services;
        }

        /// <summary>The rendezvous service for <paramref name="config"/>'s URL and secret, made once and reused while they stay the same.</summary>
        /// <exception cref="InvalidOperationException">No rendezvous URL is configured.</exception>
        private RendezvousJoinCodeService RendezvousFor(NetConfig config)
        {
            if (string.IsNullOrWhiteSpace(config.RendezvousUrl))
            {
                throw new InvalidOperationException("No rendezvous service is configured: set NetConfig.RendezvousUrl (or -rendezvous, or MINDATTIC_RENDEZVOUS_URL).");
            }

            string key = config.RendezvousUrl.Trim() + "\n" + (config.RendezvousSecret ?? string.Empty);
            if (_rendezvous != null && _rendezvousKey == key)
            {
                return _rendezvous;
            }

            if (_rendezvous != null && _publishedBy != _rendezvous)
            {
                _rendezvous.RegistrationLost -= OnRegistrationLost;
                _rendezvous.Dispose();
            }

            _rendezvous = new RendezvousJoinCodeService(config.RendezvousUrl.Trim(), config.RendezvousSecret);
            _rendezvous.RegistrationLost += OnRegistrationLost;
            _rendezvousKey = key;
            return _rendezvous;
        }

        /// <summary>Registers this server with the rendezvous service in the background (a dedicated server reaching Running, or re-registering after a lost registration).</summary>
        private async void SelfRegister()
        {
            try
            {
                await PublishCode();
            }
            catch (Exception ex)
            {
                Fail("Could not register this server with the rendezvous service: " + ex.Message);
            }
        }

        /// <summary>Publishes a code for a host that just started; leaves the session when that fails.</summary>
        private async Task<JoinCode> PublishOrLeave()
        {
            try
            {
                JoinCode code = await PublishCode();
                if (!code.IsEmpty)
                {
                    return code;
                }
            }
            catch (Exception ex)
            {
                Fail("Could not publish a join code: " + ex.Message);
            }

            Leave();
            return JoinCode.Empty;
        }

        /// <summary>Completes when the session is live (true) or has ended without getting there (false).</summary>
        private Task<bool> WhenLive()
        {
            if (_machine.IsLive)
            {
                return Task.FromResult(true);
            }

            if (!_machine.IsActive)
            {
                return Task.FromResult(false);
            }

            var done = new TaskCompletionSource<bool>();
            Action started = null;
            Action<Disconnection> ended = null;
            started = () =>
            {
                Started -= started;
                Ended -= ended;
                done.TrySetResult(true);
            };
            ended = _ =>
            {
                Started -= started;
                Ended -= ended;
                done.TrySetResult(false);
            };
            Started += started;
            Ended += ended;
            return done.Task;
        }

        private void SetPublished(JoinCode code, IJoinCodeService by)
        {
            _publishedCode = code;
            _publishedBy = by;
            Debug.Log("[MindAttic.Multiplayer] Join code: " + JoinCodeFormat.Format(code.Value));
            Raise(JoinCodePublished, code);
        }

        /// <summary>Forgets the published code without revoking it (a new session is starting).</summary>
        private void ForgetPublishedCode()
        {
            _publishGeneration++;
            _publishedCode = JoinCode.Empty;
            _publishedBy = null;
        }

        private Task _pendingRevoke;

        /// <summary>The session is ending: withdraw its code so it stops resolving now rather than when it expires.</summary>
        private void WithdrawPublishedCode()
        {
            JoinCode code = _publishedCode;
            IJoinCodeService by = _publishedBy;
            ForgetPublishedCode();
            if (code.IsEmpty || by == null)
            {
                return;
            }

            Task revoke;
            try
            {
                revoke = by.RevokeAsync(code);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[MindAttic.Multiplayer] Could not withdraw join code " + code + ": " + ex.Message);
                return;
            }

            _pendingRevoke = revoke;
            revoke.ContinueWith(
                t => Debug.LogWarning("[MindAttic.Multiplayer] Could not withdraw join code " + code + ": " + t.Exception.GetBaseException().Message),
                TaskContinuationOptions.OnlyOnFaulted);
        }

        /// <summary>Gives an in-flight withdrawal up to 2 s to reach the service before the session closes its connection.</summary>
        private void WaitForPendingRevoke()
        {
            Task revoke = _pendingRevoke;
            _pendingRevoke = null;
            if (revoke == null || revoke.IsCompleted)
            {
                return;
            }

            try
            {
                // Safe on the main thread: the rendezvous service's awaits never resume on it.
                revoke.Wait(TimeSpan.FromSeconds(2));
            }
            catch (AggregateException)
            {
                // Logged by WithdrawPublishedCode's continuation.
            }
        }

        private static async Task RevokeQuietly(IJoinCodeService service, JoinCode code)
        {
            try
            {
                await service.RevokeAsync(code);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[MindAttic.Multiplayer] Could not withdraw join code " + code + ": " + ex.Message);
            }
        }

        /// <summary>The rendezvous service lost this server's registration (raised off the main thread).</summary>
        private void OnRegistrationLost(JoinCode code, string reason)
        {
            Post(() =>
            {
                if (_disposed || code != _publishedCode)
                {
                    return;
                }

                Debug.LogWarning("[MindAttic.Multiplayer] " + reason + " Registering again; the join code will change.");
                _publishedCode = JoinCode.Empty;
                _publishedBy = null;
                if (IsServer)
                {
                    SelfRegister();
                }
            });
        }

        private void Post(Action action)
        {
            if (_mainThread == null || SynchronizationContext.Current == _mainThread)
            {
                action();
            }
            else
            {
                _mainThread.Post(_ => action(), null);
            }
        }

        private void Fail(string message)
        {
            Debug.LogWarning("[MindAttic.Multiplayer] " + message);
            Raise(JoinCodeFailed, message);
        }

        /// <summary>Applies the config to the NetworkManager and its Unity Transport (added if missing).</summary>
        private UnityTransport Configure(NetConfig config)
        {
            if (_manager.NetworkConfig == null)
            {
                _manager.NetworkConfig = new NetworkConfig();
            }

            _manager.NetworkConfig.TickRate = (uint)config.TickRate;

            // Approval is always on, on both ends (it is part of NGO's config hash, so client and server must agree).
            _manager.NetworkConfig.ConnectionApproval = true;
            ResetSeats();
            if (config.Topology == Core.Topology.Client)
            {
                _manager.ConnectionApprovalCallback = null;
            }
            else
            {
                _admission = new ConnectionAdmission(
                    ConnectionApprovalValidator.FromConfig(config, ProtocolHash, BanList),
                    new SeatRegistry(config.MaxPlayers, config.ReconnectGraceSeconds));
                _manager.ConnectionApprovalCallback = OnApproval;
            }

            _manager.NetworkConfig.ConnectionData = config.Topology == Core.Topology.Server
                ? new byte[0]
                : ConnectionPayloadCodec.Encode(LocalPayload(config));

            UnityTransport transport = _manager.NetworkConfig.NetworkTransport as UnityTransport;
            if (transport == null)
            {
                transport = _manager.GetComponent<UnityTransport>();
            }

            if (transport == null)
            {
                transport = _manager.gameObject.AddComponent<UnityTransport>();
            }

            _manager.NetworkConfig.NetworkTransport = transport;
            transport.UseWebSockets = config.Transport == TransportKind.WebSocket;

            // forceOverrideCommandLineArgs: the config already folded in the command line (NetCommandLine),
            // so Unity Transport must not apply its own -port / -ip parsing on top.
            if (config.Topology == Core.Topology.Client)
            {
                transport.SetConnectionData(true, config.EffectiveAddress, config.Port);
            }
            else
            {
                transport.SetConnectionData(true, NetConfig.LoopbackAddress, config.Port, config.EffectiveAddress);
            }

            // SetConnectionData also switched the transport back from Relay to direct connections, so a Relay setup
            // (StartCore's transportSetup) must come after this.
            return transport;
        }

        private static NetConfig WithTopology(NetConfig config, Core.Topology topology)
        {
            NetConfig copy = config == null ? new NetConfig() : config.Clone();
            copy.Topology = topology;
            return copy;
        }

        private void OnServerStarted()
        {
            _roster.Refresh(); // a host's own seat is already taken: its roster is ready when Started is raised
            _machine.NotifyStarted();
        }

        private void OnServerStopped(bool wasHost)
        {
            StopShutdownTicks();
            if (_machine.State == SessionState.Stopping)
            {
                _machine.NotifyStopped();
            }
            else if (_machine.IsActive)
            {
                _machine.NotifyDisconnected(
                    _transportFailed ? DisconnectReason.TransportFailure : DisconnectReason.ServerShutdown,
                    _transportFailed ? "The network transport failed." : "The server stopped.");
            }
        }

        private void OnClientStopped(bool wasHost)
        {
            if (wasHost || !_machine.IsActive)
            {
                return; // a host's stop is handled by OnServerStopped; a client that already ended has nothing to do
            }

            // The disconnect callback normally ends the session first with a precise reason; this is the fallback.
            if (_transportFailed)
            {
                _machine.NotifyDisconnected(DisconnectReason.TransportFailure, "The network transport failed.");
            }
            else if (_machine.State == SessionState.Connecting)
            {
                _machine.NotifyDisconnected(DisconnectReason.ConnectFailed, "Could not reach the server.");
            }
            else
            {
                _machine.NotifyDisconnected(DisconnectReason.ServerShutdown, "The connection to the server closed.");
            }
        }

        private void OnClientConnected(ulong clientId)
        {
            if (_manager.IsServer)
            {
                SeatConnectedClient(clientId);
                Raise(ClientConnected, clientId);
                _roster.Refresh();
                return;
            }

            if (clientId == _manager.LocalClientId)
            {
                _machine.NotifyConnected();
                _roster.OnLocalClientConnected();
            }
        }

        private void OnClientDisconnected(ulong clientId)
        {
            if (_manager.IsServer)
            {
                if (clientId != NetworkManager.ServerClientId)
                {
                    OnRemoteClientDisconnected(clientId);
                }

                return;
            }

            if (_machine.IsActive)
            {
                Disconnection why = ClassifyClientDisconnect();
                _machine.NotifyDisconnected(why);
            }
        }

        private void OnTransportFailure()
        {
            _transportFailed = true;
        }

        /// <summary>Turns what NGO and the transport know about a client-side disconnect into a <see cref="DisconnectReason"/>.</summary>
        private Disconnection ClassifyClientDisconnect()
        {
            string reason = _manager.DisconnectReason ?? string.Empty;
            bool serverSentReason = reason.Length > 0 && !reason.StartsWith(TransportReasonPrefix, StringComparison.Ordinal);
            bool connecting = _machine.State == SessionState.Connecting;

            if (serverSentReason && Array.IndexOf(ServerShutdownReasons, reason.Trim()) >= 0)
            {
                return new Disconnection(DisconnectReason.ServerShutdown, reason);
            }

            if (serverSentReason)
            {
                ApprovalRejection rejection;
                string message;
                if (connecting && ApprovalDecision.TryParseWireReason(reason, out rejection, out message))
                {
                    return new Disconnection(DisconnectReason.Rejected, message, rejection);
                }

                return new Disconnection(connecting ? DisconnectReason.Rejected : DisconnectReason.Kicked, reason);
            }

            if (_transportFailed)
            {
                return new Disconnection(DisconnectReason.TransportFailure, reason);
            }

            NetworkTransport transport = _manager.NetworkConfig != null ? _manager.NetworkConfig.NetworkTransport : null;
            NetworkTransport.DisconnectEvents? transportEvent = transport != null ? transport.DisconnectEvent : (NetworkTransport.DisconnectEvents?)null;

            if (connecting)
            {
                return new Disconnection(
                    transportEvent == NetworkTransport.DisconnectEvents.AuthenticationFailure ? DisconnectReason.Rejected : DisconnectReason.ConnectFailed,
                    reason);
            }

            if (transportEvent == NetworkTransport.DisconnectEvents.ProtocolTimeout)
            {
                return new Disconnection(DisconnectReason.TimedOut, reason);
            }

            if (transportEvent == NetworkTransport.DisconnectEvents.ProtocolError)
            {
                return new Disconnection(DisconnectReason.TransportFailure, reason);
            }

            return new Disconnection(DisconnectReason.ServerShutdown, reason);
        }

        private void OnMachineStateChanged(SessionStateChange change)
        {
            bool live = change.To == SessionState.Running || change.To == SessionState.Connected;
            bool ended = change.To == SessionState.Disconnected
                || (change.To == SessionState.Idle && change.From != SessionState.Disconnected);

            if (live)
            {
                StartClock();
            }
            else if (ended || change.To == SessionState.Stopping)
            {
                StopClock();
            }

            if (ended)
            {
                ResetSeats();
                _scenes.Detach();
                _roster.Reset();
                _objects.Reset();
            }

            if (ended || change.To == SessionState.Stopping)
            {
                WithdrawPublishedCode();
            }

            if (change.To == SessionState.Running && _joinCodeOverride == null && JoinCodeDefaults.ShouldSelfRegister(_config))
            {
                SelfRegister();
            }

            Raise(StateChanged, change);

            if (live)
            {
                Raise(Started);
            }
            else if (ended)
            {
                Raise(Ended, change.Disconnection);
            }
        }

        private void StartClock()
        {
            StopClock();
            if (_manager.NetworkTickSystem != null)
            {
                _clock = new NetworkTickClock(_manager.NetworkTickSystem);
                _clock.Ticked += OnTick;
            }
        }

        private void StopClock()
        {
            if (_clock != null)
            {
                _clock.Ticked -= OnTick;
                _clock.Dispose();
                _clock = null;
            }
        }

        /// <summary>Seconds on the clock seats are held by: Unity's realtime clock, which runs while paused or unfocused.</summary>
        private static double Now
        {
            get { return Time.realtimeSinceStartupAsDouble; }
        }

        /// <summary>The payload this process sends as a player (Client, or a Host's own player).</summary>
        private ConnectionPayload LocalPayload(NetConfig config)
        {
            return _identity.ToPayload(config.GameVersion, ProtocolHash, config.Password);
        }

        /// <summary>NGO's approval callback (Host or Server): admit or refuse, and seat the admitted.</summary>
        private void OnApproval(NetworkManager.ConnectionApprovalRequest request, NetworkManager.ConnectionApprovalResponse response)
        {
            response.Pending = false;
            ulong clientId = request.ClientNetworkId;
            if (_admission == null)
            {
                response.Approved = false;
                response.Reason = ApprovalDecision.Reject(ApprovalRejection.Full, "The server is not accepting players.").ToWireReason();
                return;
            }

            Admission admission;
            try
            {
                admission = clientId == NetworkManager.ServerClientId && _config != null
                    ? _admission.AdmitLocal(clientId, LocalPayload(_config), Now)
                    : _admission.Admit(clientId, request.Payload, Now);
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                response.Approved = false;
                response.Reason = ApprovalDecision.Reject(ApprovalRejection.BadPayload, "The server could not process your connection request.").ToWireReason();
                return;
            }

            ReleaseExpired(admission.Expired);

            if (!admission.Approved)
            {
                response.Approved = false;
                response.CreatePlayerObject = false;
                response.Reason = admission.Decision.ToWireReason();
                _rejected.Add(clientId);
                Debug.Log("[MindAttic.Multiplayer] Refused client " + clientId + ": " + admission.Decision);
                Raise(ConnectionRejected, new ConnectionRejection(clientId, admission.Decision));
                return;
            }

            SeatClaim claim = admission.Claim;
            NetworkObject held;
            bool keepsPlayerObject = claim.IsReturn
                && _seatObjects.TryGetValue(claim.Seat.Index, out held) && held != null && held.IsSpawned;

            response.Approved = true;
            response.CreatePlayerObject = !keepsPlayerObject && _manager.NetworkConfig.PlayerPrefab != null;
            _reclaimOnConnect[clientId] = claim.IsReturn ? claim.Seat.Index : -1;

            if (claim.ReplacedClientId.HasValue)
            {
                // The same player is back on a new connection while its old one still looks alive (a killed
                // client the transport has not timed out yet). The old one is told why and leaves (or is closed after
                // the grace); HandOver takes the player object from it when the new one completes, even if it is still there.
                ulong stale = claim.ReplacedClientId.Value;
                if (!_leaving.ContainsKey(stale))
                {
                    Remove(stale, ReplacedMessage);
                }
            }
        }

        /// <summary>
        /// Server: removes a remote client with <paramref name="message"/> as its reason (<see cref="DisconnectReason.Kicked"/>
        /// at both ends). The reason is sent first, as <see cref="DisconnectNoticeMessage"/>, and the client closes its own
        /// connection on receipt; <see cref="OnTick"/> closes it from here after <see cref="LeaveGraceSeconds"/> if it has not.
        /// </summary>
        private void Remove(ulong clientId, string message)
        {
            _dropReasons[clientId] = new Disconnection(DisconnectReason.Kicked, message);
            if (!_manager.ConnectedClients.ContainsKey(clientId))
            {
                return;
            }

            CustomMessagingManager messaging = _manager.CustomMessagingManager;
            if (messaging != null)
            {
                try
                {
                    using (FastBufferWriter writer = new FastBufferWriter(FastBufferWriter.GetWriteSize(message), Allocator.Temp))
                    {
                        writer.WriteValueSafe(message);
                        messaging.SendNamedMessage(DisconnectNoticeMessage, clientId, writer, NetworkDelivery.ReliableSequenced);
                    }

                    _leaving[clientId] = Now + LeaveGraceSeconds;
                    return;
                }
                catch (Exception ex)
                {
                    Debug.LogException(ex);
                }
            }

            _manager.DisconnectClient(clientId, message);
        }

        /// <summary>Server: closes the connections of removed clients that have not left by themselves within the grace.</summary>
        private void CloseOverdueLeavers()
        {
            double now = Now;
            List<ulong> overdue = null;
            foreach (KeyValuePair<ulong, double> leaving in _leaving)
            {
                if (leaving.Value <= now)
                {
                    (overdue ?? (overdue = new List<ulong>())).Add(leaving.Key);
                }
            }

            if (overdue == null)
            {
                return;
            }

            for (int i = 0; i < overdue.Count; i++)
            {
                ulong clientId = overdue[i];
                _leaving.Remove(clientId);
                if (_manager.ConnectedClients.ContainsKey(clientId))
                {
                    Disconnection why;
                    _manager.DisconnectClient(clientId, _dropReasons.TryGetValue(clientId, out why) ? why.Message : null);
                }
            }
        }

        /// <summary>
        /// Client: the server is removing this client (<see cref="DisconnectNoticeMessage"/>). The session ends now with
        /// <see cref="DisconnectReason.Kicked"/> and the server's reason, and this end closes the connection.
        /// </summary>
        private void OnDisconnectNotice(ulong senderClientId, FastBufferReader reader)
        {
            if (_manager.IsServer || senderClientId != NetworkManager.ServerClientId || !_machine.IsActive)
            {
                return;
            }

            string message;
            try
            {
                reader.ReadValueSafe(out message);
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                message = null;
            }

            _machine.NotifyDisconnected(DisconnectReason.Kicked, string.IsNullOrEmpty(message) ? "You were removed from the server." : message);
            if (_manager.IsListening && !_manager.ShutdownInProgress)
            {
                _manager.Shutdown();
            }
        }

        /// <summary>
        /// Host or Server, leaving: tells every remote client the server is shutting down (<see cref="ShutdownNoticeMessage"/>)
        /// and waits for them to close their own connections; <see cref="OnShutdownTick"/> shuts NGO down once they have, or
        /// after <see cref="ShutdownGraceSeconds"/>. False when there is no one to tell (or no way to), and NGO should shut
        /// down at once.
        /// </summary>
        private bool AnnounceShutdown()
        {
            CustomMessagingManager messaging = _manager.CustomMessagingManager;
            NetworkTickSystem ticks = _manager.NetworkTickSystem;
            if (!_manager.IsServer || _manager.ShutdownInProgress || messaging == null || ticks == null)
            {
                return false;
            }

            var remote = new List<ulong>();
            foreach (ulong clientId in _manager.ConnectedClientsIds)
            {
                if (clientId != NetworkManager.ServerClientId)
                {
                    remote.Add(clientId);
                }
            }

            if (remote.Count == 0)
            {
                return false;
            }

            // NGO's own wording, so a client reads the same reason whichever way it hears of the shutdown.
            string message = "Disconnected due to " + (_manager.IsHost ? "host" : "server") + " shutting down.";
            try
            {
                using (FastBufferWriter writer = new FastBufferWriter(FastBufferWriter.GetWriteSize(message), Allocator.Temp))
                {
                    writer.WriteValueSafe(message);
                    messaging.SendNamedMessage(ShutdownNoticeMessage, remote, writer, NetworkDelivery.ReliableSequenced);
                }
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                return false;
            }

            for (int i = 0; i < remote.Count; i++)
            {
                _dropReasons[remote[i]] = new Disconnection(DisconnectReason.ServerShutdown, message);
            }

            _shutdownDeadline = Now + ShutdownGraceSeconds;
            _shutdownTicks = ticks;
            _shutdownTicks.Tick += OnShutdownTick;
            return true;
        }

        /// <summary>Host or Server, stopping: shuts NGO down once every remote client has gone, or the grace is over.</summary>
        private void OnShutdownTick()
        {
            if (Now >= _shutdownDeadline || _manager.ConnectedClientsIds.Count <= (_manager.IsHost ? 1 : 0))
            {
                FinishShutdown();
            }
        }

        /// <summary>Ends the wait <see cref="AnnounceShutdown"/> started (if any) and shuts NGO down.</summary>
        private void FinishShutdown()
        {
            if (_shutdownTicks == null)
            {
                return;
            }

            StopShutdownTicks();
            if (_manager.IsListening && !_manager.ShutdownInProgress)
            {
                _manager.Shutdown();
            }
        }

        private void StopShutdownTicks()
        {
            if (_shutdownTicks != null)
            {
                _shutdownTicks.Tick -= OnShutdownTick;
                _shutdownTicks = null;
            }
        }

        /// <summary>
        /// Client: the server is shutting down (<see cref="ShutdownNoticeMessage"/>). The session ends now with
        /// <see cref="DisconnectReason.ServerShutdown"/>, and this end closes the connection.
        /// </summary>
        private void OnShutdownNotice(ulong senderClientId, FastBufferReader reader)
        {
            if (_manager.IsServer || senderClientId != NetworkManager.ServerClientId || !_machine.IsActive)
            {
                return;
            }

            string message;
            try
            {
                reader.ReadValueSafe(out message);
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                message = null;
            }

            _machine.NotifyDisconnected(DisconnectReason.ServerShutdown, string.IsNullOrEmpty(message) ? "The server shut down." : message);
            if (_manager.IsListening && !_manager.ShutdownInProgress)
            {
                _manager.Shutdown();
            }
        }

        /// <summary>Server: a client finished connecting; keep its player object with its seat (or hand the held one back).</summary>
        private void SeatConnectedClient(ulong clientId)
        {
            Seat seat;
            if (_admission == null || !_admission.Seats.TryGetByClient(clientId, out seat))
            {
                return;
            }

            int reclaimIndex;
            bool returning = _reclaimOnConnect.TryGetValue(clientId, out reclaimIndex) && reclaimIndex >= 0;
            _reclaimOnConnect.Remove(clientId);

            NetworkObject playerObject;
            if (returning && _seatObjects.TryGetValue(seat.Index, out playerObject) && playerObject != null && playerObject.IsSpawned)
            {
                HandOver(playerObject, clientId);
            }
            else
            {
                NetworkClient client;
                playerObject = _manager.ConnectedClients.TryGetValue(clientId, out client) ? client.PlayerObject : null;
            }

            if (playerObject != null)
            {
                // Kept on disconnect (NGO hands it to the server) so it can wait for the player with the seat.
                playerObject.DontDestroyWithOwner = true;
                _seatObjects[seat.Index] = playerObject;
            }

            Raise(returning ? SeatReclaimed : SeatTaken, seat);
        }

        /// <summary>Makes a held player object <paramref name="clientId"/>'s player object again: same GameObject, same state.</summary>
        private void HandOver(NetworkObject playerObject, ulong clientId)
        {
            ulong previousOwner = playerObject.OwnerClientId;
            if (previousOwner != NetworkManager.ServerClientId && previousOwner != clientId
                && _manager.ConnectedClients.ContainsKey(previousOwner) && !_leaving.ContainsKey(previousOwner))
            {
                Debug.LogWarning("[MindAttic.Multiplayer] Seat player object is still owned by connected client "
                    + playerObject.OwnerClientId + "; not handing it to client " + clientId + ".");
                return;
            }

            // NGO has no public "make this the client's player object" for a live object, so it is respawned:
            // the server keeps the same instance (and every component's state); clients get a fresh spawn of it.
            playerObject.Despawn(false);

            // A replaced connection still being told why (see Remove) may still hold it as its player object: NGO clears
            // that only on a destroying despawn, and would strip the new owner's ownership when the old connection leaves.
            NetworkClient previous;
            if (previousOwner != clientId && _manager.ConnectedClients.TryGetValue(previousOwner, out previous)
                && previous.PlayerObject == playerObject)
            {
                previous.PlayerObject = null;
            }

            playerObject.SpawnAsPlayerObject(clientId);
        }

        /// <summary>Server: a remote client's connection ended; hold or free its seat and report why.</summary>
        private void OnRemoteClientDisconnected(ulong clientId)
        {
            HoldOrFreeSeat(clientId);
            _roster.OnClientGone(clientId);
            _roster.Refresh();
        }

        private void HoldOrFreeSeat(ulong clientId)
        {
            _reclaimOnConnect.Remove(clientId);
            if (_rejected.Remove(clientId))
            {
                return; // never admitted: ConnectionRejected already said why
            }

            Disconnection why;
            if (!_dropReasons.TryGetValue(clientId, out why))
            {
                NetworkTransport transport = _manager.NetworkConfig != null ? _manager.NetworkConfig.NetworkTransport : null;
                why = transport != null && transport.DisconnectEvent == NetworkTransport.DisconnectEvents.ProtocolTimeout
                    ? new Disconnection(DisconnectReason.TimedOut, "The connection stopped responding.")
                    : new Disconnection(DisconnectReason.ClientLeft, "The client closed its connection.");
            }

            _dropReasons.Remove(clientId);
            _leaving.Remove(clientId);
            _gate.Forget(clientId);

            Seat seat = _admission != null ? _admission.Seats.Release(clientId, Now) : null;
            Raise(ClientDisconnected, clientId);
            Raise(PlayerDisconnected, new PlayerDisconnect(clientId, why, seat));

            if (seat == null)
            {
                return;
            }

            if (seat.Status == SeatStatus.Held)
            {
                Raise(SeatHeld, seat);
            }
            else
            {
                ReleaseSeatObject(seat);
                Raise(SeatReleased, seat);
            }
        }

        private void OnTick(long tick)
        {
            if (_leaving.Count > 0)
            {
                CloseOverdueLeavers();
            }

            if (_admission != null && _admission.Seats.HeldCount > 0)
            {
                ReleaseExpired(_admission.Seats.Expire(Now));
            }

            _roster.Tick(Now);
        }

        private void ReleaseExpired(IReadOnlyList<Seat> expired)
        {
            for (int i = 0; i < expired.Count; i++)
            {
                ReleaseSeatObject(expired[i]);
                Raise(SeatReleased, expired[i]);
            }

            if (expired.Count > 0)
            {
                _roster.Refresh();
            }
        }

        /// <summary>Despawns and destroys the player object kept with a freed seat.</summary>
        private void ReleaseSeatObject(Seat seat)
        {
            NetworkObject playerObject;
            if (!_seatObjects.TryGetValue(seat.Index, out playerObject))
            {
                return;
            }

            _seatObjects.Remove(seat.Index);
            if (playerObject != null && playerObject.IsSpawned && _manager.IsServer && !_manager.ShutdownInProgress)
            {
                playerObject.Despawn(true);
            }
        }

        /// <summary>Forgets every seat (the server stopped, or a new session is starting).</summary>
        private void ResetSeats()
        {
            if (_admission != null)
            {
                _admission.Seats.Clear();
                _admission = null;
            }

            _gate.Clear();
            _seatObjects.Clear();
            _reclaimOnConnect.Clear();
            _dropReasons.Clear();
            _leaving.Clear();
            _rejected.Clear();
        }

        private static void Raise(Action handler)
        {
            if (handler == null)
            {
                return;
            }

            try
            {
                handler();
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
            }
        }

        private static void Raise<T>(Action<T> handler, T value)
        {
            if (handler == null)
            {
                return;
            }

            try
            {
                handler(value);
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
            }
        }
    }
}
