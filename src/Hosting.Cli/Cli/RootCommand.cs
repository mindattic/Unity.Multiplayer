using Unity.Multiplayer.Hosting.Cli.Commands;
using Unity.Multiplayer.Hosting.Cli.Credentials;

namespace Unity.Multiplayer.Hosting.Cli.Cli;

/// <summary>Dispatches <c>mp-host &lt;verb&gt; ...</c> to its command, printing usage for an unknown or missing verb.</summary>
public static class RootCommand
{
    private const string Usage = """
        mp-host -- the Unity.Multiplayer hosting deploy CLI (UM-T25)

        Usage:
          mp-host game        local | down | deploy | update | status | logs | destroy [--project <unity project dir>] [--profile <p>] [--scene <path>] [--dry-run]
                                Any Unity.Multiplayer game, no per-game script (UM-T58): finds the editor, builds the server
                                package from the project and runs the commands below. 'mp-host game help' lists its options.
          mp-host deploy     --profile <GenericVps|RunPod|Hetzner|Vultr|DigitalOcean|Linode|OvhCloud|AwsEc2|AwsLightsail|Gce|AzureVm|Fly|GameLift|Oci> --name <server> --image <owner/repo> ...
          mp-host status      --profile <p> --name <server> --server-id <id>
          mp-host logs         --profile <p> --name <server> --server-id <id> [--tail <n>]
          mp-host update       --profile <p> --name <server> --server-id <id> --image <owner/repo> ...
          mp-host destroy      --profile <p> --name <server> --server-id <id> --yes
          mp-host local        up --name <server> (--package <dir> | --unity <exe> --unity-project <dir> [--scene <path>] [--package-output <dir>]) [--rendezvous-url <url> [--rendezvous-secret <s>]] [--public-address <addr>] [--no-upnp]
                                down --name <server>
          mp-host credentials  set-ghcr | set-runpod | set-rendezvous | set-ssh | set-hetzner | set-vultr | set-digitalocean | set-linode | set-ovh | set-aws | set-gce | set-azure | set-fly | set-oci | set-cloud-options ...

        Hetzner/Vultr/DigitalOcean/Linode/OvhCloud (UM-T26) and AwsEc2/AwsLightsail/Gce/AzureVm
        (UM-T27) each create their own VM plus a security group/firewall/NSG for the game port and
        rendezvous; see docs/articles/hosting/ for each one's setup and sizing guide, and -- for the
        UM-T27 big-cloud profiles -- the per-player-hour egress cost estimate before using one for a
        public launch. Fly (UM-T28) deploys through Fly's Machines API with a dedicated IPv4 for its
        UDP game port and rendezvous as either a second process group or a second Fly app; see
        docs/articles/hosting/flyio.md. GameLift (UM-T30) uploads the raw (non-Docker) build and
        creates a managed fleet instead of a VM; a matchmaker then allocates one game session per
        match with Unity.Multiplayer.Core.GameLiftMatchAllocator -- see docs/articles/hosting/gamelift.md.
        Oci (UM-T31) runs on Oracle Cloud's Always Free tier; see docs/articles/hosting/oracle-oci.md
        for the Ampere A1 (ARM64) vs AMD micro shape choice.

        'local' (UM-T31) runs the server package on this machine instead of a hosted provider: no
        GHCR push, no SSH, LAN join by IP, and a best-effort UPnP attempt to forward the game port
        off-LAN (a clear warning, not an error, when the router refuses -- the owner's own
        HOA-controlled home network always will; use Unity Relay for a listen server reachable off
        that LAN, per RFC 0001). See docs/articles/hosting/local.md.

        Every command accepts --dry-run, which prints every API/SSH/docker call it would make
        instead of making it. Credentials are read from MindAttic.Vault (see 'credentials'); none
        are ever read from, or written to, this repo.
        """;

    public static async Task<int> RunAsync(string[] args, IDeployLog log, CancellationToken ct)
    {
        if (args.Length == 0)
        {
            log.Info(Usage);
            return 1;
        }

        try
        {
            HostingCredentialStore credentials = HostingCredentialStore.Default;
            return args[0] switch
            {
                "game" => await GameCommand.RunAsync(args, log, credentials, ct),
                "deploy" => await DeployCommand.RunAsync(args, log, credentials, ct),
                "status" => await StatusCommand.RunAsync(args, log, credentials, ct),
                "logs" => await LogsCommand.RunAsync(args, log, credentials, ct),
                "update" => await UpdateCommand.RunAsync(args, log, credentials, ct),
                "destroy" => await DestroyCommand.RunAsync(args, log, credentials, ct),
                "local" => await LocalCommand.RunAsync(args, log, credentials, ct),
                "credentials" => await CredentialsCommand.RunAsync(args, log, credentials),
                "help" or "--help" or "-h" => Help(log),
                _ => Unknown(args[0], log),
            };
        }
        catch (ArgumentException ex)
        {
            log.Error(ex.Message);
            return 1;
        }
        catch (InvalidOperationException ex)
        {
            log.Error(ex.Message);
            return 1;
        }
        catch (IOException ex)
        {
            // A missing/unreadable file the user named (e.g. --ssh-public-key, --package's manifest)
            // should read as a clean error, not an unhandled-exception stack trace.
            log.Error(ex.Message);
            return 1;
        }
        catch (UnauthorizedAccessException ex)
        {
            log.Error(ex.Message);
            return 1;
        }
    }

    private static int Help(IDeployLog log)
    {
        log.Info(Usage);
        return 0;
    }

    private static int Unknown(string verb, IDeployLog log)
    {
        log.Error($"Unknown command '{verb}'.");
        log.Info(Usage);
        return 1;
    }
}
