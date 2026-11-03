using System.Globalization;
using System.Text.Json;
using MindAttic.Vault.Credentials;
using MindAttic.Vault.Paths;

namespace Unity.Multiplayer.Hosting.Cli.Credentials;

/// <summary>
/// Hosting keyring at <c>%APPDATA%\MindAttic\Hosting\</c> (MindAttic.Vault, never the repo). One
/// entry per provider id mp-host knows about (<c>ghcr</c>, <c>genericvps:&lt;server-name&gt;</c>,
/// <c>runpod</c>, ...). Shape mirrors <c>MindAttic.Vault.Credentials.BrokerCredentialStore</c>:
/// <code>
/// {
///   "ghcr":      { "type": "ghcr",      "apiKey": "&lt;PAT with write:packages&gt;", "secret": "&lt;github username&gt;" },
///   "runpod":    { "type": "runpod",    "apiKey": "&lt;RunPod API key&gt;" },
///   "rendezvous": { "type": "rendezvous", "apiKey": "&lt;rendezvous registration secret&gt;" },
///   "genericvps:prod-box": { "type": "ssh", "apiKey": "&lt;passphrase or empty&gt;", "secret": "root@203.0.113.10:22",
///                             "sshKeyPath": "C:\\Users\\me\\.ssh\\id_ed25519", "sshFormat": 2 }
/// }
/// </code>
/// Every real provisioner call reads from here; nothing it needs is ever passed on the command
/// line or committed to a project file. Tests construct their own instance over a temp directory
/// instead of touching the real keyring.
/// </summary>
public sealed class HostingCredentialStore : CredentialStore
{
    /// <summary>Bucket folder name under <c>%APPDATA%\MindAttic\</c>.</summary>
    public const string Bucket = "Hosting";

    /// <summary>Environment variable that overrides the resolved bucket directory.</summary>
    public const string DirectoryEnvVar = "MINDATTIC_HOSTING_CREDENTIALS";

    /// <summary>Default instance rooted at <c>%APPDATA%\MindAttic\Hosting\</c> (or <see cref="DirectoryEnvVar"/>).</summary>
    public static HostingCredentialStore Default { get; } = new(ResolveDefaultDirectory());

    /// <inheritdoc />
    public HostingCredentialStore(string directory) : base(directory) { }

    private static string ResolveDefaultDirectory()
    {
        var overrideDir = Environment.GetEnvironmentVariable(DirectoryEnvVar);
        return string.IsNullOrWhiteSpace(overrideDir) ? VaultPaths.RoamingBucket(Bucket) : overrideDir;
    }

    /// <summary>A GitHub Container Registry token: <paramref name="Username"/> + a PAT with <c>write:packages</c>.</summary>
    public sealed record GhcrCreds(string Username, string Token);

    /// <summary>An SSH target for a bring-your-own (generic VPS) host.</summary>
    public sealed record SshCreds(string Host, int Port, string User, string PrivateKeyPath, string? Passphrase);

    /// <summary>A bare provider API key (RunPod and friends).</summary>
    public sealed record ApiKeyCreds(string ApiKey);

    /// <summary>
    /// A UM-T26 plain-VPS cloud provider's API token plus the one SSH keypair mp-host registers
    /// with that provider and bakes into every server it creates (Hetzner, Vultr, DigitalOcean,
    /// Linode share this shape; OVH needs <see cref="OvhCreds"/> instead, for its signature auth).
    /// </summary>
    public sealed record CloudVpsCreds(string ApiKey, string SshUser, string SshPrivateKeyPath, string SshPublicKey);

    /// <summary>
    /// OVHcloud's signature-based API credential (application key/secret + a consumer key validated
    /// for the Public Cloud project), its regional endpoint and project id, plus the same SSH keypair
    /// shape as <see cref="CloudVpsCreds"/>.
    /// </summary>
    public sealed record OvhCreds(
        string Endpoint, string ServiceName, string ApplicationKey, string ApplicationSecret, string ConsumerKey,
        string SshUser, string SshPrivateKeyPath, string SshPublicKey);

    /// <summary>An AWS account's long-term access key (AwsEc2, AwsLightsail and GameLift share one; each deploy's <c>--region</c> picks the endpoint).</summary>
    public sealed record AwsCreds(string AccessKeyId, string SecretAccessKey, string Region);

    /// <summary>A GCE project id plus an operator-refreshed OAuth2 access token (see <c>docs/articles/hosting/gce.md</c> for how to mint one).</summary>
    public sealed record GceCreds(string ProjectId, string AccessToken);

    /// <summary>An Entra ID (Azure AD) service principal mp-host uses for ARM's client-credentials flow.</summary>
    public sealed record AzureCreds(string TenantId, string ClientId, string ClientSecret, string SubscriptionId);

    /// <summary>A Fly.io API token plus the org slug mp-host creates apps under (e.g. <c>personal</c>).</summary>
    public sealed record FlyCreds(string ApiToken, string OrgSlug);

    /// <summary>
    /// An OCI API signing key (UM-T31): the tenancy/user OCIDs and key fingerprint from the OCI
    /// Console's "API Keys" page, the PEM private key half's file path, the region to call (e.g.
    /// <c>us-ashburn-1</c>), and the compartment OCID every instance is created in.
    /// </summary>
    public sealed record OciCreds(string TenancyOcid, string UserOcid, string Fingerprint, string PrivateKeyPath, string Region, string CompartmentOcid);

    /// <summary>
    /// An Edgegap API token plus the app name <c>Unity.Multiplayer.Core.EdgegapMatchAllocator</c> deploys
    /// (UM-T29). mp-host itself never calls Edgegap's API; this only gives a matchmaking service (or a
    /// one-off script) a vault-backed place to read the token from instead of hard-coding it.
    /// </summary>
    public sealed record EdgegapCreds(string ApiToken, string AppName);

    /// <summary>
    /// A Gameye API key plus the image name and location <c>Unity.Multiplayer.Core.GameyeMatchAllocator</c>
    /// starts sessions with (UM-T29). mp-host itself never calls Gameye's API; this only gives a
    /// matchmaking service (or a one-off script) a vault-backed place to read the key from instead of
    /// hard-coding it.
    /// </summary>
    public sealed record GameyeCreds(string ApiKey, string ImageName, string Location);

    /// <summary>GHCR provider id in this store.</summary>
    public const string GhcrProviderId = "ghcr";

    /// <summary>RunPod provider id in this store.</summary>
    public const string RunPodProviderId = "runpod";

    /// <summary>Hetzner Cloud provider id in this store.</summary>
    public const string HetznerProviderId = "hetzner";

    /// <summary>Vultr provider id in this store.</summary>
    public const string VultrProviderId = "vultr";

    /// <summary>DigitalOcean provider id in this store.</summary>
    public const string DigitalOceanProviderId = "digitalocean";

    /// <summary>Linode (Akamai Cloud Computing) provider id in this store.</summary>
    public const string LinodeProviderId = "linode";

    /// <summary>OVHcloud provider id in this store.</summary>
    public const string OvhProviderId = "ovh";

    /// <summary>AWS (EC2 + Lightsail) provider id in this store.</summary>
    public const string AwsProviderId = "aws";

    /// <summary>GCE provider id in this store.</summary>
    public const string GceProviderId = "gce";

    /// <summary>Azure provider id in this store.</summary>
    public const string AzureProviderId = "azure";

    /// <summary>Fly.io provider id in this store.</summary>
    public const string FlyProviderId = "fly";

    /// <summary>OCI (Oracle Cloud Infrastructure) provider id in this store.</summary>
    public const string OciProviderId = "oci";

    /// <summary>Edgegap provider id in this store.</summary>
    public const string EdgegapProviderId = "edgegap";

    /// <summary>Gameye provider id in this store.</summary>
    public const string GameyeProviderId = "gameye";

    /// <summary>
    /// The rendezvous registration secret's id in this store (UM-T52): the shared secret a dedicated
    /// server sends as <c>MINDATTIC_RENDEZVOUS_SECRET</c> and its rendezvous container checks as
    /// <c>Rendezvous__RegistrationSecret</c>.
    /// </summary>
    public const string RendezvousProviderId = "rendezvous";

    /// <summary>
    /// What <c>mp-host local up</c> remembers between invocations so <c>mp-host local down</c> can
    /// find the same package directory and compose file (and know whether to remove a UPnP mapping)
    /// without the operator repeating every flag. Not a secret -- stored in the same keyring purely
    /// for convenience, the same way <see cref="GetCloudVmOptions"/> stores non-secret launch options.
    /// </summary>
    public sealed record LocalServerState(string PackageDirectory, string ComposeFileName, int Port, string Protocol, bool UpnpMapped);

    /// <summary>The generic-VPS provider id for a given server name (one SSH target per server).</summary>
    public static string GenericVpsProviderId(string serverName) => "genericvps:" + serverName;

    /// <summary>The local-hosting state id for a given server name.</summary>
    public static string LocalStateProviderId(string serverName) => "local:" + serverName;

    /// <summary>The cloud-VM launch-options id for a given profile + server name (one set of options per server).</summary>
    public static string CloudVmOptionsProviderId(string profileId, string serverName) => $"cloudvmoptions:{profileId}:{serverName}";

    /// <summary>Reads the GHCR credential, or null when missing or incomplete.</summary>
    public GhcrCreds? GetGhcrCreds()
    {
        var raw = LoadAllRaw();
        if (!raw.TryGetValue(GhcrProviderId, out var json) || string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            string token = StringProp(doc, "apiKey");
            string user = StringProp(doc, "secret");
            if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(user)) return null;
            return new GhcrCreds(user.Trim(), token.Trim());
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Upserts the GHCR credential.</summary>
    public void SetGhcrCreds(GhcrCreds creds)
    {
        ArgumentNullException.ThrowIfNull(creds);
        SaveRaw(GhcrProviderId, Write(w =>
        {
            w.WriteString("type", "ghcr");
            w.WriteString("apiKey", creds.Token);
            w.WriteString("secret", creds.Username);
        }));
    }

    /// <summary>Reads the RunPod API key, or null when missing.</summary>
    public ApiKeyCreds? GetRunPodCreds()
    {
        string? key = GetKey(RunPodProviderId);
        return string.IsNullOrWhiteSpace(key) ? null : new ApiKeyCreds(key);
    }

    /// <summary>Upserts the RunPod API key.</summary>
    public void SetRunPodCreds(string apiKey) => SetKey(RunPodProviderId, apiKey);

    /// <summary>Reads the stored rendezvous registration secret, or null when none is stored.</summary>
    public string? GetRendezvousSecret()
    {
        string? secret = GetKey(RendezvousProviderId);
        return string.IsNullOrWhiteSpace(secret) ? null : secret.Trim();
    }

    /// <summary>Upserts the rendezvous registration secret.</summary>
    public void SetRendezvousSecret(string secret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);
        SaveRaw(RendezvousProviderId, Write(w =>
        {
            w.WriteString("type", "rendezvous");
            w.WriteString("apiKey", secret.Trim());
        }));
    }

    /// <summary>
    /// A new random rendezvous registration secret: 32 bytes from a cryptographic RNG, base64url
    /// without padding, so it never holds a character YAML, a shell or Docker Compose's <c>$</c>
    /// interpolation treats specially.
    /// </summary>
    public static string GenerateRendezvousSecret() =>
        Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Reads the SSH target for <paramref name="serverName"/>'s generic-VPS entry, or null when missing or incomplete.</summary>
    public SshCreds? GetSshCreds(string serverName)
    {
        var raw = LoadAllRaw();
        if (!raw.TryGetValue(GenericVpsProviderId(serverName), out var json) || string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            string hostPort = StringProp(doc, "secret");
            string user = "root";
            string host = hostPort;
            int port = 22;
            int at = hostPort.IndexOf('@');
            if (at >= 0)
            {
                user = hostPort[..at];
                hostPort = hostPort[(at + 1)..];
            }

            bool current = IsCurrentSshFormat(doc);
            (host, port) = current ? SplitHostPort(hostPort, port) : SplitLegacyHostPort(hostPort, port);

            string keyPath = StringProp(doc, "sshKeyPath");
            if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(keyPath)) return null;
            string? passphrase = StringPropOrNull(doc, "apiKey");
            var creds = new SshCreds(host, port, user, keyPath, string.IsNullOrWhiteSpace(passphrase) ? null : passphrase);
            if (!current) TryMigrateSshCreds(serverName, creds);
            return creds;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The <c>sshFormat</c> marker <see cref="SetSshCreds"/> writes since UM-T89. Entries without it were
    /// written before UM-T88 bracketed IPv6 hosts, so an unbracketed IPv6 "secret" in them still ends in
    /// the port (<c>root@2001:db8::1:22</c>) and is read by <see cref="SplitLegacyHostPort"/>.
    /// </summary>
    internal const int CurrentSshFormat = 2;

    private static bool IsCurrentSshFormat(JsonDocument doc) =>
        doc.RootElement.ValueKind == JsonValueKind.Object
        && doc.RootElement.TryGetProperty("sshFormat", out var v)
        && v.ValueKind == JsonValueKind.Number
        && v.TryGetInt32(out int format)
        && format >= CurrentSshFormat;

    /// <summary>
    /// Splits an unmarked (pre-UM-T89) "host[:port]" the way the old code did (UM-T89): the old
    /// SetSshCreds always appended ":port" and never bracketed, so "2001:db8::1:22" is host
    /// "2001:db8::1", port 22 (split on the last colon). Bracketed values (only ever hand-entered) and
    /// unbracketed multi-colon values whose prefix is not a valid IPv6 address (a bare address with no
    /// port, e.g. "2001:db8::1") fall through to the current <see cref="SplitHostPort"/> rule.
    /// </summary>
    internal static (string Host, int Port) SplitLegacyHostPort(string hostPort, int defaultPort)
    {
        if (!hostPort.StartsWith('[') && hostPort.IndexOf(':') != hostPort.LastIndexOf(':'))
        {
            int colon = hostPort.LastIndexOf(':');
            string prefix = hostPort[..colon];
            if (int.TryParse(hostPort[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out int legacyPort)
                && System.Net.IPAddress.TryParse(prefix, out var address)
                && address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
            {
                return (prefix, legacyPort);
            }
        }

        return SplitHostPort(hostPort, defaultPort);
    }

    /// <summary>
    /// Rewrites an old-format entry in the current (marked, IPv6-bracketed) form so nobody has to re-save
    /// it (UM-T89). Best effort: a read never fails because the keyring could not be written.
    /// </summary>
    private void TryMigrateSshCreds(string serverName, SshCreds creds)
    {
        try
        {
            SetSshCreds(serverName, creds);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Splits the stored "host[:port]" (after any "user@"), IPv6-aware (UM-T88): "[2001:db8::1]:2222" and
    /// "[2001:db8::1]" are bracketed IPv6 with and without a port; an unbracketed value with more than one
    /// colon ("2001:db8::1") is a bare IPv6 address and takes the default port, since its last group would
    /// otherwise be misread as one; "host:2222" and "203.0.113.5" keep their old meaning.
    /// </summary>
    internal static (string Host, int Port) SplitHostPort(string hostPort, int defaultPort)
    {
        if (hostPort.StartsWith('['))
        {
            int close = hostPort.IndexOf(']');
            if (close < 0) return (hostPort, defaultPort);
            string inner = hostPort[1..close];
            string rest = hostPort[(close + 1)..];
            if (rest.StartsWith(':') && int.TryParse(rest[1..], NumberStyles.None, CultureInfo.InvariantCulture, out int bracketPort))
                return (inner, bracketPort);
            return (inner, defaultPort);
        }

        int first = hostPort.IndexOf(':');
        int colon = hostPort.LastIndexOf(':');
        if (first != colon) return (hostPort, defaultPort);
        if (colon >= 0 && int.TryParse(hostPort[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out int parsedPort))
            return (hostPort[..colon], parsedPort);
        return (hostPort, defaultPort);
    }

    /// <summary>Upserts the SSH target for <paramref name="serverName"/>'s generic-VPS entry.</summary>
    public void SetSshCreds(string serverName, SshCreds creds)
    {
        ArgumentNullException.ThrowIfNull(creds);
        SaveRaw(GenericVpsProviderId(serverName), Write(w =>
        {
            w.WriteString("type", "ssh");
            w.WriteString("apiKey", creds.Passphrase ?? "");
            // An IPv6 host is bracketed so its port can be told from its last group on the way back in.
            string host = creds.Host.Contains(':') && !creds.Host.StartsWith('[') ? $"[{creds.Host}]" : creds.Host;
            w.WriteString("secret", $"{creds.User}@{host}:{creds.Port}");
            w.WriteString("sshKeyPath", creds.PrivateKeyPath);
            w.WriteNumber("sshFormat", CurrentSshFormat);
        }));
    }

    /// <summary>Reads the Hetzner Cloud credential, or null when missing or incomplete.</summary>
    public CloudVpsCreds? GetHetznerCreds() => GetCloudVpsCreds(HetznerProviderId);

    /// <summary>Upserts the Hetzner Cloud credential.</summary>
    public void SetHetznerCreds(CloudVpsCreds creds) => SetCloudVpsCreds(HetznerProviderId, creds);

    /// <summary>Reads the Vultr credential, or null when missing or incomplete.</summary>
    public CloudVpsCreds? GetVultrCreds() => GetCloudVpsCreds(VultrProviderId);

    /// <summary>Upserts the Vultr credential.</summary>
    public void SetVultrCreds(CloudVpsCreds creds) => SetCloudVpsCreds(VultrProviderId, creds);

    /// <summary>Reads the DigitalOcean credential, or null when missing or incomplete.</summary>
    public CloudVpsCreds? GetDigitalOceanCreds() => GetCloudVpsCreds(DigitalOceanProviderId);

    /// <summary>Upserts the DigitalOcean credential.</summary>
    public void SetDigitalOceanCreds(CloudVpsCreds creds) => SetCloudVpsCreds(DigitalOceanProviderId, creds);

    /// <summary>Reads the Linode credential, or null when missing or incomplete.</summary>
    public CloudVpsCreds? GetLinodeCreds() => GetCloudVpsCreds(LinodeProviderId);

    /// <summary>Upserts the Linode credential.</summary>
    public void SetLinodeCreds(CloudVpsCreds creds) => SetCloudVpsCreds(LinodeProviderId, creds);

    /// <summary>Reads the OVHcloud credential, or null when missing or incomplete.</summary>
    public OvhCreds? GetOvhCreds()
    {
        var raw = LoadAllRaw();
        if (!raw.TryGetValue(OvhProviderId, out var json) || string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            string endpoint = StringProp(doc, "endpoint");
            string serviceName = StringProp(doc, "serviceName");
            string appKey = StringProp(doc, "appKey");
            string appSecret = StringProp(doc, "apiKey");
            string consumerKey = StringProp(doc, "secret");
            string sshUser = StringPropOrNull(doc, "sshUser") ?? "root";
            string sshKeyPath = StringProp(doc, "sshKeyPath");
            string sshPublicKey = StringProp(doc, "sshPublicKey");
            if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(serviceName) || string.IsNullOrWhiteSpace(appKey)
                || string.IsNullOrWhiteSpace(appSecret) || string.IsNullOrWhiteSpace(consumerKey)
                || string.IsNullOrWhiteSpace(sshKeyPath) || string.IsNullOrWhiteSpace(sshPublicKey))
            {
                return null;
            }

            return new OvhCreds(endpoint, serviceName, appKey, appSecret, consumerKey, sshUser, sshKeyPath, sshPublicKey);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Upserts the OVHcloud credential.</summary>
    public void SetOvhCreds(OvhCreds creds)
    {
        ArgumentNullException.ThrowIfNull(creds);
        SaveRaw(OvhProviderId, Write(w =>
        {
            w.WriteString("type", "ovh");
            w.WriteString("endpoint", creds.Endpoint);
            w.WriteString("serviceName", creds.ServiceName);
            w.WriteString("appKey", creds.ApplicationKey);
            w.WriteString("apiKey", creds.ApplicationSecret);
            w.WriteString("secret", creds.ConsumerKey);
            w.WriteString("sshUser", creds.SshUser);
            w.WriteString("sshKeyPath", creds.SshPrivateKeyPath);
            w.WriteString("sshPublicKey", creds.SshPublicKey);
        }));
    }

    private CloudVpsCreds? GetCloudVpsCreds(string providerId)
    {
        var raw = LoadAllRaw();
        if (!raw.TryGetValue(providerId, out var json) || string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            string apiKey = StringProp(doc, "apiKey");
            string sshUser = StringPropOrNull(doc, "sshUser") ?? "root";
            string sshKeyPath = StringProp(doc, "sshKeyPath");
            string sshPublicKey = StringProp(doc, "secret");
            if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(sshKeyPath) || string.IsNullOrWhiteSpace(sshPublicKey))
            {
                return null;
            }

            return new CloudVpsCreds(apiKey, sshUser, sshKeyPath, sshPublicKey);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private void SetCloudVpsCreds(string providerId, CloudVpsCreds creds)
    {
        ArgumentNullException.ThrowIfNull(creds);
        SaveRaw(providerId, Write(w =>
        {
            w.WriteString("type", "cloudvps");
            w.WriteString("apiKey", creds.ApiKey);
            w.WriteString("secret", creds.SshPublicKey);
            w.WriteString("sshUser", creds.SshUser);
            w.WriteString("sshKeyPath", creds.SshPrivateKeyPath);
        }));
    }

    /// <summary>Reads the AWS account credential, or null when missing or incomplete.</summary>
    public AwsCreds? GetAwsCreds()
    {
        var raw = LoadAllRaw();
        if (!raw.TryGetValue(AwsProviderId, out var json) || string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            string accessKeyId = StringProp(doc, "accessKeyId");
            string secretAccessKey = StringProp(doc, "secretAccessKey");
            string region = StringProp(doc, "region");
            if (string.IsNullOrWhiteSpace(accessKeyId) || string.IsNullOrWhiteSpace(secretAccessKey) || string.IsNullOrWhiteSpace(region)) return null;
            return new AwsCreds(accessKeyId, secretAccessKey, region);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Upserts the AWS account credential.</summary>
    public void SetAwsCreds(AwsCreds creds)
    {
        ArgumentNullException.ThrowIfNull(creds);
        SaveRaw(AwsProviderId, Write(w =>
        {
            w.WriteString("type", "aws");
            w.WriteString("accessKeyId", creds.AccessKeyId);
            w.WriteString("secretAccessKey", creds.SecretAccessKey);
            w.WriteString("region", creds.Region);
        }));
    }

    /// <summary>Reads the GCE project + access token credential, or null when missing or incomplete.</summary>
    public GceCreds? GetGceCreds()
    {
        var raw = LoadAllRaw();
        if (!raw.TryGetValue(GceProviderId, out var json) || string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            string projectId = StringProp(doc, "projectId");
            string accessToken = StringProp(doc, "accessToken");
            if (string.IsNullOrWhiteSpace(projectId) || string.IsNullOrWhiteSpace(accessToken)) return null;
            return new GceCreds(projectId, accessToken);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Upserts the GCE project + access token credential.</summary>
    public void SetGceCreds(GceCreds creds)
    {
        ArgumentNullException.ThrowIfNull(creds);
        SaveRaw(GceProviderId, Write(w =>
        {
            w.WriteString("type", "gce");
            w.WriteString("projectId", creds.ProjectId);
            w.WriteString("accessToken", creds.AccessToken);
        }));
    }

    /// <summary>Reads the Azure service-principal credential, or null when missing or incomplete.</summary>
    public AzureCreds? GetAzureCreds()
    {
        var raw = LoadAllRaw();
        if (!raw.TryGetValue(AzureProviderId, out var json) || string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            string tenantId = StringProp(doc, "tenantId");
            string clientId = StringProp(doc, "clientId");
            string clientSecret = StringProp(doc, "clientSecret");
            string subscriptionId = StringProp(doc, "subscriptionId");
            if (string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(clientId)
                || string.IsNullOrWhiteSpace(clientSecret) || string.IsNullOrWhiteSpace(subscriptionId)) return null;
            return new AzureCreds(tenantId, clientId, clientSecret, subscriptionId);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Upserts the Azure service-principal credential.</summary>
    public void SetAzureCreds(AzureCreds creds)
    {
        ArgumentNullException.ThrowIfNull(creds);
        SaveRaw(AzureProviderId, Write(w =>
        {
            w.WriteString("type", "azure");
            w.WriteString("tenantId", creds.TenantId);
            w.WriteString("clientId", creds.ClientId);
            w.WriteString("clientSecret", creds.ClientSecret);
            w.WriteString("subscriptionId", creds.SubscriptionId);
        }));
    }

    /// <summary>Reads the Fly.io credential, or null when missing or incomplete.</summary>
    public FlyCreds? GetFlyCreds()
    {
        var raw = LoadAllRaw();
        if (!raw.TryGetValue(FlyProviderId, out var json) || string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            string apiToken = StringProp(doc, "apiKey");
            string orgSlug = StringProp(doc, "secret");
            if (string.IsNullOrWhiteSpace(apiToken) || string.IsNullOrWhiteSpace(orgSlug)) return null;
            return new FlyCreds(apiToken, orgSlug);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Upserts the Fly.io credential.</summary>
    public void SetFlyCreds(FlyCreds creds)
    {
        ArgumentNullException.ThrowIfNull(creds);
        SaveRaw(FlyProviderId, Write(w =>
        {
            w.WriteString("type", "fly");
            w.WriteString("apiKey", creds.ApiToken);
            w.WriteString("secret", creds.OrgSlug);
        }));
    }

    /// <summary>Reads the Edgegap credential, or null when missing or incomplete.</summary>
    public EdgegapCreds? GetEdgegapCreds()
    {
        var raw = LoadAllRaw();
        if (!raw.TryGetValue(EdgegapProviderId, out var json) || string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            string token = StringProp(doc, "apiKey");
            string appName = StringProp(doc, "appName");
            if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(appName)) return null;
            return new EdgegapCreds(token, appName);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Upserts the Edgegap credential.</summary>
    public void SetEdgegapCreds(EdgegapCreds creds)
    {
        ArgumentNullException.ThrowIfNull(creds);
        SaveRaw(EdgegapProviderId, Write(w =>
        {
            w.WriteString("type", "edgegap");
            w.WriteString("apiKey", creds.ApiToken);
            w.WriteString("appName", creds.AppName);
        }));
    }

    /// <summary>Reads the Gameye credential, or null when missing or incomplete.</summary>
    public GameyeCreds? GetGameyeCreds()
    {
        var raw = LoadAllRaw();
        if (!raw.TryGetValue(GameyeProviderId, out var json) || string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            string apiKey = StringProp(doc, "apiKey");
            string imageName = StringProp(doc, "imageName");
            string location = StringProp(doc, "location");
            if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(imageName) || string.IsNullOrWhiteSpace(location)) return null;
            return new GameyeCreds(apiKey, imageName, location);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Upserts the Gameye credential.</summary>
    public void SetGameyeCreds(GameyeCreds creds)
    {
        ArgumentNullException.ThrowIfNull(creds);
        SaveRaw(GameyeProviderId, Write(w =>
        {
            w.WriteString("type", "gameye");
            w.WriteString("apiKey", creds.ApiKey);
            w.WriteString("imageName", creds.ImageName);
            w.WriteString("location", creds.Location);
        }));
    }

    /// <summary>Reads the OCI API signing key credential, or null when missing or incomplete.</summary>
    public OciCreds? GetOciCreds()
    {
        var raw = LoadAllRaw();
        if (!raw.TryGetValue(OciProviderId, out var json) || string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            string tenancyOcid = StringProp(doc, "tenancyOcid");
            string userOcid = StringProp(doc, "userOcid");
            string fingerprint = StringProp(doc, "fingerprint");
            string privateKeyPath = StringProp(doc, "privateKeyPath");
            string region = StringProp(doc, "region");
            string compartmentOcid = StringProp(doc, "compartmentOcid");
            if (string.IsNullOrWhiteSpace(tenancyOcid) || string.IsNullOrWhiteSpace(userOcid) || string.IsNullOrWhiteSpace(fingerprint)
                || string.IsNullOrWhiteSpace(privateKeyPath) || string.IsNullOrWhiteSpace(region) || string.IsNullOrWhiteSpace(compartmentOcid))
            {
                return null;
            }

            return new OciCreds(tenancyOcid, userOcid, fingerprint, privateKeyPath, region, compartmentOcid);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Upserts the OCI API signing key credential.</summary>
    public void SetOciCreds(OciCreds creds)
    {
        ArgumentNullException.ThrowIfNull(creds);
        SaveRaw(OciProviderId, Write(w =>
        {
            w.WriteString("type", "oci");
            w.WriteString("tenancyOcid", creds.TenancyOcid);
            w.WriteString("userOcid", creds.UserOcid);
            w.WriteString("fingerprint", creds.Fingerprint);
            w.WriteString("privateKeyPath", creds.PrivateKeyPath);
            w.WriteString("region", creds.Region);
            w.WriteString("compartmentOcid", creds.CompartmentOcid);
        }));
    }

    /// <summary>
    /// Reads the free-form launch options stored for <paramref name="profileId"/>/<paramref name="serverName"/>
    /// (<c>sshUser</c>/<c>sshKeyPath</c>/<c>sshPublicKeyPath</c>, plus whichever provider-specific keys
    /// that provisioner's class doc lists -- <c>amiId</c>/<c>keyName</c> for AwsEc2,
    /// <c>resourceGroup</c>/<c>subnetId</c> for AzureVm, and so on), or null when nothing is stored.
    /// These become a cloud <see cref="Provisioning.ProviderConnection.ProviderOptions"/> verbatim;
    /// <see cref="Composition.HostingComposer"/> is the only reader.
    /// </summary>
    public IReadOnlyDictionary<string, string>? GetCloudVmOptions(string profileId, string serverName)
    {
        var raw = LoadAllRaw();
        if (!raw.TryGetValue(CloudVmOptionsProviderId(profileId, serverName), out var json) || string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            var options = new Dictionary<string, string>();
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                if (property.Name == "type") continue;
                if (property.Value.ValueKind == JsonValueKind.String)
                {
                    options[property.Name] = property.Value.GetString() ?? "";
                }
            }

            return options;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Replaces the launch options stored for <paramref name="profileId"/>/<paramref name="serverName"/>.</summary>
    public void SetCloudVmOptions(string profileId, string serverName, IReadOnlyDictionary<string, string> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        SaveRaw(CloudVmOptionsProviderId(profileId, serverName), Write(w =>
        {
            w.WriteString("type", "cloudvmoptions");
            foreach (var (key, value) in options)
            {
                w.WriteString(key, value);
            }
        }));
    }

    /// <summary>Reads the local-hosting state for <paramref name="serverName"/>, or null when missing or incomplete.</summary>
    public LocalServerState? GetLocalState(string serverName)
    {
        var raw = LoadAllRaw();
        if (!raw.TryGetValue(LocalStateProviderId(serverName), out var json) || string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            string packageDirectory = StringProp(doc, "packageDirectory");
            string composeFileName = StringProp(doc, "composeFileName");
            string protocol = StringProp(doc, "protocol");
            if (string.IsNullOrWhiteSpace(packageDirectory) || string.IsNullOrWhiteSpace(composeFileName)) return null;
            int port = doc.RootElement.TryGetProperty("port", out var p) && p.ValueKind == JsonValueKind.Number ? p.GetInt32() : 0;
            bool upnpMapped = doc.RootElement.TryGetProperty("upnpMapped", out var u) && u.ValueKind == JsonValueKind.True;
            return new LocalServerState(packageDirectory, composeFileName, port, string.IsNullOrWhiteSpace(protocol) ? "udp" : protocol, upnpMapped);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Upserts the local-hosting state for <paramref name="serverName"/>.</summary>
    public void SetLocalState(string serverName, LocalServerState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        SaveRaw(LocalStateProviderId(serverName), Write(w =>
        {
            w.WriteString("type", "localstate");
            w.WriteString("packageDirectory", state.PackageDirectory);
            w.WriteString("composeFileName", state.ComposeFileName);
            w.WriteNumber("port", state.Port);
            w.WriteString("protocol", state.Protocol);
            w.WriteBoolean("upnpMapped", state.UpnpMapped);
        }));
    }

    /// <summary>
    /// Clears the local-hosting state for <paramref name="serverName"/> (after a successful
    /// <c>mp-host local down</c>). Writes an empty object rather than deleting the entry --
    /// <see cref="GetLocalState"/> treats a blank <c>packageDirectory</c> the same as "missing".
    /// </summary>
    public void ClearLocalState(string serverName) => SaveRaw(LocalStateProviderId(serverName), "{}");

    private static string StringProp(JsonDocument doc, string name) => StringPropOrNull(doc, name) ?? "";

    private static string? StringPropOrNull(JsonDocument doc, string name) =>
        doc.RootElement.ValueKind == JsonValueKind.Object
        && doc.RootElement.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string Write(Action<Utf8JsonWriter> body)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            body(w);
            w.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }
}
