namespace Unity.Multiplayer.Hosting.Cli.Provisioning.GenericVps;

/// <summary>
/// A one-shot <c>SSH_ASKPASS</c> helper for a passphrase-protected private key (UM-T43).
///
/// <para><c>BatchMode=yes</c> (the default for a key with no passphrase -- see
/// <see cref="ProcessSshCommandRunner"/>'s <c>BuildArgs</c>) disables OpenSSH's passphrase prompt
/// outright: with batch mode on, ssh never even attempts to decrypt the key with anything but an
/// empty passphrase, so no amount of askpass wiring fixes it. A passphrase-protected key instead
/// needs <c>BatchMode</c> left off, <c>SSH_ASKPASS_REQUIRE=force</c> (OpenSSH 8.4+) so ssh calls
/// the askpass helper even though stdin isn't a tty, and <c>SSH_ASKPASS</c> pointing at the helper
/// this class generates.</para>
///
/// <para>The passphrase itself never touches disk or a command line (a command line is visible to
/// every other process via <c>ps</c>/Task Manager): it travels only as an environment variable
/// scoped to the one ssh process a given instance was created for, and the generated script holds
/// nothing but a reference to that variable's name. Dispose deletes the script immediately --
/// callers hold it in a <c>using</c> so that happens even when the ssh call itself throws.</para>
/// </summary>
public sealed class SshAskPass : IDisposable
{
    // Internal (not private) only so NUnit coverage can assert the passphrase was actually fed to
    // ssh via this variable's name, rather than duplicating the literal.
    internal const string PassphraseEnvVar = "MINDATTIC_SSH_ASKPASS_PASSPHRASE";

    private readonly string _scriptPath;
    private bool _disposed;

    private SshAskPass(string scriptPath, string passphrase)
    {
        _scriptPath = scriptPath;
        EnvironmentVariables = new Dictionary<string, string>
        {
            ["SSH_ASKPASS_REQUIRE"] = "force",
            ["SSH_ASKPASS"] = scriptPath,
            [PassphraseEnvVar] = passphrase,
        };
    }

    /// <summary>The env vars that make ssh call this helper, and the passphrase it echoes back.</summary>
    public IReadOnlyDictionary<string, string> EnvironmentVariables { get; }

    /// <summary>Builds a fresh helper for <paramref name="passphrase"/>, or null when there is none to feed (the key has no passphrase, so ssh runs exactly as before).</summary>
    public static SshAskPass? CreateIfNeeded(string? passphrase)
    {
        if (string.IsNullOrEmpty(passphrase))
        {
            return null;
        }

        bool windows = OperatingSystem.IsWindows();
        string scriptPath = Path.Combine(Path.GetTempPath(), $"mp-host-askpass-{Guid.NewGuid():N}" + (windows ? ".cmd" : ".sh"));

        // Echoes the passphrase it inherited from ssh's own environment (set below) back to ssh's
        // askpass prompt. Holds no secret of its own -- only the env var's name -- so the script
        // file itself needs no special protection; only the one ssh process ever gets the value.
        // cmd: delayed expansion (!VAR!) is never re-parsed, unlike %VAR%, so a passphrase holding & | < > ^ is
        // echoed as typed instead of being run as commands. sh: printf, since dash's echo rewrites backslashes.
        string script = windows
            ? $"@echo off{Environment.NewLine}setlocal EnableDelayedExpansion{Environment.NewLine}echo(!{PassphraseEnvVar}!{Environment.NewLine}"
            : $"#!/bin/sh{Environment.NewLine}printf '%s\\n' \"${PassphraseEnvVar}\"{Environment.NewLine}";
        File.WriteAllText(scriptPath, script);
        if (!windows)
        {
            File.SetUnixFileMode(scriptPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return new SshAskPass(scriptPath, passphrase);
    }

    /// <summary>Deletes the helper script. Safe to call more than once; never throws if the file is already gone.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            File.Delete(_scriptPath);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
