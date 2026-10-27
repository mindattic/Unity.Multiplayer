using System.Net.Http.Headers;
using Unity.Multiplayer.Hosting.Cli.Cli;
using Unity.Multiplayer.Hosting.Cli.Credentials;
using Unity.Multiplayer.Hosting.Cli.Docker;
using Unity.Multiplayer.Hosting.Cli.Packaging;
using Unity.Multiplayer.Hosting.Cli.Processes;
using Unity.Multiplayer.Hosting.Cli.Provisioning;
using Unity.Multiplayer.Hosting.Cli.Provisioning.Aws.Ec2;
using Unity.Multiplayer.Hosting.Cli.Provisioning.Aws.Lightsail;
using Unity.Multiplayer.Hosting.Cli.Provisioning.Azure;
using Unity.Multiplayer.Hosting.Cli.Provisioning.CloudVps;
using Unity.Multiplayer.Hosting.Cli.Provisioning.DigitalOcean;
using Unity.Multiplayer.Hosting.Cli.Provisioning.Fly;
using Unity.Multiplayer.Hosting.Cli.Provisioning.GameLift;
using Unity.Multiplayer.Hosting.Cli.Provisioning.Gce;
using Unity.Multiplayer.Hosting.Cli.Provisioning.GenericVps;
using Unity.Multiplayer.Hosting.Cli.Provisioning.Hetzner;
using Unity.Multiplayer.Hosting.Cli.Provisioning.Linode;
using Unity.Multiplayer.Hosting.Cli.Provisioning.Oci;
using Unity.Multiplayer.Hosting.Cli.Provisioning.Ovh;
using Unity.Multiplayer.Hosting.Cli.Provisioning.RunPod;
using Unity.Multiplayer.Hosting.Cli.Provisioning.Vultr;

namespace Unity.Multiplayer.Hosting.Cli.Composition;

/// <summary>
/// Wires real (or dry-run) implementations together from <see cref="HostingCredentialStore"/>,
/// the one place in mp-host that touches MindAttic.Vault. Every command handler goes through this
/// instead of constructing provisioners itself, so the dry-run switch is in exactly one place.
/// </summary>
public sealed class HostingComposer
{
    private readonly HostingCredentialStore _credentials;
    private readonly bool _dryRun;

    public HostingComposer(HostingCredentialStore credentials, bool dryRun)
    {
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _dryRun = dryRun;
    }

    /// <summary>GHCR registry host; overridable for a self-hosted or mirrored registry.</summary>
    public string GhcrRegistry { get; init; } = "ghcr.io";

    /// <summary>The server-package builder: reads an already-built package, or drives Unity in batch mode (or, dry-run, neither).</summary>
    public IServerPackageBuilder BuildPackageBuilder() => _dryRun ? new DryRunServerPackageBuilder() : new ServerPackageBuilder(new ProcessRunner());

    /// <summary>The image tool: real docker CLI, or a dry-run echo.</summary>
    public IContainerImageTool BuildImageTool(IDeployLog log) => _dryRun ? new DryRunImageTool() : new DockerCliImageTool(new ProcessRunner());

    /// <summary>
    /// The label every dry-run placeholder credential carries, so nothing printed from one can be
    /// mistaken for (or pasted as) a real value (UM-T49).
    /// </summary>
    public const string PlaceholderPrefix = "<dry-run placeholder ";

    private static string Placeholder(string what) => PlaceholderPrefix + what + ">";

    /// <summary>
    /// GHCR credentials from the vault. Required for every real deploy/update. A dry run touches
    /// nothing, so with no GHCR credential stored it warns and uses clearly-labelled placeholders
    /// instead of refusing (UM-T49): <c>run.ps1 -DryRun</c> works on a fresh machine.
    /// </summary>
    /// <exception cref="InvalidOperationException">No GHCR credential is stored and this is not a dry run.</exception>
    public HostingCredentialStore.GhcrCreds RequireGhcrCreds(IDeployLog log)
    {
        ArgumentNullException.ThrowIfNull(log);
        var stored = _credentials.GetGhcrCreds();
        if (stored is not null) return stored;
        if (_dryRun)
        {
            log.Warn("Dry run: no GHCR credential in the Hosting vault; using placeholder GHCR credentials. A real run needs 'mp-host credentials set-ghcr'.");
            return new HostingCredentialStore.GhcrCreds(Placeholder("ghcr-username"), Placeholder("ghcr-token"));
        }

        throw new InvalidOperationException(
            "No GHCR credential in the Hosting vault. Store one first ('mp-host credentials set-ghcr' or HostingCredentialStore.SetGhcrCreds), " +
            "or set MINDATTIC_HOSTING_CREDENTIALS to a directory that has one.");
    }

    /// <summary>
    /// The rendezvous registration secret a deploy injects into both the game server
    /// (<c>MINDATTIC_RENDEZVOUS_SECRET</c>) and its rendezvous container
    /// (<c>Rendezvous__RegistrationSecret</c>) (UM-T52): an explicit <c>--rendezvous-secret</c> wins,
    /// else the one stored in the vault (<c>mp-host credentials set-rendezvous</c>), else a new one
    /// is generated and stored, so every later deploy/update reuses it. A dry run never writes the
    /// vault: with nothing stored it uses a labelled placeholder.
    /// </summary>
    public string ResolveRendezvousSecret(string? explicitSecret, IDeployLog log)
    {
        ArgumentNullException.ThrowIfNull(log);
        if (!string.IsNullOrWhiteSpace(explicitSecret)) return explicitSecret;
        string? stored = _credentials.GetRendezvousSecret();
        if (stored is not null) return stored;
        if (_dryRun)
        {
            log.Warn("Dry run: no rendezvous secret in the Hosting vault; a real run generates and stores one. Using a placeholder.");
            return Placeholder("rendezvous-secret");
        }

        string generated = HostingCredentialStore.GenerateRendezvousSecret();
        _credentials.SetRendezvousSecret(generated);
        log.Info("Generated a rendezvous registration secret and stored it in the Hosting vault ('mp-host credentials set-rendezvous' replaces it).");
        return generated;
    }

    /// <summary>Every hosting profile id <see cref="BuildProvisioner"/> and <see cref="BuildConnection"/> know about, for error messages.</summary>
    private static readonly string[] KnownProfiles =
    {
        GenericVpsProvisioner.Id, RunPodProvisioner.Id,
        HetznerProvisioner.Id, VultrProvisioner.Id, DigitalOceanProvisioner.Id, LinodeProvisioner.Id, OvhProvisioner.Id,
        Ec2Provisioner.Id, LightsailProvisioner.Id, GceProvisioner.Id, AzureVmProvisioner.Id, FlyProvisioner.Id, GameLiftProvisioner.Id, OciProvisioner.Id,
    };

    /// <summary>The provisioner for <paramref name="profileId"/>, wired to the vault credential (or dry-run) for it.</summary>
    /// <exception cref="ArgumentException"><paramref name="profileId"/> is not a known hosting profile.</exception>
    public IProvisioner BuildProvisioner(string profileId, IDeployLog log)
    {
        if (string.Equals(profileId, GenericVpsProvisioner.Id, StringComparison.OrdinalIgnoreCase))
        {
            return new GenericVpsProvisioner(BuildSsh(log));
        }

        if (string.Equals(profileId, RunPodProvisioner.Id, StringComparison.OrdinalIgnoreCase))
        {
            IRunPodApiClient api = _dryRun ? new DryRunRunPodApiClient(log) : BuildRealRunPodClient();
            return new RunPodProvisioner(api);
        }

        if (string.Equals(profileId, HetznerProvisioner.Id, StringComparison.OrdinalIgnoreCase))
        {
            ICloudVpsApiClient api = _dryRun ? DryRunCloudVps(HetznerProvisioner.Id, "https://api.hetzner.cloud/v1", log) : BuildHetznerClient();
            return new HetznerProvisioner(api, BuildSsh(log));
        }

        if (string.Equals(profileId, VultrProvisioner.Id, StringComparison.OrdinalIgnoreCase))
        {
            ICloudVpsApiClient api = _dryRun ? DryRunCloudVps(VultrProvisioner.Id, "https://api.vultr.com/v2", log) : BuildVultrClient();
            return new VultrProvisioner(api, BuildSsh(log));
        }

        if (string.Equals(profileId, DigitalOceanProvisioner.Id, StringComparison.OrdinalIgnoreCase))
        {
            ICloudVpsApiClient api = _dryRun ? DryRunCloudVps(DigitalOceanProvisioner.Id, "https://api.digitalocean.com/v2", log) : BuildDigitalOceanClient();
            return new DigitalOceanProvisioner(api, BuildSsh(log));
        }

        if (string.Equals(profileId, LinodeProvisioner.Id, StringComparison.OrdinalIgnoreCase))
        {
            ICloudVpsApiClient api = _dryRun ? DryRunCloudVps(LinodeProvisioner.Id, "https://api.linode.com/v4", log) : BuildLinodeClient();
            return new LinodeProvisioner(api, BuildSsh(log));
        }

        if (string.Equals(profileId, OvhProvisioner.Id, StringComparison.OrdinalIgnoreCase))
        {
            ICloudVpsApiClient api = _dryRun ? DryRunCloudVps(OvhProvisioner.Id, "https://<endpoint>.api.ovh.com/1.0", log) : BuildOvhClient();
            return new OvhProvisioner(api, BuildSsh(log));
        }

        if (string.Equals(profileId, Ec2Provisioner.Id, StringComparison.OrdinalIgnoreCase))
        {
            IEc2ApiClient api = _dryRun ? new DryRunEc2ApiClient(log) : BuildRealEc2Client();
            return new Ec2Provisioner(api, BuildSsh(log));
        }

        if (string.Equals(profileId, LightsailProvisioner.Id, StringComparison.OrdinalIgnoreCase))
        {
            ILightsailApiClient api = _dryRun ? new DryRunLightsailApiClient(log) : BuildRealLightsailClient();
            return new LightsailProvisioner(api, BuildSsh(log));
        }

        if (string.Equals(profileId, GceProvisioner.Id, StringComparison.OrdinalIgnoreCase))
        {
            IGceApiClient api = _dryRun ? new DryRunGceApiClient(log) : BuildRealGceClient();
            return new GceProvisioner(api, BuildSsh(log));
        }

        if (string.Equals(profileId, AzureVmProvisioner.Id, StringComparison.OrdinalIgnoreCase))
        {
            IAzureVmApiClient api = _dryRun ? new DryRunAzureVmApiClient(log) : BuildRealAzureVmClient();
            return new AzureVmProvisioner(api, BuildSsh(log));
        }

        if (string.Equals(profileId, FlyProvisioner.Id, StringComparison.OrdinalIgnoreCase))
        {
            IFlyApiClient api = _dryRun ? new DryRunFlyApiClient(log) : BuildRealFlyClient();
            IFlyCli cli = _dryRun ? new DryRunFlyCli(log) : new ProcessFlyCli(new ProcessRunner());
            return new FlyProvisioner(api, cli);
        }

        if (string.Equals(profileId, GameLiftProvisioner.Id, StringComparison.OrdinalIgnoreCase))
        {
            IGameLiftFleetApiClient api = _dryRun ? new DryRunGameLiftFleetApiClient(log) : BuildRealGameLiftClient();
            return new GameLiftProvisioner(api);
        }

        if (string.Equals(profileId, OciProvisioner.Id, StringComparison.OrdinalIgnoreCase))
        {
            IOciApiClient api = _dryRun ? new DryRunOciApiClient(log) : BuildRealOciClient();
            return new OciProvisioner(api, BuildSsh(log));
        }

        throw new ArgumentException($"Unknown hosting profile '{profileId}'. Known: {string.Join(", ", KnownProfiles)}.");
    }

    /// <summary>
    /// Every UM-T26/UM-T27 cloud provisioner reaches its box the same way GenericVps does once
    /// cloud-init (or the UM-T26 providers' own user-data) has booted it: over SSH. Real or
    /// dry-run, same switch as <see cref="BuildImageTool"/>.
    /// </summary>
    private ISshCommandRunner BuildSsh(IDeployLog log) => _dryRun ? new DryRunSshCommandRunner(log) : new ProcessSshCommandRunner(new ProcessRunner());

    private static DryRunCloudVpsApiClient DryRunCloudVps(string providerId, string apiBaseUrl, IDeployLog log) => new(log, providerId, apiBaseUrl);

    /// <summary>
    /// The connection info a provisioner needs for <paramref name="serverName"/>, resolved from the
    /// vault. A dry run whose profile has no stored SSH target or provider credential warns and uses
    /// clearly-labelled placeholders (<see cref="PlaceholderConnection"/>) instead of refusing
    /// (UM-T49); a big-cloud profile's launch options are configuration naming real local files
    /// (<c>sshPublicKeyPath</c>, AMI ids, subnets), not credentials, so those are still required.
    /// </summary>
    /// <exception cref="InvalidOperationException">The profile needs a stored credential/target that isn't there (and this is not a dry run, or no placeholder can stand in).</exception>
    public ProviderConnection BuildConnection(string profileId, string serverName, IDeployLog log)
    {
        ArgumentNullException.ThrowIfNull(log);
        try
        {
            return BuildStoredConnection(profileId, serverName);
        }
        catch (InvalidOperationException) when (_dryRun)
        {
            ProviderConnection? placeholder = PlaceholderConnection(profileId, serverName);
            if (placeholder is null) throw;
            log.Warn($"Dry run: no stored '{profileId}' credential/SSH target for '{serverName}' in the Hosting vault; using placeholder values. "
                + "A real run needs them stored with 'mp-host credentials'.");
            return placeholder;
        }
    }

    /// <summary>
    /// The dry-run stand-in for a missing stored connection, every value labelled with
    /// <see cref="PlaceholderPrefix"/>; null for a profile whose connection needs real local
    /// configuration no placeholder can stand in for (the big-cloud launch options, OCI).
    /// </summary>
    internal static ProviderConnection? PlaceholderConnection(string profileId, string serverName)
    {
        if (string.Equals(profileId, GenericVpsProvisioner.Id, StringComparison.OrdinalIgnoreCase))
        {
            return new ProviderConnection(serverName, new Dictionary<string, string>
            {
                ["sshHost"] = Placeholder("ssh-host"),
                ["sshPort"] = "22",
                ["sshUser"] = Placeholder("ssh-user"),
                ["sshKeyPath"] = Placeholder("ssh-key-path"),
            }, new Dictionary<string, string>());
        }

        if (string.Equals(profileId, HetznerProvisioner.Id, StringComparison.OrdinalIgnoreCase)
            || string.Equals(profileId, VultrProvisioner.Id, StringComparison.OrdinalIgnoreCase)
            || string.Equals(profileId, DigitalOceanProvisioner.Id, StringComparison.OrdinalIgnoreCase)
            || string.Equals(profileId, LinodeProvisioner.Id, StringComparison.OrdinalIgnoreCase)
            || string.Equals(profileId, OvhProvisioner.Id, StringComparison.OrdinalIgnoreCase))
        {
            return CloudVpsConnection(serverName, Placeholder("ssh-user"), Placeholder("ssh-key-path"), Placeholder("ssh-public-key"));
        }

        if (string.Equals(profileId, FlyProvisioner.Id, StringComparison.OrdinalIgnoreCase))
        {
            return new ProviderConnection(serverName, new Dictionary<string, string> { ["orgSlug"] = Placeholder("fly-org") }, new Dictionary<string, string>());
        }

        return null;
    }

    private ProviderConnection BuildStoredConnection(string profileId, string serverName)
    {
        if (string.Equals(profileId, GenericVpsProvisioner.Id, StringComparison.OrdinalIgnoreCase))
        {
            var ssh = _credentials.GetSshCreds(serverName) ?? throw new InvalidOperationException(
                $"No SSH target stored for '{serverName}'. Store one first (HostingCredentialStore.SetSshCreds).");
            var options = new Dictionary<string, string>
            {
                ["sshHost"] = ssh.Host,
                ["sshPort"] = ssh.Port.ToString(),
                ["sshUser"] = ssh.User,
                ["sshKeyPath"] = ssh.PrivateKeyPath,
            };
            var secrets = new Dictionary<string, string>();
            if (!string.IsNullOrEmpty(ssh.Passphrase))
            {
                secrets["sshPassphrase"] = ssh.Passphrase;
            }

            return new ProviderConnection(serverName, options, secrets);
        }

        if (string.Equals(profileId, HetznerProvisioner.Id, StringComparison.OrdinalIgnoreCase))
        {
            var creds = _credentials.GetHetznerCreds() ?? throw new InvalidOperationException(
                "No Hetzner credential in the Hosting vault. Store one first (HostingCredentialStore.SetHetznerCreds).");
            return CloudVpsConnection(serverName, creds);
        }

        if (string.Equals(profileId, VultrProvisioner.Id, StringComparison.OrdinalIgnoreCase))
        {
            var creds = _credentials.GetVultrCreds() ?? throw new InvalidOperationException(
                "No Vultr credential in the Hosting vault. Store one first (HostingCredentialStore.SetVultrCreds).");
            return CloudVpsConnection(serverName, creds);
        }

        if (string.Equals(profileId, DigitalOceanProvisioner.Id, StringComparison.OrdinalIgnoreCase))
        {
            var creds = _credentials.GetDigitalOceanCreds() ?? throw new InvalidOperationException(
                "No DigitalOcean credential in the Hosting vault. Store one first (HostingCredentialStore.SetDigitalOceanCreds).");
            return CloudVpsConnection(serverName, creds);
        }

        if (string.Equals(profileId, LinodeProvisioner.Id, StringComparison.OrdinalIgnoreCase))
        {
            var creds = _credentials.GetLinodeCreds() ?? throw new InvalidOperationException(
                "No Linode credential in the Hosting vault. Store one first (HostingCredentialStore.SetLinodeCreds).");
            return CloudVpsConnection(serverName, creds);
        }

        if (string.Equals(profileId, OvhProvisioner.Id, StringComparison.OrdinalIgnoreCase))
        {
            var creds = _credentials.GetOvhCreds() ?? throw new InvalidOperationException(
                "No OVHcloud credential in the Hosting vault. Store one first (HostingCredentialStore.SetOvhCreds).");
            return CloudVpsConnection(serverName, creds.SshUser, creds.SshPrivateKeyPath, creds.SshPublicKey);
        }

        if (IsBigCloudProfile(profileId))
        {
            var options = _credentials.GetCloudVmOptions(profileId, serverName) ?? throw new InvalidOperationException(
                $"No launch options stored for '{profileId}'/'{serverName}'. Store them first (HostingCredentialStore.SetCloudVmOptions, " +
                "or 'mp-host credentials set-cloud-options').");
            return new ProviderConnection(serverName, options, new Dictionary<string, string>());
        }

        if (string.Equals(profileId, FlyProvisioner.Id, StringComparison.OrdinalIgnoreCase))
        {
            var creds = _credentials.GetFlyCreds() ?? throw new InvalidOperationException(
                "No Fly.io credential in the Hosting vault. Store one first (HostingCredentialStore.SetFlyCreds, or 'mp-host credentials set-fly').");
            var options = new Dictionary<string, string>(_credentials.GetCloudVmOptions(profileId, serverName) ?? new Dictionary<string, string>())
            {
                ["orgSlug"] = creds.OrgSlug,
            };
            return new ProviderConnection(serverName, options, new Dictionary<string, string>());
        }

        if (string.Equals(profileId, OciProvisioner.Id, StringComparison.OrdinalIgnoreCase))
        {
            var creds = _credentials.GetOciCreds() ?? throw new InvalidOperationException(
                "No OCI credential in the Hosting vault. Store one first (HostingCredentialStore.SetOciCreds, or 'mp-host credentials set-oci').");
            var launchOptions = _credentials.GetCloudVmOptions(profileId, serverName) ?? throw new InvalidOperationException(
                $"No launch options stored for '{profileId}'/'{serverName}'. Store them first (HostingCredentialStore.SetCloudVmOptions, " +
                "or 'mp-host credentials set-cloud-options').");
            var options = new Dictionary<string, string>(launchOptions) { ["compartmentId"] = creds.CompartmentOcid };
            return new ProviderConnection(serverName, options, new Dictionary<string, string>());
        }

        return ProviderConnection.Named(serverName);
    }

    /// <summary>
    /// The SSH-fallback half of a UM-T26 cloud provider's <see cref="ProviderConnection"/>: the
    /// shared SSH keypair mp-host registers with the provider and uses for status/logs/update once a
    /// server has a public IP. The host itself is never in here -- it is only known after
    /// <see cref="IProvisioner.ProvisionAsync"/> creates the server, so every later call resolves it
    /// fresh from the provider's API.
    /// </summary>
    private static ProviderConnection CloudVpsConnection(string serverName, HostingCredentialStore.CloudVpsCreds creds) =>
        CloudVpsConnection(serverName, creds.SshUser, creds.SshPrivateKeyPath, creds.SshPublicKey);

    private static ProviderConnection CloudVpsConnection(string serverName, string sshUser, string sshKeyPath, string sshPublicKey)
    {
        var options = new Dictionary<string, string>
        {
            ["sshUser"] = sshUser,
            ["sshKeyPath"] = sshKeyPath,
            ["sshPublicKey"] = sshPublicKey,
        };
        return new ProviderConnection(serverName, options, new Dictionary<string, string>());
    }

    /// <summary>
    /// True for a UM-T27 big-cloud profile, whose launch options come from the generic
    /// <see cref="HostingCredentialStore.GetCloudVmOptions"/> bag rather than a typed credential.
    /// </summary>
    private static bool IsBigCloudProfile(string profileId) =>
        string.Equals(profileId, Ec2Provisioner.Id, StringComparison.OrdinalIgnoreCase)
        || string.Equals(profileId, LightsailProvisioner.Id, StringComparison.OrdinalIgnoreCase)
        || string.Equals(profileId, GceProvisioner.Id, StringComparison.OrdinalIgnoreCase)
        || string.Equals(profileId, AzureVmProvisioner.Id, StringComparison.OrdinalIgnoreCase)
        || string.Equals(profileId, GameLiftProvisioner.Id, StringComparison.OrdinalIgnoreCase);

    private HttpRunPodApiClient BuildRealRunPodClient()
    {
        var creds = _credentials.GetRunPodCreds() ?? throw new InvalidOperationException(
            "No RunPod API key in the Hosting vault. Store one first (HostingCredentialStore.SetRunPodCreds).");
        var http = new HttpClient { BaseAddress = new Uri("https://api.runpod.io/graphql") };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", creds.ApiKey);
        return new HttpRunPodApiClient(http);
    }

    private HttpHetznerApiClient BuildHetznerClient()
    {
        var creds = _credentials.GetHetznerCreds() ?? throw new InvalidOperationException(
            "No Hetzner credential in the Hosting vault. Store one first (HostingCredentialStore.SetHetznerCreds).");
        var http = new HttpClient { BaseAddress = new Uri("https://api.hetzner.cloud/v1/") };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", creds.ApiKey);
        return new HttpHetznerApiClient(http);
    }

    private HttpVultrApiClient BuildVultrClient()
    {
        var creds = _credentials.GetVultrCreds() ?? throw new InvalidOperationException(
            "No Vultr credential in the Hosting vault. Store one first (HostingCredentialStore.SetVultrCreds).");
        var http = new HttpClient { BaseAddress = new Uri("https://api.vultr.com/v2/") };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", creds.ApiKey);
        return new HttpVultrApiClient(http);
    }

    private HttpDigitalOceanApiClient BuildDigitalOceanClient()
    {
        var creds = _credentials.GetDigitalOceanCreds() ?? throw new InvalidOperationException(
            "No DigitalOcean credential in the Hosting vault. Store one first (HostingCredentialStore.SetDigitalOceanCreds).");
        var http = new HttpClient { BaseAddress = new Uri("https://api.digitalocean.com/v2/") };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", creds.ApiKey);
        return new HttpDigitalOceanApiClient(http);
    }

    private HttpLinodeApiClient BuildLinodeClient()
    {
        var creds = _credentials.GetLinodeCreds() ?? throw new InvalidOperationException(
            "No Linode credential in the Hosting vault. Store one first (HostingCredentialStore.SetLinodeCreds).");
        var http = new HttpClient { BaseAddress = new Uri("https://api.linode.com/v4/") };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", creds.ApiKey);
        return new HttpLinodeApiClient(http);
    }

    private HttpOvhApiClient BuildOvhClient()
    {
        var creds = _credentials.GetOvhCreds() ?? throw new InvalidOperationException(
            "No OVHcloud credential in the Hosting vault. Store one first (HostingCredentialStore.SetOvhCreds).");
        var http = new HttpClient { BaseAddress = new Uri($"https://{creds.Endpoint}.api.ovh.com/1.0/") };
        return new HttpOvhApiClient(http, creds.ApplicationKey, creds.ApplicationSecret, creds.ConsumerKey, creds.ServiceName);
    }

    private HostingCredentialStore.AwsCreds RequireAwsCreds() =>
        _credentials.GetAwsCreds() ?? throw new InvalidOperationException(
            "No AWS credential in the Hosting vault. Store one first (HostingCredentialStore.SetAwsCreds, or 'mp-host credentials set-aws').");

    private HttpEc2ApiClient BuildRealEc2Client()
    {
        var creds = RequireAwsCreds();
        var http = new HttpClient { BaseAddress = new Uri($"https://ec2.{creds.Region}.amazonaws.com/") };
        return new HttpEc2ApiClient(http, creds.AccessKeyId, creds.SecretAccessKey, creds.Region);
    }

    private HttpLightsailApiClient BuildRealLightsailClient()
    {
        var creds = RequireAwsCreds();
        var http = new HttpClient { BaseAddress = new Uri($"https://lightsail.{creds.Region}.amazonaws.com/") };
        return new HttpLightsailApiClient(http, creds.AccessKeyId, creds.SecretAccessKey, creds.Region);
    }

    private HttpGceApiClient BuildRealGceClient()
    {
        var creds = _credentials.GetGceCreds() ?? throw new InvalidOperationException(
            "No GCE credential in the Hosting vault. Store one first (HostingCredentialStore.SetGceCreds, or 'mp-host credentials set-gce').");
        var http = new HttpClient { BaseAddress = new Uri("https://compute.googleapis.com/compute/v1/") };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", creds.AccessToken);
        return new HttpGceApiClient(http, creds.ProjectId);
    }

    private HttpAzureVmApiClient BuildRealAzureVmClient()
    {
        var creds = _credentials.GetAzureCreds() ?? throw new InvalidOperationException(
            "No Azure credential in the Hosting vault. Store one first (HostingCredentialStore.SetAzureCreds, or 'mp-host credentials set-azure').");
        return new HttpAzureVmApiClient(new HttpClient(), creds.TenantId, creds.ClientId, creds.ClientSecret, creds.SubscriptionId);
    }

    private HttpFlyApiClient BuildRealFlyClient()
    {
        var creds = _credentials.GetFlyCreds() ?? throw new InvalidOperationException(
            "No Fly.io credential in the Hosting vault. Store one first (HostingCredentialStore.SetFlyCreds, or 'mp-host credentials set-fly').");
        var http = new HttpClient { BaseAddress = new Uri("https://api.machines.dev/v1/") };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", creds.ApiToken);
        return new HttpFlyApiClient(http);
    }

    private HttpGameLiftFleetApiClient BuildRealGameLiftClient()
    {
        var creds = RequireAwsCreds();
        var http = new HttpClient { BaseAddress = new Uri($"https://gamelift.{creds.Region}.amazonaws.com/") };
        return new HttpGameLiftFleetApiClient(http, new HttpClient(), creds.AccessKeyId, creds.SecretAccessKey, creds.Region);
    }

    private HttpOciApiClient BuildRealOciClient()
    {
        var creds = _credentials.GetOciCreds() ?? throw new InvalidOperationException(
            "No OCI credential in the Hosting vault. Store one first (HostingCredentialStore.SetOciCreds, or 'mp-host credentials set-oci').");
        var http = new HttpClient { BaseAddress = new Uri($"https://iaas.{creds.Region}.oraclecloud.com/") };
        string privateKeyPem = File.ReadAllText(creds.PrivateKeyPath);
        return new HttpOciApiClient(http, creds.CompartmentOcid, creds.TenancyOcid, creds.UserOcid, creds.Fingerprint, privateKeyPem);
    }
}
