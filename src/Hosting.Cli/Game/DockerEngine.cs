using Unity.Multiplayer.Hosting.Cli.Cli;
using Unity.Multiplayer.Hosting.Cli.Processes;

namespace Unity.Multiplayer.Hosting.Cli.Game;

/// <summary>
/// Server packages are Linux images. Docker Desktop on Windows in Windows-containers mode still runs its Linux
/// engine on its own pipe, so rather than make the operator switch engines, <see cref="PreferLinuxAsync"/> points this
/// process (and every docker/compose child it starts) at that engine through <c>DOCKER_HOST</c> (UM-T58; this was
/// Unity.Cursory's root <c>run.ps1</c> Use-LinuxDockerEngine).
/// </summary>
public static class DockerEngine
{
    /// <summary>Docker Desktop's Linux engine pipe on Windows.</summary>
    public const string LinuxEnginePipe = "npipe:////./pipe/dockerDesktopLinuxEngine";

    /// <summary>
    /// Returns the <c>DOCKER_HOST</c> to use, or null to leave docker as it is: null when <c>DOCKER_HOST</c> is already
    /// set, docker is missing, or the default engine already runs Linux containers.
    /// </summary>
    public static async Task<string?> FindLinuxEngineAsync(IProcessRunner processes, Func<string, string?> getEnvironmentVariable, IDeployLog log, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(processes);
        ArgumentNullException.ThrowIfNull(log);
        if (!string.IsNullOrWhiteSpace(getEnvironmentVariable("DOCKER_HOST"))) return null;

        string? os = await OsTypeAsync(processes, null, ct);
        if (os is null)
        {
            log.Warn("docker did not answer ('docker info' failed); is Docker running?");
            return null;
        }

        if (os != "windows") return null;
        if (await OsTypeAsync(processes, LinuxEnginePipe, ct) == "linux")
        {
            log.Info($"Docker is in Windows-containers mode; using Docker Desktop's Linux engine ({LinuxEnginePipe}) for this run.");
            return LinuxEnginePipe;
        }

        log.Warn("Docker is in Windows-containers mode and no Linux engine answered; switch Docker Desktop to Linux containers.");
        return null;
    }

    /// <summary><see cref="FindLinuxEngineAsync"/>, then sets <c>DOCKER_HOST</c> for this process and its children when it found one.</summary>
    public static async Task PreferLinuxAsync(IProcessRunner processes, IDeployLog log, CancellationToken ct)
    {
        string? host = await FindLinuxEngineAsync(processes, Environment.GetEnvironmentVariable, log, ct);
        if (host is not null) Environment.SetEnvironmentVariable("DOCKER_HOST", host);
    }

    private static async Task<string?> OsTypeAsync(IProcessRunner processes, string? host, CancellationToken ct)
    {
        var arguments = new List<string>();
        if (host is not null) arguments.AddRange(new[] { "-H", host });
        arguments.AddRange(new[] { "info", "--format", "{{.OSType}}" });
        try
        {
            ProcessOutcome outcome = await processes.RunAsync("docker", arguments, null, null, ct);
            return outcome.Succeeded ? outcome.StandardOutput.Trim() : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null; // docker is not installed / not on PATH
        }
    }
}
