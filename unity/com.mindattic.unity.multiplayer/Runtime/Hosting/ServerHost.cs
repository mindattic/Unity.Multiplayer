using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using Unity.Multiplayer.Core;
using UnityEngine;

namespace Unity.Multiplayer
{
    /// <summary>
    /// Runs a dedicated server the way the server package promises (<see cref="ServerPackageSpec"/>): log files in
    /// <c>Logs/</c>, the game's data in <c>Saves/</c>, the health endpoint, the optional admin console on standard input,
    /// and a clean shutdown on SIGTERM, SIGINT, the <c>stop</c> command or a quit. <see cref="NetBootstrap"/> starts one
    /// for a dedicated server in a player build; a game with its own bootstrap calls <see cref="Load"/> and
    /// <see cref="Attach"/>.
    /// </summary>
    /// <remarks>
    /// <para>A clean shutdown: the health endpoint turns 503, <see cref="ShuttingDown"/> fires (save the game to
    /// <see cref="Paths"/>.SavesDirectory there), the session closes (players are told, the rendezvous registration is
    /// dropped), then the process quits with exit code 0, or after <see cref="ServerSettings.ShutdownGraceSeconds"/>
    /// whatever is still running.</para>
    /// <para>SIGINT arrives as <c>Console.CancelKeyPress</c>. Unity's Linux player turns SIGTERM into a quit, which
    /// <c>Application.wantsToQuit</c> holds back until the clean shutdown is done; <c>Application.quitting</c> and
    /// process exit do what they still can if a quit gets through anyway.</para>
    /// </remarks>
    [DisallowMultipleComponent]
    [AddComponentMenu("")]
    public sealed class ServerHost : MonoBehaviour
    {
        private readonly ConcurrentQueue<string> _adminLines = new ConcurrentQueue<string>();
        private readonly ServerShutdown _shutdown = new ServerShutdown();
        private NetSession _session;
        private NetConfig _config;
        private ServerLogFile _log;
        private HealthEndpoint _health;
        private HealthReport _report = new HealthReport { Status = HealthStatus.Starting };
        private Thread _adminThread;
        private bool _closingSession;
        private bool _finished;
        private bool _quitApproved;
        private ConsoleCancelEventHandler _cancelHandler;
        private EventHandler _exitHandler;

        /// <summary>The running server's host, or null.</summary>
        public static ServerHost Current { get; private set; }

        /// <summary>Fires once on the main thread when a clean shutdown starts, before the session closes: save here.</summary>
        public event Action<string> ShuttingDown;

        /// <summary>The package settings in force.</summary>
        public ServerSettings Settings { get; private set; }

        /// <summary>The package, logs and saves folders. Keep everything that must survive a restart under SavesDirectory.</summary>
        public ServerPaths Paths { get; private set; }

        /// <summary>The admin commands; add a game's own with <see cref="AdminConsole.Register"/>.</summary>
        public AdminConsole Admin { get; } = new AdminConsole();

        /// <summary>The log file this run writes, or null when it could not be opened.</summary>
        public string LogFilePath
        {
            get { return _log != null ? _log.Path : null; }
        }

        /// <summary>The health endpoint's port, or 0 when it is off.</summary>
        public int HealthPort
        {
            get { return _health != null ? _health.Port : 0; }
        }

        /// <summary>True once a clean shutdown has been asked for.</summary>
        public bool IsShuttingDown
        {
            get { return _shutdown.IsRequested; }
        }

        /// <summary>
        /// The package folder of this build: the folder holding the executable (the parent of <c>&lt;Name&gt;_Data</c>);
        /// in the editor, the project folder.
        /// </summary>
        public static string PackageDirectory
        {
            get
            {
                string parent = Path.GetDirectoryName(Application.dataPath);
                return string.IsNullOrEmpty(parent) ? Directory.GetCurrentDirectory() : parent;
            }
        }

        /// <summary>
        /// Layers <c>server.json</c>, the environment and the command line onto <paramref name="config"/>
        /// (<see cref="ServerStartup.Load"/>) and logs every warning and error.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="config"/> is null.</exception>
        public static ServerStartupResult Load(NetConfig config)
        {
            ServerStartupResult result = ServerStartup.Load(config, Environment.GetCommandLineArgs(), PackageDirectory);
            Debug.Log("[MindAttic.Multiplayer] Server package: " + (result.ConfigPath ?? "no " + ServerConfigFile.FileName + ", defaults")
                + "; " + result.Settings);
            foreach (string warning in result.Warnings)
            {
                Debug.LogWarning("[MindAttic.Multiplayer] " + warning);
            }

            foreach (string error in result.Errors)
            {
                Debug.LogError("[MindAttic.Multiplayer] " + error);
            }

            return result;
        }

        /// <summary>
        /// Adds a host to <paramref name="owner"/> for the running dedicated server <paramref name="session"/>: opens the
        /// log file, starts the health endpoint and the admin console, and listens for shutdown signals.
        /// </summary>
        /// <exception cref="ArgumentNullException">An argument is null.</exception>
        public static ServerHost Attach(GameObject owner, NetSession session, NetConfig config, ServerStartupResult startup)
        {
            if (owner == null)
            {
                throw new ArgumentNullException(nameof(owner));
            }

            if (session == null)
            {
                throw new ArgumentNullException(nameof(session));
            }

            if (config == null)
            {
                throw new ArgumentNullException(nameof(config));
            }

            if (startup == null)
            {
                throw new ArgumentNullException(nameof(startup));
            }

            // Not `??`: in the Editor a missing component comes back as Unity's fake-null object, which `??` keeps.
            ServerHost host = owner.GetComponent<ServerHost>();
            if (host == null)
            {
                host = owner.AddComponent<ServerHost>();
            }

            host.Begin(session, config, startup);
            return host;
        }

        /// <summary>Asks for a clean shutdown (from any thread). Only the first request counts.</summary>
        public void RequestShutdown(string reason)
        {
            if (_shutdown.Request(reason, Now))
            {
                Debug.Log("[MindAttic.Multiplayer] Shutting down (" + _shutdown.Reason + ").");
            }
        }

        private static double Now
        {
            get { return DateTime.UtcNow.Ticks / (double)TimeSpan.TicksPerSecond; }
        }

        private void Begin(NetSession session, NetConfig config, ServerStartupResult startup)
        {
            _session = session;
            _config = config.Clone();
            Settings = startup.Settings;
            Paths = startup.Paths;
            Current = this;

            try
            {
                Paths.EnsureCreated();
                _log = ServerLogFile.Open(Paths.LogsDirectory, DateTime.UtcNow, Settings.KeepLogFiles);
                Application.logMessageReceivedThreaded += OnLog;
                _log.Write(DateTime.UtcNow, "Info", "Server starting: " + _config + "; " + Settings);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                Debug.LogWarning("[MindAttic.Multiplayer] Could not open a log file in " + Paths.LogsDirectory + ": " + ex.Message);
            }

            UpdateReport();
            if (Settings.HealthPort != 0)
            {
                try
                {
                    _health = new HealthEndpoint(() => Volatile.Read(ref _report));
                    _health.Start(Settings.HealthPort);
                    Debug.Log("[MindAttic.Multiplayer] Health endpoint on :" + _health.Port + ServerSettings.HealthPath);
                }
                catch (System.Net.Sockets.SocketException ex)
                {
                    Debug.LogError("[MindAttic.Multiplayer] Health endpoint could not listen on " + Settings.HealthPort + ": " + ex.Message);
                    _health = null;
                }
            }

            RegisterBuiltInCommands();
            if (Settings.AdminCommands)
            {
                _adminThread = new Thread(ReadAdminLines) { IsBackground = true, Name = "MindAttic admin console" };
                _adminThread.Start();
                Debug.Log("[MindAttic.Multiplayer] Admin commands on standard input; type help.");
            }

            _cancelHandler = (sender, e) =>
            {
                e.Cancel = true;
                RequestShutdown("SIGINT");
            };
            Console.CancelKeyPress += _cancelHandler;
            _exitHandler = (sender, e) => RequestShutdown("process exit");
            AppDomain.CurrentDomain.ProcessExit += _exitHandler;
            Application.wantsToQuit += OnWantsToQuit;
            Application.quitting += OnQuitting;
        }

        private void RegisterBuiltInCommands()
        {
            Admin.Register("status", "status", "Shows the server's state, players and uptime.", args => _report.ToJson());
            Admin.Register("players", "players", "Lists connected players with their client ids.", args => ListPlayers());
            Admin.Register("kick", "kick <clientId> [reason]", "Removes a player; the reason is shown to them.", args =>
            {
                if (args.Count < 1 || !ulong.TryParse(args[0], out ulong clientId))
                {
                    throw new ArgumentException("Give the player's client id (see players).");
                }

                string reason = args.Count > 1 ? string.Join(" ", Skip(args, 1)) : null;
                return _session.Kick(clientId, reason) ? "Kicked " + clientId + "." : "No connected player has client id " + clientId + ".";
            });
            Admin.Register("stop", "stop", "Shuts the server down cleanly.", args =>
            {
                RequestShutdown("admin stop");
                return "Stopping.";
            });
        }

        private string ListPlayers()
        {
            IReadOnlyList<ulong> ids = _session.ConnectedClientIds;
            if (ids.Count == 0)
            {
                return "No players connected.";
            }

            var text = new StringBuilder(ids.Count + " player(s):");
            foreach (ulong id in ids)
            {
                text.Append("\n  ").Append(id);
                if (_session.TryGetSeat(id, out Seat seat))
                {
                    text.Append("  seat ").Append(seat.Index).Append("  ").Append(seat.DisplayName);
                }
            }

            return text.ToString();
        }

        private static IEnumerable<string> Skip(IReadOnlyList<string> list, int count)
        {
            for (int i = count; i < list.Count; i++)
            {
                yield return list[i];
            }
        }

        private void ReadAdminLines()
        {
            try
            {
                string line;
                while ((line = Console.In.ReadLine()) != null)
                {
                    _adminLines.Enqueue(line);
                }
            }
            catch (Exception)
            {
                // No usable standard input (detached container): the console is simply quiet.
            }
        }

        private void Update()
        {
            while (_adminLines.TryDequeue(out string line))
            {
                AdminCommandResult result = Admin.Execute(line);
                if (result.Output.Length > 0)
                {
                    if (result.Succeeded)
                    {
                        Debug.Log("[Admin] " + result.Output);
                    }
                    else
                    {
                        Debug.LogWarning("[Admin] " + result.Output);
                    }
                }
            }

            UpdateReport();
            if (!_shutdown.IsRequested || _finished)
            {
                return;
            }

            if (!_closingSession)
            {
                _closingSession = true;
                RaiseShuttingDown();
                if (_session != null)
                {
                    _session.Leave();
                }
            }

            bool sessionClosed = _session == null || _session.State == SessionState.Idle || _session.State == SessionState.Disconnected;
            if (sessionClosed || _shutdown.IsOverdue(Now, Settings.ShutdownGraceSeconds))
            {
                if (!sessionClosed)
                {
                    Debug.LogWarning("[MindAttic.Multiplayer] The session did not close within " + Settings.ShutdownGraceSeconds + " s; quitting anyway.");
                }

                Finish();
                _quitApproved = true;
                Application.Quit(0);
            }
        }

        private void RaiseShuttingDown()
        {
            try
            {
                ShuttingDown?.Invoke(_shutdown.Reason);
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
            }
        }

        private void UpdateReport()
        {
            HealthStatus status = _shutdown.IsRequested
                ? HealthStatus.Stopping
                : _session != null && _session.State == SessionState.Running ? HealthStatus.Running : HealthStatus.Starting;
            Volatile.Write(ref _report, new HealthReport
            {
                Status = status,
                Name = _config != null ? _config.ServerName ?? string.Empty : string.Empty,
                Version = _config != null ? _config.GameVersion : string.Empty,
                Players = _session != null ? _session.ConnectedClientIds.Count : 0,
                MaxPlayers = _config != null ? _config.MaxPlayers : 0,
                Port = _config != null ? _config.Port : 0,
                Transport = _config != null ? _config.Transport : TransportKind.Udp,
                PasswordProtected = _config != null && !string.IsNullOrEmpty(_config.Password),
                UptimeSeconds = Time.realtimeSinceStartupAsDouble,
            });
        }

        private bool OnWantsToQuit()
        {
            if (_quitApproved || _finished)
            {
                return true;
            }

            // A SIGTERM (or any other quit) waits for the clean shutdown, which quits itself when done.
            RequestShutdown("quit");
            return false;
        }

        private void OnQuitting()
        {
            if (_finished)
            {
                return;
            }

            // The quit got through without the clean shutdown: do what still can be done, in order.
            RequestShutdown("quit");
            if (!_closingSession)
            {
                _closingSession = true;
                RaiseShuttingDown();
                if (_session != null)
                {
                    _session.Leave();
                }
            }

            Finish();
        }

        private void OnLog(string message, string stackTrace, LogType type)
        {
            ServerLogFile log = _log;
            if (log == null)
            {
                return;
            }

            string level = type == LogType.Log ? "Info" : type.ToString();
            bool withStack = (type == LogType.Exception || type == LogType.Error) && !string.IsNullOrEmpty(stackTrace);
            log.Write(DateTime.UtcNow, level, withStack ? message + "\n" + stackTrace.TrimEnd() : message);
        }

        private void Finish()
        {
            if (_finished)
            {
                return;
            }

            _finished = true;
            if (_health != null)
            {
                _health.Dispose();
                _health = null;
            }

            if (_log != null)
            {
                _log.Write(DateTime.UtcNow, "Info", "Server stopped (" + (_shutdown.Reason ?? "quit") + ").");
                Application.logMessageReceivedThreaded -= OnLog;
                _log.Dispose();
                _log = null;
            }
        }

        private void OnDestroy()
        {
            Finish();
            if (_cancelHandler != null)
            {
                Console.CancelKeyPress -= _cancelHandler;
            }

            if (_exitHandler != null)
            {
                AppDomain.CurrentDomain.ProcessExit -= _exitHandler;
            }

            Application.wantsToQuit -= OnWantsToQuit;
            Application.quitting -= OnQuitting;
            if (Current == this)
            {
                Current = null;
            }
        }
    }
}
