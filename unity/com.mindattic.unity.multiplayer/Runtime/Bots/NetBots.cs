using System;
using System.Collections.Generic;
using System.Reflection;
using Unity.Multiplayer.Core;
using UnityEngine;

namespace Unity.Multiplayer
{
    /// <summary>
    /// Bot and end-to-end launches (UM-T57), acted on at scene load with nothing for a game to wire up:
    /// <list type="bullet">
    /// <item><c>-bot &lt;role&gt; (-joincode &lt;code&gt; | -joincodefile &lt;path&gt; | -address &lt;ip&gt;)</c> makes
    /// this process a headless bot client. The scene's <see cref="NetBootstrap"/> starts nothing by itself
    /// (<see cref="NetLaunch"/>) and the front end shows no menus; a <see cref="NetBotRunner"/> joins with the bootstrap's
    /// session and settings (<c>-rendezvous</c> and the rest as usual) and hands it to the game's
    /// <see cref="IBotDriver"/>.</item>
    /// <item>A server launched with <c>-joincodefile &lt;path&gt;</c> writes every join code it publishes there
    /// (<see cref="WriteJoinCodes"/>), so bots started beside it can join.</item>
    /// <item><c>-scenario &lt;name&gt;</c> is handed to the game as is (<see cref="Scenario"/>, <see cref="NetBot.Scenario"/>).</item>
    /// </list>
    /// </summary>
    public static class NetBots
    {
        private static BotLaunchOptions _launch;

        /// <summary>This process's bot arguments (read from its command line once).</summary>
        public static BotLaunchOptions Launch
        {
            get
            {
                if (_launch == null)
                {
                    _launch = BotCommandLine.Parse(Environment.GetCommandLineArgs());
                }

                return _launch;
            }
        }

        /// <summary>True when this process was launched as a bot (<c>-bot &lt;role&gt;</c>).</summary>
        public static bool IsBot
        {
            get { return Launch.IsBot; }
        }

        /// <summary>The scenario this process was launched with (<c>-scenario</c>), or null. The game decides what it means.</summary>
        public static string Scenario
        {
            get { return Launch.Scenario; }
        }

        /// <summary>The bot a <c>-bot</c> launch started, once it has; null otherwise.</summary>
        public static NetBotRunner Current { get; private set; }

        /// <summary>
        /// Writes every join code <paramref name="session"/> publishes to <paramref name="path"/> (and the one it already
        /// has), replacing what was there. A server launched with <c>-joincodefile</c> does this by itself.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="session"/> or <paramref name="path"/> is null.</exception>
        public static void WriteJoinCodes(NetSession session, string path)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));
            if (path == null) throw new ArgumentNullException(nameof(path));
            session.JoinCodePublished += code => Write(path, JoinCodeFormat.Format(code.Value));
            if (!string.IsNullOrEmpty(session.PublishedCodeText))
            {
                Write(path, session.PublishedCodeText);
            }
        }

        /// <summary>
        /// The game's bot driver: an enabled component in the loaded scenes that implements <see cref="IBotDriver"/>, else
        /// a new instance of the one class marked <see cref="NetBotDriverAttribute"/> (a MonoBehaviour one is added to
        /// <paramref name="host"/>). Null, with <paramref name="problem"/> saying why, when there is none or more than one.
        /// </summary>
        public static IBotDriver FindDriver(GameObject host, out string problem)
        {
            problem = null;
            var components = new List<IBotDriver>();
            foreach (MonoBehaviour behaviour in UnityEngine.Object.FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                if (behaviour is IBotDriver driver && behaviour.enabled)
                {
                    components.Add(driver);
                }
            }

            if (components.Count == 1)
            {
                return components[0];
            }

            if (components.Count > 1)
            {
                problem = "the scene has " + components.Count + " IBotDriver components; keep one.";
                return null;
            }

            List<Type> marked = MarkedDriverTypes();
            if (marked.Count == 0)
            {
                problem = "no bot driver: add a component that implements IBotDriver to the scene, or mark a class [NetBotDriver].";
                return null;
            }

            if (marked.Count > 1)
            {
                problem = "more than one class is marked [NetBotDriver] (" + string.Join(", ", marked.ConvertAll(t => t.FullName)) + "); keep one.";
                return null;
            }

            Type type = marked[0];
            if (!typeof(IBotDriver).IsAssignableFrom(type))
            {
                problem = type.FullName + " is marked [NetBotDriver] but does not implement IBotDriver.";
                return null;
            }

            if (typeof(Component).IsAssignableFrom(type))
            {
                if (host == null)
                {
                    problem = type.FullName + " is a component; it needs a GameObject to live on.";
                    return null;
                }

                return (IBotDriver)host.AddComponent(type);
            }

            try
            {
                return (IBotDriver)Activator.CreateInstance(type);
            }
            catch (Exception ex)
            {
                problem = "could not create " + type.FullName + " (it needs a public parameterless constructor): " + ex.Message;
                return null;
            }
        }

        /// <summary>Every class marked <see cref="NetBotDriverAttribute"/> in the assemblies that reference this one.</summary>
        internal static List<Type> MarkedDriverTypes()
        {
            var found = new List<Type>();
            string self = typeof(NetBots).Assembly.GetName().Name;
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly.IsDynamic || !References(assembly, self))
                {
                    continue;
                }

                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    types = ex.Types;
                }

                foreach (Type type in types)
                {
                    if (type != null && type.IsClass && !type.IsAbstract && type.IsDefined(typeof(NetBotDriverAttribute), false))
                    {
                        found.Add(type);
                    }
                }
            }

            return found;
        }

        private static bool References(Assembly assembly, string name)
        {
            foreach (AssemblyName reference in assembly.GetReferencedAssemblies())
            {
                if (reference.Name == name)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>Acts on the launch line once the first scene has loaded: after every Awake, before any Start.</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Boot()
        {
            _launch = null; // read afresh each play session in the editor
            BotLaunchOptions launch = Launch;
            if (!launch.IsBot)
            {
                if (launch.JoinCodeFile != null)
                {
                    NetBootstrap server = NetBootstrap.Instance;
                    if (server == null || server.Session == null)
                    {
                        Debug.LogError("[MindAttic.Multiplayer] " + BotCommandLine.JoinCodeFileArg + " given, but the scene has no NetBootstrap.");
                        return;
                    }

                    WriteJoinCodes(server.Session, launch.JoinCodeFile);
                }

                return;
            }

            string role = launch.Role;
            foreach (string error in launch.Errors)
            {
                Debug.LogWarning(BotLog.Line(role, "command line: " + error));
            }

            // Netcode for GameObjects registers its own message types from a RuntimeInitializeOnLoadMethod(AfterSceneLoad)
            // callback (generated by ILPP), the same stage this method runs at; Unity does not order callbacks across
            // assemblies within one stage, so joining here (which calls NetworkManager.StartClient) can race that
            // registration, especially in a player build. Start() always runs after every AfterSceneLoad callback has
            // finished, so the join is deferred there (see JoinDeferred below).
            var holder = new GameObject("NetBot " + role);
            UnityEngine.Object.DontDestroyOnLoad(holder);
            holder.AddComponent<JoinDeferred>().Begin(role, launch);
        }

        /// <summary>Runs <see cref="BeginJoin"/> from Start(), once every AfterSceneLoad callback (NGO's own included) has finished.</summary>
        [AddComponentMenu("")]
        private sealed class JoinDeferred : MonoBehaviour
        {
            private string _role;
            private BotLaunchOptions _launch;

            public void Begin(string role, BotLaunchOptions launch)
            {
                _role = role;
                _launch = launch;
            }

            private void Start()
            {
                BeginJoin(gameObject, _role, _launch);
                Destroy(this);
            }
        }

        private static void BeginJoin(GameObject holder, string role, BotLaunchOptions launch)
        {
            NetBootstrap bootstrap = NetBootstrap.Instance;
            if (bootstrap == null || bootstrap.Session == null)
            {
                BootFailed(role, "the scene has no NetBootstrap to join with.");
                return;
            }

            NetConfig config = bootstrap.BuildConfig();
            if (launch.JoinCode == null && launch.JoinCodeFile == null && string.IsNullOrWhiteSpace(config.Address))
            {
                BootFailed(role, "nothing to join: give " + BotCommandLine.JoinCodeArg + " <code>, " + BotCommandLine.JoinCodeFileArg
                    + " <path> or " + NetCommandLine.AddressArg + " <ip>.");
                return;
            }

            if (HeadlessBootstrap.IsHeadless)
            {
                HeadlessBootstrap.Apply(config.TickRate);
            }

            if (!HasCommandLineToken())
            {
                // Bots launched from one build would otherwise share the remembered token and take each other's seats.
                bootstrap.Session.Identity = new PlayerIdentity(PlayerIdentity.NewToken(), bootstrap.Session.Identity.DisplayName, bootstrap.Session.Identity.Color);
            }

            string problem;
            IBotDriver driver = FindDriver(holder, out problem);
            if (driver == null)
            {
                BootFailed(role, problem);
                return;
            }

            Current = NetBotRunner.Run(holder, bootstrap.Session, driver, new NetBotOptions
            {
                Role = role,
                JoinCode = launch.JoinCode,
                JoinCodeFile = launch.JoinCodeFile,
                Scenario = launch.Scenario,
                JoinConfig = config,
                TimeoutSeconds = (float)launch.TimeoutSeconds,
                QuitOnFailure = !Application.isEditor,
            });
        }

        private static bool HasCommandLineToken()
        {
            foreach (string arg in Environment.GetCommandLineArgs())
            {
                if (string.Equals(arg, NetBootstrap.PlayerTokenArg, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static void BootFailed(string role, string message)
        {
            Debug.LogError(BotLog.Fail(role, message));
            if (!Application.isEditor)
            {
                Application.Quit(1);
            }
        }

        private static void Write(string path, string code)
        {
            try
            {
                JoinCodeFile.Write(path, code);
                Debug.Log("[MindAttic.Multiplayer] Join code " + code + " written to " + path);
            }
            catch (Exception ex)
            {
                Debug.LogError("[MindAttic.Multiplayer] Could not write the join code to " + path + ": " + ex.Message);
            }
        }
    }
}
