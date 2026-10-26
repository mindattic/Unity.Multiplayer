using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Unity.Multiplayer.Core;
using UnityEngine;

namespace Unity.Multiplayer
{
    /// <summary>Where a <see cref="NetBotRunner"/> is in its run.</summary>
    public enum NetBotState
    {
        /// <summary>Not started.</summary>
        Idle = 0,

        /// <summary>Waiting for the join-code file to appear.</summary>
        WaitingForCode,

        /// <summary>Joining the server.</summary>
        Joining,

        /// <summary>Connected; the game's driver is playing.</summary>
        Running,

        /// <summary>The driver passed the run (it may still be playing).</summary>
        Passed,

        /// <summary>The run failed: see <see cref="NetBotRunner.FailureReason"/>.</summary>
        Failed,
    }

    /// <summary>How a <see cref="NetBotRunner"/> joins and plays.</summary>
    public sealed class NetBotOptions
    {
        /// <summary>The role the bot plays; required.</summary>
        public string Role { get; set; }

        /// <summary>The code to join by; when null, the code in <see cref="JoinCodeFile"/>.</summary>
        public string JoinCode { get; set; }

        /// <summary>A file to wait for and read the join code from (a server started with <c>-joincodefile</c> writes it).</summary>
        public string JoinCodeFile { get; set; }

        /// <summary>The scenario handed to the driver (<see cref="NetBot.Scenario"/>).</summary>
        public string Scenario { get; set; }

        /// <summary>
        /// The settings to join with: the rendezvous URL for a code, the version, the transport. With neither a code nor a
        /// code file, the bot joins <see cref="NetConfig.Address"/>:<see cref="NetConfig.Port"/> directly.
        /// </summary>
        public NetConfig JoinConfig { get; set; }

        /// <summary>Seconds the run may take before it fails (until the driver passes it).</summary>
        public float TimeoutSeconds { get; set; } = (float)BotCommandLine.DefaultTimeoutSeconds;

        /// <summary>The name the bot plays as; null for <c>Bot &lt;role&gt;</c>. The colour and token stay the session's.</summary>
        public string DisplayName { get; set; }

        /// <summary>Quit the player with exit code 1 when the run fails (never in the editor). <c>-bot</c> launches set it.</summary>
        public bool QuitOnFailure { get; set; }
    }

    /// <summary>
    /// Runs one bot (UM-T57): gets the join code (given, or from a file once it appears), joins, waits until connected,
    /// then hands a <see cref="NetBot"/> to the game's <see cref="IBotDriver"/>. Logs <c>[NetBot:role] OK: ...</c> when the
    /// driver passes and <c>[NetBot:role] FAIL: ...</c> on any failure: a refused join, a timeout, the session ending
    /// before a pass, the driver throwing. A <c>-bot</c> launch makes one by itself (<see cref="NetBots"/>); tests make
    /// them with <see cref="Run"/> (or the test kit's <c>NetBotTest</c>).
    /// </summary>
    [AddComponentMenu("")]
    public sealed class NetBotRunner : MonoBehaviour
    {
        private readonly List<FrameWaiter> _frameWaiters = new List<FrameWaiter>();
        private NetSession _session;
        private IBotDriver _driver;
        private NetBotOptions _options;
        private float _deadline;
        private bool _finished;

        /// <summary>The role this bot plays.</summary>
        public string Role { get; private set; }

        /// <summary>Where the run is.</summary>
        public NetBotState State { get; private set; }

        /// <summary>Why it failed; null unless <see cref="State"/> is <see cref="NetBotState.Failed"/>.</summary>
        public string FailureReason { get; private set; }

        /// <summary>What the driver passed with; null until it has.</summary>
        public string PassMessage { get; private set; }

        /// <summary>The driver's context; null until the bot has joined.</summary>
        public NetBot Bot { get; private set; }

        /// <summary>The session the bot plays on.</summary>
        public NetSession Session
        {
            get { return _session; }
        }

        /// <summary>True once the run passed or failed.</summary>
        public bool IsFinished
        {
            get { return _finished; }
        }

        /// <summary>Raised once, when the run passes or fails.</summary>
        public event Action<NetBotRunner> Finished;

        internal float SecondsLeft
        {
            get
            {
                if (State == NetBotState.Passed)
                {
                    return float.PositiveInfinity;
                }

                return Mathf.Max(0f, _deadline - Time.realtimeSinceStartup);
            }
        }

        /// <summary>
        /// Starts a bot on <paramref name="host"/>: <paramref name="session"/> (idle, with the game's prefabs registered)
        /// joins as <paramref name="options"/> says and then <paramref name="driver"/> plays.
        /// </summary>
        /// <exception cref="ArgumentNullException">Any argument is null.</exception>
        /// <exception cref="ArgumentException">No role, or no join config.</exception>
        public static NetBotRunner Run(GameObject host, NetSession session, IBotDriver driver, NetBotOptions options)
        {
            if (host == null) throw new ArgumentNullException(nameof(host));
            if (session == null) throw new ArgumentNullException(nameof(session));
            if (driver == null) throw new ArgumentNullException(nameof(driver));
            if (options == null) throw new ArgumentNullException(nameof(options));
            if (string.IsNullOrWhiteSpace(options.Role)) throw new ArgumentException("A bot needs a role.", nameof(options));
            if (options.JoinConfig == null) throw new ArgumentException("A bot needs a JoinConfig.", nameof(options));

            NetBotRunner runner = host.AddComponent<NetBotRunner>();
            runner.Role = options.Role.Trim();
            runner._session = session;
            runner._driver = driver;
            runner._options = options;
            runner._deadline = Time.realtimeSinceStartup + Mathf.Max(1f, options.TimeoutSeconds);
            runner.Bot = new NetBot(runner, session, runner.Role, options.Scenario);
            session.Identity = session.Identity.With(PlayerNames.Clean(options.DisplayName ?? "Bot " + runner.Role));
            runner.Play();
            return runner;
        }

        private async void Play()
        {
            try
            {
                await PlayAsync();
            }
            catch (OperationCanceledException)
            {
                // Already failed (timeout, session ended, Fail): the reason is logged.
                if (!_finished)
                {
                    Fail("cancelled in " + State + ".");
                }
            }
            catch (TimeoutException ex)
            {
                Fail("timed out in " + State + ": " + ex.Message);
            }
            catch (Exception ex)
            {
                Fail((State == NetBotState.Running ? "the driver threw " : "") + ex.GetType().Name + ": " + ex.Message
                    + (ex.StackTrace != null ? "\n" + ex.StackTrace : string.Empty));
            }
        }

        private async Task PlayAsync()
        {
            CancellationToken cancel = Bot.Cancellation;

            // 1. The code (unless joining by address).
            string code = string.IsNullOrWhiteSpace(_options.JoinCode) ? null : _options.JoinCode.Trim();
            if (code == null && !string.IsNullOrWhiteSpace(_options.JoinCodeFile))
            {
                State = NetBotState.WaitingForCode;
                Bot.Log("waiting for the join code in " + _options.JoinCodeFile);
                while (!JoinCodeFile.TryRead(_options.JoinCodeFile, out code))
                {
                    await NextFrame(cancel);
                }
            }

            // 2. Join.
            State = NetBotState.Joining;
            Bot.JoinCode = code;
            if (code != null)
            {
                string refusal = null;
                Action<string> onFailed = message => refusal = message;
                _session.JoinCodeFailed += onFailed;
                bool started;
                try
                {
                    Bot.Log("joining with code " + code + (string.IsNullOrEmpty(_options.JoinConfig.RendezvousUrl) ? "" : " via " + _options.JoinConfig.RendezvousUrl));
                    started = await _session.JoinByCode(code, _options.JoinConfig);
                }
                finally
                {
                    _session.JoinCodeFailed -= onFailed;
                }

                if (!started)
                {
                    Fail("JoinByCode(" + code + ") failed: " + (refusal ?? "refused"));
                    return;
                }
            }
            else
            {
                string address = string.IsNullOrWhiteSpace(_options.JoinConfig.Address) ? NetConfig.LoopbackAddress : _options.JoinConfig.Address;
                Bot.Log("joining " + address + ":" + _options.JoinConfig.Port);
                if (!_session.JoinByAddress(address, _options.JoinConfig.Port, _options.JoinConfig))
                {
                    string reason = _session.LastDisconnect.Message;
                    Fail("JoinByAddress(" + address + ":" + _options.JoinConfig.Port + ") refused to start"
                        + (string.IsNullOrEmpty(reason) ? "." : ": " + reason));
                    return;
                }
            }

            while (_session.State != SessionState.Connected)
            {
                if (_session.State == SessionState.Idle || _session.State == SessionState.Disconnected)
                {
                    Fail("could not connect (session " + _session.State + ").");
                    return;
                }

                await NextFrame(cancel);
            }

            // 3. Play.
            _session.Ended += OnEnded;
            State = NetBotState.Running;
            Bot.Log("connected as client " + _session.LocalClientId + "; " + _driver.GetType().Name + " plays" + (Bot.Scenario != null ? " scenario " + Bot.Scenario : "") + ".");
            await _driver.RunAsync(Bot);
            if (!_finished)
            {
                Pass(_driver.GetType().Name + " finished.");
            }
        }

        internal void Pass(string message)
        {
            if (_finished)
            {
                return;
            }

            _finished = true;
            State = NetBotState.Passed;
            PassMessage = string.IsNullOrWhiteSpace(message) ? "passed." : message;
            Debug.Log(BotLog.Ok(Role, PassMessage));
            Finished?.Invoke(this);
        }

        internal void Fail(string message)
        {
            if (_finished)
            {
                return;
            }

            _finished = true;
            State = NetBotState.Failed;
            FailureReason = string.IsNullOrWhiteSpace(message) ? "failed." : message;
            Debug.LogError(BotLog.Fail(Role, FailureReason));
            Bot?.Cancel();
            Finished?.Invoke(this);
            if (_options != null && _options.QuitOnFailure && !Application.isEditor)
            {
                Application.Quit(1);
            }
        }

        internal Task NextFrame(CancellationToken cancel)
        {
            cancel.ThrowIfCancellationRequested();
            var waiter = new FrameWaiter(cancel);
            _frameWaiters.Add(waiter);
            return waiter.Source.Task;
        }

        private void OnEnded(Disconnection reason)
        {
            if (State != NetBotState.Passed)
            {
                Fail("the session ended while playing: " + reason + ".");
            }
        }

        private void Update()
        {
            if (!_finished && Time.realtimeSinceStartup > _deadline)
            {
                string where = State == NetBotState.Running ? "while " + _driver.GetType().Name + " played" : "in " + State + " (session " + _session.State + ")";
                Fail("timed out after " + _options.TimeoutSeconds.ToString("0.#") + " s " + where + ".");
            }

            if (_frameWaiters.Count == 0)
            {
                return;
            }

            FrameWaiter[] due = _frameWaiters.ToArray();
            _frameWaiters.Clear();
            foreach (FrameWaiter waiter in due)
            {
                if (waiter.Cancel.IsCancellationRequested)
                {
                    waiter.Source.TrySetCanceled();
                }
                else
                {
                    waiter.Source.TrySetResult(true);
                }
            }
        }

        private void OnDestroy()
        {
            if (_session != null)
            {
                _session.Ended -= OnEnded;
            }

            Bot?.Cancel();
            foreach (FrameWaiter waiter in _frameWaiters)
            {
                waiter.Source.TrySetCanceled();
            }

            _frameWaiters.Clear();
        }

        private sealed class FrameWaiter
        {
            public FrameWaiter(CancellationToken cancel)
            {
                Cancel = cancel;
                Source = new TaskCompletionSource<bool>();
            }

            public CancellationToken Cancel { get; }

            public TaskCompletionSource<bool> Source { get; }
        }
    }
}
