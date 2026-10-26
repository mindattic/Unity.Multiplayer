using System;
using System.Collections.Generic;
using System.Globalization;

namespace Unity.Multiplayer.Core
{
    /// <summary>
    /// Reads the bot and end-to-end arguments from a process command line (UM-T57). Engine-free, so the rules are tested
    /// by plain <c>dotnet test</c>; the adapter's <c>NetBots</c> acts on them at launch.
    /// </summary>
    /// <remarks>
    /// <list type="table">
    /// <item><term><c>-bot &lt;role&gt;</c></term><description>This process is a bot client playing <c>role</c> (any word
    /// the game's driver understands: <c>A</c>, <c>B</c>, <c>runner</c>, ...). It starts no session by itself, shows no
    /// menus, joins, then hands the session to the game's <c>IBotDriver</c>.</description></item>
    /// <item><term><c>-joincode &lt;code&gt;</c></term><description>Bot: the join code to join by.</description></item>
    /// <item><term><c>-joincodefile &lt;path&gt;</c></term><description>Bot: wait for this file and join by the code in
    /// it. Server: write every join code it publishes to this file (atomically), so bots started beside it can read it
    /// (a shared Docker volume, a temp folder in a test).</description></item>
    /// <item><term><c>-scenario &lt;name&gt;</c></term><description>Free text the game reads to set the run up (a level, a
    /// map, a mode); the library only passes it on. Server and bots alike.</description></item>
    /// <item><term><c>-bottimeout &lt;seconds&gt;</c></term><description>Bot: fail the run after this long (default
    /// <see cref="DefaultTimeoutSeconds"/>).</description></item>
    /// </list>
    /// <para>Names ignore case and take their value as the next argument or after <c>=</c>. Anything else is ignored.</para>
    /// </remarks>
    public static class BotCommandLine
    {
        /// <summary>Makes this process a bot client playing the given role.</summary>
        public const string BotArg = "-bot";

        /// <summary>The join code a bot joins by.</summary>
        public const string JoinCodeArg = "-joincode";

        /// <summary>The file a bot reads the join code from, and a server writes it to.</summary>
        public const string JoinCodeFileArg = "-joincodefile";

        /// <summary>The scenario the game sets the run up for.</summary>
        public const string ScenarioArg = "-scenario";

        /// <summary>Seconds a bot's whole run may take.</summary>
        public const string TimeoutArg = "-bottimeout";

        /// <summary>The default for <see cref="TimeoutArg"/>, in seconds.</summary>
        public const double DefaultTimeoutSeconds = 180;

        /// <summary>Reads the bot arguments in <paramref name="args"/> (a later repeat wins).</summary>
        /// <exception cref="ArgumentNullException"><paramref name="args"/> is null.</exception>
        public static BotLaunchOptions Parse(IReadOnlyList<string> args)
        {
            if (args == null)
            {
                throw new ArgumentNullException(nameof(args));
            }

            var errors = new List<string>();
            string role = null;
            string code = null;
            string codeFile = null;
            string scenario = null;
            double timeout = DefaultTimeoutSeconds;

            for (int i = 0; i < args.Count; i++)
            {
                string arg = args[i];
                if (string.IsNullOrEmpty(arg) || arg[0] != '-')
                {
                    continue;
                }

                string name = arg;
                string value = null;
                bool inline = false;
                int equals = arg.IndexOf('=');
                if (equals > 0)
                {
                    name = arg.Substring(0, equals);
                    value = arg.Substring(equals + 1);
                    inline = true;
                }

                if (!Is(name, BotArg) && !Is(name, JoinCodeArg) && !Is(name, JoinCodeFileArg) && !Is(name, ScenarioArg) && !Is(name, TimeoutArg))
                {
                    continue;
                }

                if (!inline && i + 1 < args.Count && !string.IsNullOrEmpty(args[i + 1]) && args[i + 1][0] != '-')
                {
                    value = args[i + 1];
                    i++;
                }

                if (string.IsNullOrWhiteSpace(value))
                {
                    errors.Add(name + " needs a value.");
                    continue;
                }

                value = value.Trim();
                if (Is(name, BotArg))
                {
                    role = value;
                }
                else if (Is(name, JoinCodeArg))
                {
                    code = value;
                }
                else if (Is(name, JoinCodeFileArg))
                {
                    codeFile = value;
                }
                else if (Is(name, ScenarioArg))
                {
                    scenario = value;
                }
                else
                {
                    double seconds;
                    if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out seconds) && seconds > 0 && !double.IsInfinity(seconds))
                    {
                        timeout = seconds;
                    }
                    else
                    {
                        errors.Add(TimeoutArg + " must be a number of seconds above 0; got '" + value + "'.");
                    }
                }
            }

            if (role != null && code == null && codeFile == null && !HasAddress(args))
            {
                errors.Add(BotArg + " " + role + " needs " + JoinCodeArg + " <code>, " + JoinCodeFileArg + " <path> or "
                    + NetCommandLine.AddressArg + " <ip> to join by.");
            }

            return new BotLaunchOptions(role, code, codeFile, scenario, timeout, errors);
        }

        /// <summary>True when <c>-address</c> (or its alias <c>-ip</c>) is given with a non-blank value.</summary>
        private static bool HasAddress(IReadOnlyList<string> args)
        {
            for (int i = 0; i < args.Count; i++)
            {
                string arg = args[i];
                if (string.IsNullOrEmpty(arg) || arg[0] != '-')
                {
                    continue;
                }

                string name = arg;
                string value = null;
                int equals = arg.IndexOf('=');
                if (equals > 0)
                {
                    name = arg.Substring(0, equals);
                    value = arg.Substring(equals + 1);
                }

                if (!Is(name, NetCommandLine.AddressArg) && !Is(name, NetCommandLine.IpArg))
                {
                    continue;
                }

                if (value == null && i + 1 < args.Count && !string.IsNullOrEmpty(args[i + 1]) && args[i + 1][0] != '-')
                {
                    value = args[i + 1];
                }

                if (!string.IsNullOrWhiteSpace(value))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool Is(string name, string expected)
        {
            return string.Equals(name, expected, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>What <see cref="BotCommandLine.Parse"/> found on a launch line.</summary>
    public sealed class BotLaunchOptions
    {
        /// <summary>Creates options.</summary>
        public BotLaunchOptions(string role, string joinCode, string joinCodeFile, string scenario, double timeoutSeconds, IReadOnlyList<string> errors = null)
        {
            Role = string.IsNullOrWhiteSpace(role) ? null : role.Trim();
            JoinCode = string.IsNullOrWhiteSpace(joinCode) ? null : joinCode.Trim();
            JoinCodeFile = string.IsNullOrWhiteSpace(joinCodeFile) ? null : joinCodeFile.Trim();
            Scenario = string.IsNullOrWhiteSpace(scenario) ? null : scenario.Trim();
            TimeoutSeconds = timeoutSeconds > 0 ? timeoutSeconds : BotCommandLine.DefaultTimeoutSeconds;
            Errors = errors ?? new string[0];
        }

        /// <summary>The bot's role (<c>-bot</c>), or null when this process is not a bot.</summary>
        public string Role { get; }

        /// <summary>True when <c>-bot</c> was given.</summary>
        public bool IsBot
        {
            get { return Role != null; }
        }

        /// <summary>The code to join by (<c>-joincode</c>), or null.</summary>
        public string JoinCode { get; }

        /// <summary>The join-code file (<c>-joincodefile</c>): read by a bot, written by a server. Null when absent.</summary>
        public string JoinCodeFile { get; }

        /// <summary>The scenario (<c>-scenario</c>), or null.</summary>
        public string Scenario { get; }

        /// <summary>Seconds a bot's run may take (<c>-bottimeout</c>).</summary>
        public double TimeoutSeconds { get; }

        /// <summary>One message per unreadable argument, or a bot with nothing to join by. Never null.</summary>
        public IReadOnlyList<string> Errors { get; }

        /// <summary>True when <see cref="Errors"/> is empty.</summary>
        public bool IsValid
        {
            get { return Errors.Count == 0; }
        }
    }
}
