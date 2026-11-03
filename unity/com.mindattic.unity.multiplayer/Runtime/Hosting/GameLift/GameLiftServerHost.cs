using System;
using System.Collections.Generic;
using Unity.Multiplayer.Core;
using UnityEngine;
#if MINDATTIC_GAMELIFT
using Aws.GameLift.Server;
using Aws.GameLift.Server.Model;
#endif

namespace Unity.Multiplayer
{
    /// <summary>
    /// Wires a dedicated server into AWS GameLift (the <see cref="GameLiftProfile"/> hosting profile):
    /// <c>InitSDK</c>, <c>ProcessReady</c> with its <c>OnStartGameSession</c>/<c>OnProcessTerminate</c>/
    /// <c>OnHealthCheck</c> callbacks, <c>ActivateGameSession</c> once the callback fires,
    /// <c>AcceptPlayerSession</c>/<c>RemovePlayerSession</c> as players join and leave, and
    /// <c>ProcessEnding</c> on a clean shutdown. Compiled in only when the GameLift Server SDK package
    /// is installed (it defines <c>MINDATTIC_GAMELIFT</c>); otherwise <see cref="IsAvailable"/> is false
    /// and <see cref="Attach"/> is a no-op, so a game (or <see cref="NetBootstrap"/>) can call it
    /// unconditionally alongside every other hosting profile.
    /// </summary>
    /// <remarks>
    /// <para>Not a thing a game configures the way it does a <see cref="NetBootstrap"/> field: GameLift
    /// fleets run the exact same server package every other host runs (<see cref="ServerHost"/> already
    /// started it, with its own log file, health reporting and shutdown handling), and this component
    /// only makes that same running process also behave like a GameLift server process. It attaches
    /// itself to <see cref="NetBootstrap"/>'s dedicated-server start right after <see cref="ServerHost"/>
    /// does; a game with its own bootstrap can call <see cref="Attach"/> the same way.</para>
    /// <para>A player's <see cref="ConnectionPayload.PlayerToken"/> doubles as the GameLift player
    /// session id when running under GameLift: a matchmaker gets that id from
    /// <c>GameLiftMatchAllocator.CreatePlayerSessionAsync</c> (<c>Unity.Multiplayer.Core</c>) and must
    /// hand it to the player to send as their player token. <see cref="AcceptConnectedPlayer"/> and
    /// <see cref="RemoveDisconnectedPlayer"/> read it back off the seat <see cref="NetSession"/> already
    /// keeps (<see cref="NetSession.TryGetSeat"/>) -- no new wire field, no change to approval.</para>
    /// <para>Method and type names follow the GameLift Server SDK's publicly documented 5.x C# API
    /// (<c>Aws.GameLift.Server.GameLiftServerAPI</c>/<c>Aws.GameLift.Server.Model</c>) as of this
    /// writing; verify them against the SDK version you actually install before depending on this for
    /// a public launch -- the asmdef's <c>versionDefines</c> expression pins the package version this
    /// was written against, and this class is never compiled by <c>dotnet test</c> (it needs the real
    /// SDK and UnityEngine, like every other Runtime-not-Core file in this repo), so a drift here is
    /// only caught by actually building the Unity project with the SDK installed.</para>
    /// </remarks>
    [DisallowMultipleComponent]
    [AddComponentMenu("")]
    public sealed class GameLiftServerHost : MonoBehaviour
    {
        private ServerHost _host;
        private NetSession _session;
#if MINDATTIC_GAMELIFT
        private bool _started;
#endif

        /// <summary>True when the GameLift Server SDK is compiled in (its package is installed).</summary>
        public static bool IsAvailable
        {
            get
            {
#if MINDATTIC_GAMELIFT
                return true;
#else
                return false;
#endif
            }
        }

        /// <summary>The running server's GameLift host, or null.</summary>
        public static GameLiftServerHost Current { get; private set; }

        /// <summary>
        /// Adds a GameLift host to <paramref name="owner"/> for the already-running dedicated server
        /// <paramref name="session"/>/<paramref name="host"/>: InitSDK, ProcessReady on
        /// <paramref name="gamePort"/>, and the player-session/shutdown wiring. Does nothing (and
        /// returns null) when <see cref="IsAvailable"/> is false.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="owner"/>, <paramref name="host"/> or <paramref name="session"/> is null.</exception>
        public static GameLiftServerHost Attach(GameObject owner, ServerHost host, NetSession session, int gamePort)
        {
            if (owner == null)
            {
                throw new ArgumentNullException(nameof(owner));
            }

            if (host == null)
            {
                throw new ArgumentNullException(nameof(host));
            }

            if (session == null)
            {
                throw new ArgumentNullException(nameof(session));
            }

            if (!IsAvailable)
            {
                Debug.Log("[MindAttic.Multiplayer] GameLift Server SDK not installed; GameLiftServerHost stays inactive.");
                return null;
            }

            // Not `??`: in the Editor a missing component comes back as Unity's fake-null object, which `??` keeps.
            GameLiftServerHost component = owner.GetComponent<GameLiftServerHost>();
            if (component == null)
            {
                component = owner.AddComponent<GameLiftServerHost>();
            }

            component.Begin(host, session, gamePort);
            return component;
        }

        private void Begin(ServerHost host, NetSession session, int gamePort)
        {
            _host = host;
            _session = session;
            Current = this;

#if MINDATTIC_GAMELIFT
            GenericOutcome initOutcome = GameLiftServerAPI.InitSDK();
            if (!initOutcome.Success)
            {
                Debug.LogError("[MindAttic.Multiplayer] GameLift InitSDK failed: " + initOutcome.Error.ErrorMessage);
                return;
            }

            var logParameters = new LogParameters(new List<string> { host.Paths.LogsDirectory });
            var processParameters = new ProcessParameters(
                OnStartGameSession,
                OnUpdateGameSession,
                OnProcessTerminate,
                OnHealthCheck,
                gamePort,
                logParameters);

            GenericOutcome readyOutcome = GameLiftServerAPI.ProcessReady(processParameters);
            if (!readyOutcome.Success)
            {
                Debug.LogError("[MindAttic.Multiplayer] GameLift ProcessReady failed: " + readyOutcome.Error.ErrorMessage);
                return;
            }

            _started = true;
            session.ClientConnected += AcceptConnectedPlayer;
            session.ClientDisconnected += RemoveDisconnectedPlayer;
            host.ShuttingDown += OnShuttingDown;
            Debug.Log("[MindAttic.Multiplayer] GameLift process ready on port " + gamePort + ".");
#else
            Debug.Log("[MindAttic.Multiplayer] GameLift Server SDK not installed; GameLiftServerHost stays inactive.");
#endif
        }

#if MINDATTIC_GAMELIFT
        private void OnStartGameSession(GameSession gameSession)
        {
            // ServerHost already started the dedicated server and it is already accepting NGO
            // connections; GameLift only needs to be told the session this process got is servable.
            GenericOutcome outcome = GameLiftServerAPI.ActivateGameSession();
            if (!outcome.Success)
            {
                Debug.LogError("[MindAttic.Multiplayer] GameLift ActivateGameSession failed: " + outcome.Error.ErrorMessage);
            }
        }

        private void OnUpdateGameSession(UpdateGameSession update)
        {
            Debug.Log("[MindAttic.Multiplayer] GameLift updated the game session (" + update.UpdateReason + ").");
        }

        private void OnProcessTerminate()
        {
            if (_host != null)
            {
                _host.RequestShutdown("GameLift ProcessTerminate");
            }
        }

        private bool OnHealthCheck()
        {
            return _host != null && !_host.IsShuttingDown;
        }

        private void AcceptConnectedPlayer(ulong clientId)
        {
            Seat seat;
            if (!_started || _session == null || !_session.TryGetSeat(clientId, out seat) || string.IsNullOrEmpty(seat.PlayerToken))
            {
                return;
            }

            GenericOutcome outcome = GameLiftServerAPI.AcceptPlayerSession(seat.PlayerToken);
            if (!outcome.Success)
            {
                Debug.LogWarning("[MindAttic.Multiplayer] GameLift AcceptPlayerSession(" + seat.PlayerToken + ") failed: " + outcome.Error.ErrorMessage);
            }
        }

        private void RemoveDisconnectedPlayer(ulong clientId)
        {
            Seat seat;
            if (!_started || _session == null || !_session.TryGetSeat(clientId, out seat) || string.IsNullOrEmpty(seat.PlayerToken))
            {
                return;
            }

            GameLiftServerAPI.RemovePlayerSession(seat.PlayerToken);
        }

        private void OnShuttingDown(string reason)
        {
            if (!_started)
            {
                return;
            }

            GameLiftServerAPI.ProcessEnding();
        }
#endif

        private void OnDestroy()
        {
#if MINDATTIC_GAMELIFT
            if (_started)
            {
                GameLiftServerAPI.Destroy();
                _started = false;
            }

            if (_session != null)
            {
                _session.ClientConnected -= AcceptConnectedPlayer;
                _session.ClientDisconnected -= RemoveDisconnectedPlayer;
            }

            if (_host != null)
            {
                _host.ShuttingDown -= OnShuttingDown;
            }
#endif
            if (Current == this)
            {
                Current = null;
            }
        }
    }
}
