using System;
using System.IO;
using NUnit.Framework;

namespace Unity.Multiplayer.Core.Tests
{
    /// <summary>UM-T57: the bot launch line, NetLaunch's bot rule and the join-code file hand-off.</summary>
    [TestFixture]
    public class BotCommandLineTests
    {
        [Test]
        public void Parse_ReadsEveryBotArgument()
        {
            BotLaunchOptions options = BotCommandLine.Parse(new[]
            {
                "Game.x86_64", "-batchmode", "-bot", "A", "-joincodefile", "/shared/join-code.txt", "-scenario=level-2", "-bottimeout", "240", "-rendezvous", "http://r:8080",
            });

            Assert.That(options.IsValid, Is.True, string.Join("; ", options.Errors));
            Assert.That(options.IsBot, Is.True);
            Assert.That(options.Role, Is.EqualTo("A"));
            Assert.That(options.JoinCodeFile, Is.EqualTo("/shared/join-code.txt"));
            Assert.That(options.JoinCode, Is.Null);
            Assert.That(options.Scenario, Is.EqualTo("level-2"));
            Assert.That(options.TimeoutSeconds, Is.EqualTo(240));
        }

        [Test]
        public void Parse_WithoutBot_IsNotABot_ButKeepsTheServersFileAndScenario()
        {
            BotLaunchOptions options = BotCommandLine.Parse(new[] { "-mode", "server", "-JoinCodeFile", "/tmp/um-t57/code.txt", "-SCENARIO", "2" });

            Assert.That(options.IsBot, Is.False);
            Assert.That(options.Role, Is.Null);
            Assert.That(options.JoinCodeFile, Is.EqualTo("/tmp/um-t57/code.txt"));
            Assert.That(options.Scenario, Is.EqualTo("2"));
            Assert.That(options.TimeoutSeconds, Is.EqualTo(BotCommandLine.DefaultTimeoutSeconds));
            Assert.That(options.IsValid, Is.True);
        }

        [Test]
        public void Parse_ReportsMissingAndUnreadableValues_AndKeepsDefaults()
        {
            BotLaunchOptions options = BotCommandLine.Parse(new[] { "-bot", "B", "-joincode", "ABC-DEF-GHJ", "-bottimeout", "soon", "-scenario" });

            Assert.That(options.Role, Is.EqualTo("B"));
            Assert.That(options.JoinCode, Is.EqualTo("ABC-DEF-GHJ"));
            Assert.That(options.TimeoutSeconds, Is.EqualTo(BotCommandLine.DefaultTimeoutSeconds));
            Assert.That(options.Errors, Has.Count.EqualTo(2));
            Assert.That(options.Errors, Has.Some.Contains("-bottimeout"));
            Assert.That(options.Errors, Has.Some.Contains("-scenario needs a value"));
        }

        [Test]
        public void Parse_DoesNotMistakeJoinCodesForJoinCode()
        {
            BotLaunchOptions options = BotCommandLine.Parse(new[] { "-joincodes", "rendezvous", "-bot", "runner", "-address", "10.0.0.5" });
            Assert.That(options.JoinCode, Is.Null);
            Assert.That(options.Role, Is.EqualTo("runner"));
            Assert.That(options.IsValid, Is.True, string.Join("; ", options.Errors));
        }

        [Test]
        public void Parse_WithAddress_NeedsNoJoinCode_EvenAsAnAlias_OrInlineValue()
        {
            Assert.That(BotCommandLine.Parse(new[] { "-bot", "A", "-address", "127.0.0.1" }).IsValid, Is.True);
            Assert.That(BotCommandLine.Parse(new[] { "-bot", "A", "-ip", "127.0.0.1" }).IsValid, Is.True, "-ip is -address's alias");
            Assert.That(BotCommandLine.Parse(new[] { "-bot", "A", "-address=127.0.0.1" }).IsValid, Is.True, "inline -address=value");
        }

        [Test]
        public void Parse_WithNoJoinCodeAndNoAddress_StillReportsItNeedsOne()
        {
            BotLaunchOptions options = BotCommandLine.Parse(new[] { "-bot", "A" });
            Assert.That(options.IsValid, Is.False);
            Assert.That(options.Errors, Has.Some.Contains(BotCommandLine.JoinCodeArg));
            Assert.That(options.Errors, Has.Some.Contains(NetCommandLine.AddressArg), "the error should mention -address is also a way to join");

            Assert.That(BotCommandLine.Parse(new[] { "-bot", "A", "-address", string.Empty }).IsValid, Is.False, "a blank -address is not an address");
        }

        [Test]
        public void NetCommandLine_SeesTheBot_AndLeavesTheConfigAlone()
        {
            var config = new NetConfig { Topology = Topology.Host };
            NetCommandLineResult line = NetCommandLine.Apply(config, new[] { "-batchmode", "-bot", "A", "-joincodefile", "/shared/x", "-rendezvous", "http://r:8080" });

            Assert.That(line.IsValid, Is.True, string.Join("; ", line.Errors));
            Assert.That(line.IsBot, Is.True);
            Assert.That(line.BotRole, Is.EqualTo("A"));
            Assert.That(line.TopologySpecified, Is.False);
            Assert.That(config.RendezvousUrl, Is.EqualTo("http://r:8080"));
            Assert.That(config.Topology, Is.EqualTo(Topology.Host));
            Assert.That(NetCommandLine.Apply(new NetConfig(), new[] { "-bot" }).Errors, Has.Some.Contains("-bot needs a value"));
        }

        [Test]
        public void ABot_NeverStartsBySelf_NorShowsMenus_EvenHeadlessOrWithMode()
        {
            var config = new NetConfig { Topology = Topology.Host };
            NetCommandLineResult bot = NetCommandLine.Apply(config, new[] { "-batchmode", "-nographics", "-bot", "A" });
            Assert.That(NetLaunch.ShouldStart(config, bot, headless: true, autoStart: true, serveWhenHeadless: true), Is.False);
            Assert.That(config.Topology, Is.EqualTo(Topology.Host), "a bot is never turned into a dedicated server");
            Assert.That(NetLaunch.ShowsMenus(bot, headless: false), Is.False, "a windowed bot shows no menus either");

            NetCommandLineResult withMode = NetCommandLine.Apply(new NetConfig(), new[] { "-mode", "server", "-bot", "A" });
            Assert.That(NetLaunch.ShouldStart(new NetConfig(), withMode, false, false, false), Is.False, "-bot wins over -mode");

            var outsideCommandLine = new NetCommandLineResult(false, false, null, "B");
            Assert.That(NetLaunch.ShouldStart(new NetConfig(), outsideCommandLine, headless: true, autoStart: false, serveWhenHeadless: true), Is.False);
            Assert.That(new NetCommandLineResult(false, false, null).IsBot, Is.False);
        }

        [Test]
        public void JoinCodeFile_WritesAtomically_AndReadsOnlyOnceThere()
        {
            string folder = Path.Combine(Path.GetTempPath(), "um-t57-" + Guid.NewGuid().ToString("N"));
            string path = Path.Combine(folder, "nested", "join-code.txt");
            try
            {
                string code;
                Assert.That(JoinCodeFile.TryRead(path, out code), Is.False);
                Assert.That(code, Is.Null);

                JoinCodeFile.Write(path, " ABC-DEF-GHJ ");
                Assert.That(JoinCodeFile.TryRead(path, out code), Is.True);
                Assert.That(code, Is.EqualTo("ABC-DEF-GHJ"));

                JoinCodeFile.Write(path, "KLM-NPQ-RST");
                Assert.That(JoinCodeFile.TryRead(path, out code), Is.True);
                Assert.That(code, Is.EqualTo("KLM-NPQ-RST"), "a new code replaces the old");
                Assert.That(Directory.GetFiles(Path.GetDirectoryName(path)), Has.Length.EqualTo(1), "no temp file is left behind");

                File.WriteAllText(path, "  \n");
                Assert.That(JoinCodeFile.TryRead(path, out code), Is.False, "an empty file holds no code yet");
                Assert.Throws<ArgumentException>(() => JoinCodeFile.Write(path, " "));
                Assert.Throws<ArgumentException>(() => JoinCodeFile.Write("", "ABC"));
            }
            finally
            {
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
            }
        }

        [Test]
        public void BotLog_LinesAreWhatTheSmokeWaitsFor()
        {
            Assert.That(BotLog.Ok("A", "solved"), Is.EqualTo("[NetBot:A] OK: solved"));
            Assert.That(BotLog.Fail("B", "timed out"), Is.EqualTo("[NetBot:B] FAIL: timed out"));
            Assert.That(BotLog.Line(" ", "x"), Is.EqualTo("[NetBot:?] x"));
        }
    }
}
