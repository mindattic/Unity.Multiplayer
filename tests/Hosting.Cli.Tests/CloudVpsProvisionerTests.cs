using Unity.Multiplayer.Hosting.Cli.Cli;
using Unity.Multiplayer.Hosting.Cli.Provisioning;
using Unity.Multiplayer.Hosting.Cli.Provisioning.CloudVps;
using Unity.Multiplayer.Hosting.Cli.Provisioning.GenericVps;
using Unity.Multiplayer.Hosting.Cli.Tests.Fakes;

namespace Unity.Multiplayer.Hosting.Cli.Tests;

/// <summary>
/// Exercises the shared <see cref="CloudVpsProvisioner"/> logic every UM-T26 profile (Hetzner,
/// Vultr, DigitalOcean, Linode, OVHcloud) reuses -- each provider's own test only needs to cover its
/// <see cref="ICloudVpsApiClient"/> wire format, not this.
/// </summary>
[TestFixture]
public class CloudVpsProvisionerTests
{
    private static readonly CloudVpsProviderSpec Spec = new("TestCloud", "Test Cloud", "test-region", "test-size", "ubuntu-24.04");

    private FakeCloudVpsApiClient _api = null!;
    private FakeSshCommandRunner _ssh = null!;
    private CloudVpsProvisioner _provisioner = null!;
    private RecordingDeployLog _log = null!;
    private ProviderConnection _connection = null!;

    [SetUp]
    public void SetUp()
    {
        _api = new FakeCloudVpsApiClient();
        _ssh = new FakeSshCommandRunner();
        _provisioner = new CloudVpsProvisioner(_api, _ssh, Spec);
        _log = new RecordingDeployLog();
        _connection = new ProviderConnection("prod-cloud", new Dictionary<string, string>
        {
            ["sshUser"] = "deploy",
            ["sshKeyPath"] = "/keys/id_ed25519",
            ["sshPublicKey"] = "ssh-ed25519 AAAA... deploy@laptop",
        }, new Dictionary<string, string>());
    }

    private ProvisionRequest BuildRequest(string? region = null, string? size = null) => new(
        _connection, "ghcr.io/mindattic/game:v1", "ghcr.io", "octocat", "ghp_token",
        TestManifests.WithHealthPort(),
        new Dictionary<string, string> { ["MINDATTIC_RENDEZVOUS_URL"] = "http://rendezvous:8080" },
        region, size);

    [Test]
    public async Task ProvisionAsync_registers_the_ssh_key_then_creates_a_server_with_cloud_init_user_data()
    {
        var result = await _provisioner.ProvisionAsync(BuildRequest(), _log, CancellationToken.None);

        Assert.That(_api.EnsureSshKeyCalls.Single().PublicKey, Is.EqualTo("ssh-ed25519 AAAA... deploy@laptop"));
        var create = _api.CreateCalls.Single();
        Assert.That(create.Region, Is.EqualTo("test-region"));
        Assert.That(create.Size, Is.EqualTo("test-size"));
        Assert.That(create.ImageSlug, Is.EqualTo("ubuntu-24.04"));
        Assert.That(create.SshKeyId, Is.EqualTo("key-1"));
        Assert.That(create.CloudInitUserData, Does.Contain("#cloud-config"));
        Assert.That(create.CloudInitUserData, Does.Contain("ghcr.io/mindattic/game:v1"));
        Assert.That(result.ServerId, Is.EqualTo("server-1"));
        Assert.That(result.PublicAddress, Is.EqualTo("198.51.100.1"));
        Assert.That(result.PortMappings[7777], Is.EqualTo(7777));
    }

    [Test]
    public async Task ProvisionAsync_prefers_an_explicit_region_and_size_over_the_spec_defaults()
    {
        await _provisioner.ProvisionAsync(BuildRequest(region: "fsn1", size: "cpx21"), _log, CancellationToken.None);
        Assert.That(_api.CreateCalls.Single().Region, Is.EqualTo("fsn1"));
        Assert.That(_api.CreateCalls.Single().Size, Is.EqualTo("cpx21"));
    }

    [Test]
    public void ProvisionAsync_throws_when_no_ssh_public_key_is_configured()
    {
        var connection = ProviderConnection.Named("no-key");
        var request = BuildRequest() with { Connection = connection };
        Assert.ThrowsAsync<ArgumentException>(() => _provisioner.ProvisionAsync(request, _log, CancellationToken.None));
    }

    [Test]
    public async Task OpenFirewallAsync_opens_every_non_health_port_plus_the_rendezvous_port()
    {
        await _provisioner.OpenFirewallAsync("server-1", BuildRequest(), _log, CancellationToken.None);

        var (serverId, rules) = _api.FirewallCalls.Single();
        Assert.That(serverId, Is.EqualTo("server-1"));
        Assert.That(rules, Has.Count.EqualTo(2));
        Assert.That(rules, Has.Some.Matches<CloudVpsFirewallRule>(r => r.Port == 7777 && r.Protocol == "udp"));
        Assert.That(rules, Has.Some.Matches<CloudVpsFirewallRule>(r => r.Port == 8080 && r.Protocol == "tcp"));
    }

    [Test]
    public async Task WaitForHealthyAsync_polls_the_server_then_probes_over_ssh_once_it_has_an_ip()
    {
        _api.GetSequence = new Queue<CloudVpsServer>(new[]
        {
            new CloudVpsServer("server-1", null, "initializing"),
            new CloudVpsServer("server-1", "198.51.100.1", "running"),
        });
        _ssh.Responses.Add(("curl", new SshCommandResult(0, "200", "")));

        bool healthy = await _provisioner.WaitForHealthyAsync("server-1", BuildRequest(), TimeSpan.FromSeconds(10), _log, CancellationToken.None);

        Assert.That(healthy, Is.True);
        Assert.That(_api.GetCalls, Has.Count.EqualTo(2));
    }

    [Test]
    public async Task WaitForHealthyAsync_gives_up_after_the_timeout_when_the_server_never_gets_an_ip()
    {
        _api.ServerToCreate = new CloudVpsServer("server-1", null, "initializing");
        bool healthy = await _provisioner.WaitForHealthyAsync("server-1", BuildRequest(), TimeSpan.Zero, _log, CancellationToken.None);
        Assert.That(healthy, Is.False);
        Assert.That(_ssh.RunCalls, Is.Empty);
    }

    [Test]
    public async Task WaitForHealthyAsync_skips_the_probe_when_the_manifest_has_no_health_port()
    {
        var request = BuildRequest() with { Manifest = TestManifests.WithoutHealthPort() };
        Assert.That(await _provisioner.WaitForHealthyAsync("server-1", request, TimeSpan.Zero, _log, CancellationToken.None), Is.True);
        Assert.That(_api.GetCalls, Is.Empty);
    }

    [TestCase("running", ServerState.Running)]
    [TestCase("active", ServerState.Running)]
    [TestCase("off", ServerState.Stopped)]
    [TestCase("deleted", ServerState.Gone)]
    [TestCase("", ServerState.Gone)]
    [TestCase("build", ServerState.Provisioning)]
    [TestCase("weird", ServerState.Unknown)]
    public async Task GetStatusAsync_maps_the_provider_status(string providerStatus, ServerState expected)
    {
        _api.ServerToCreate = new CloudVpsServer("server-1", "198.51.100.1", providerStatus);
        var status = await _provisioner.GetStatusAsync("server-1", _connection, _log, CancellationToken.None);
        Assert.That(status.State, Is.EqualTo(expected));
    }

    [Test]
    public async Task GetLogsAsync_resolves_the_host_from_the_api_then_tails_compose_logs_over_ssh()
    {
        _ssh.Responses.Add(("logs --no-color", new SshCommandResult(0, "[MindAttic.Multiplayer] Join code: ABC-DEF-GHJ\n", "")));
        string logs = await _provisioner.GetLogsAsync("server-1", 50, _connection, _log, CancellationToken.None);
        Assert.That(logs, Does.Contain("ABC-DEF-GHJ"));
        Assert.That(_ssh.RunCalls.Single().Target.Host, Is.EqualTo("198.51.100.1"));
    }

    [Test]
    public void GetLogsAsync_throws_when_the_server_has_no_public_ip_yet()
    {
        _api.ServerToCreate = new CloudVpsServer("server-1", null, "initializing");
        Assert.ThrowsAsync<InvalidOperationException>(() => _provisioner.GetLogsAsync("server-1", 50, _connection, _log, CancellationToken.None));
    }

    [Test]
    public async Task UpdateAsync_re_uploads_compose_and_pulls_then_restarts_over_ssh()
    {
        await _provisioner.UpdateAsync("server-1", BuildRequest(), _log, CancellationToken.None);
        Assert.That(_ssh.UploadCalls.Single().RemotePath, Is.EqualTo("docker-compose.yml"));
        Assert.That(_ssh.RunCalls.Single().Command, Does.Contain("pull").And.Contain("up -d"));
    }

    [Test]
    public void UpdateAsync_throws_when_the_restart_fails()
    {
        _ssh.Responses.Add(("up -d", new SshCommandResult(1, "", "no space left on device")));
        Assert.ThrowsAsync<InvalidOperationException>(() => _provisioner.UpdateAsync("server-1", BuildRequest(), _log, CancellationToken.None));
    }

    [Test]
    public async Task DestroyAsync_destroys_the_server_through_the_api_not_ssh()
    {
        await _provisioner.DestroyAsync("server-1", _connection, _log, CancellationToken.None);
        Assert.That(_api.DestroyCalls, Is.EqualTo(new[] { "server-1" }));
        Assert.That(_ssh.RunCalls, Is.Empty);
    }
}
