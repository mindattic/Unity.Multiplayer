using Unity.Multiplayer.Hosting.Cli.CloudInit;

namespace Unity.Multiplayer.Hosting.Cli.Tests;

[TestFixture]
public class CloudInitRendererTests
{
    [Test]
    public void RenderCompose_references_the_pushed_image_not_a_local_build()
    {
        string compose = CloudInitRenderer.RenderCompose(
            TestManifests.WithHealthPort(), "ghcr.io/mindattic/game:v1", "ghcr.io/mindattic/rendezvous:latest",
            new Dictionary<string, string> { ["MINDATTIC_RENDEZVOUS_SECRET"] = "s3cr3t" });

        Assert.That(compose, Does.Contain("image: ghcr.io/mindattic/game:v1"));
        Assert.That(compose, Does.Not.Contain("build: ."));
        Assert.That(compose, Does.Contain("image: ghcr.io/mindattic/rendezvous:latest"));
    }

    [Test]
    public void RenderCompose_mounts_saves_and_logs_under_the_packages_own_container_workdir()
    {
        // The pulled image is built straight from a server package whose own Dockerfile (written by
        // the Unity Editor's Build Server Package step) sets WORKDIR /srv/game, so a volume mounted
        // anywhere else leaves the game writing into the container's throwaway layer instead.
        string compose = CloudInitRenderer.RenderCompose(
            TestManifests.WithHealthPort(), "img", "rzv", new Dictionary<string, string>());
        Assert.That(compose, Does.Contain("saves:/srv/game/Saves"));
        Assert.That(compose, Does.Contain("logs:/srv/game/Logs"));
        Assert.That(compose, Does.Not.Contain("/app/"));
    }

    [Test]
    public void RenderCompose_binds_the_health_port_to_localhost_only()
    {
        string compose = CloudInitRenderer.RenderCompose(
            TestManifests.WithHealthPort(), "img", "rzv", new Dictionary<string, string>());
        Assert.That(compose, Does.Contain("127.0.0.1:8080:8080/tcp"));
        Assert.That(compose, Does.Contain("\"7777:7777/udp\""));
    }

    [Test]
    public void RenderCompose_writes_every_environment_variable()
    {
        string compose = CloudInitRenderer.RenderCompose(
            TestManifests.WithHealthPort(), "img", "rzv",
            new Dictionary<string, string> { ["MINDATTIC_PUBLIC_ADDRESS"] = "203.0.113.5" });
        Assert.That(compose, Does.Contain("MINDATTIC_PUBLIC_ADDRESS"));
        Assert.That(compose, Does.Contain("203.0.113.5"));
    }

    [Test]
    public void RenderCompose_escapes_a_literal_dollar_sign_so_compose_does_not_interpolate_it()
    {
        // Docker Compose itself interpolates $VAR/${VAR} in every string value it parses; a secret or
        // password containing a literal '$' must be doubled ('$$') or Compose silently replaces it.
        string compose = CloudInitRenderer.RenderCompose(
            TestManifests.WithHealthPort(), "img", "rzv",
            new Dictionary<string, string>
            {
                ["MINDATTIC_SERVER_PASSWORD"] = "P@ss$123",
                ["MINDATTIC_RENDEZVOUS_SECRET"] = "s3cr$t",
            });
        Assert.That(compose, Does.Contain("P@ss$$123"));
        Assert.That(compose, Does.Contain("s3cr$$t"));
        Assert.That(compose, Does.Not.Contain("P@ss$123"));
    }

    [Test]
    public void RenderBootstrapScript_logs_in_pulls_and_starts_then_removes_the_token()
    {
        string script = CloudInitRenderer.RenderBootstrapScript("ghcr.io", "octocat");
        Assert.That(script, Does.Contain("docker login ghcr.io --username octocat --password-stdin"));
        Assert.That(script, Does.Contain("docker compose -f docker-compose.yml pull"));
        Assert.That(script, Does.Contain("docker compose -f docker-compose.yml up -d"));
        Assert.That(script, Does.Contain("rm -f /tmp/mp-host-ghcr-token"));
    }

    [Test]
    public void RenderUserData_embeds_the_ssh_key_compose_file_token_and_bootstrap_script()
    {
        string userData = CloudInitRenderer.RenderUserData(
            TestManifests.WithHealthPort(), "ghcr.io/mindattic/game:v1", "ghcr.io/mindattic/rendezvous:latest",
            new Dictionary<string, string> { ["MINDATTIC_RENDEZVOUS_SECRET"] = "s3cr3t" },
            "ghcr.io", "octocat", "ghp_token", "ssh-ed25519 AAAA... me@laptop");

        Assert.That(userData, Does.StartWith("#cloud-config\n"));
        Assert.That(userData, Does.Contain("ssh_authorized_keys:"));
        Assert.That(userData, Does.Contain("ssh-ed25519 AAAA... me@laptop"));
        Assert.That(userData, Does.Contain("path: /root/docker-compose.yml"));
        Assert.That(userData, Does.Contain("path: /tmp/mp-host-ghcr-token"));
        Assert.That(userData, Does.Contain("path: /root/mp-host-bootstrap.sh"));
        Assert.That(userData, Does.Contain("ghcr.io/mindattic/game:v1"));
        Assert.That(userData, Does.Contain("ghp_token"));
        Assert.That(userData, Does.Contain("runcmd:"));
        Assert.That(userData, Does.Contain("mp-host-bootstrap.sh"));
    }

    [Test]
    public void RenderUserData_trims_the_public_key_so_a_trailing_newline_from_a_read_file_does_not_break_the_yaml()
    {
        string userData = CloudInitRenderer.RenderUserData(
            TestManifests.WithoutHealthPort(), "img", "rzv", new Dictionary<string, string>(),
            "ghcr.io", "octocat", "token", "ssh-ed25519 AAAA...\n");
        Assert.That(userData, Does.Contain("  - ssh-ed25519 AAAA...\n"));
        Assert.That(userData, Does.Not.Contain("ssh-ed25519 AAAA...\n\n"));
    }

    [Test]
    public void RenderCloudConfig_is_a_cloud_config_that_writes_compose_token_and_bootstrap_into_the_ssh_users_home()
    {
        string compose = CloudInitRenderer.RenderCompose(TestManifests.WithHealthPort(), "img", "rzv", new Dictionary<string, string>());
        string bootstrap = CloudInitRenderer.RenderBootstrapScript("ghcr.io", "octocat");
        string cloudConfig = CloudInitRenderer.RenderCloudConfig("ubuntu", compose, bootstrap, "ghp_token");

        Assert.That(cloudConfig, Does.StartWith("#cloud-config\n"));
        Assert.That(cloudConfig, Does.Contain("path: /home/ubuntu/docker-compose.yml"));
        Assert.That(cloudConfig, Does.Contain("path: /tmp/mp-host-ghcr-token"));
        Assert.That(cloudConfig, Does.Contain("path: /home/ubuntu/mp-host-bootstrap.sh"));
        Assert.That(cloudConfig, Does.Contain("cd /home/ubuntu && ./mp-host-bootstrap.sh"));
    }

    [Test]
    public void RenderCloudConfig_base64_encodes_every_files_content()
    {
        string cloudConfig = CloudInitRenderer.RenderCloudConfig("ubuntu", "compose-body", "bootstrap-body", "token-body");
        Assert.That(cloudConfig, Does.Contain(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("compose-body"))));
        Assert.That(cloudConfig, Does.Contain(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("token-body"))));
    }

    [Test]
    public void RenderStartupScript_is_a_plain_shell_script_that_writes_the_same_three_files_and_runs_bootstrap()
    {
        string compose = CloudInitRenderer.RenderCompose(TestManifests.WithHealthPort(), "img", "rzv", new Dictionary<string, string>());
        string bootstrap = CloudInitRenderer.RenderBootstrapScript("ghcr.io", "octocat");
        string script = CloudInitRenderer.RenderStartupScript("ubuntu", compose, bootstrap, "ghp_token");

        Assert.That(script, Does.StartWith("#!/bin/sh\n"));
        Assert.That(script, Does.Not.Contain("#cloud-config"));
        Assert.That(script, Does.Contain("cat > /home/ubuntu/docker-compose.yml"));
        Assert.That(script, Does.Contain("cat > /tmp/mp-host-ghcr-token"));
        Assert.That(script, Does.Contain("cat > /home/ubuntu/mp-host-bootstrap.sh"));
        Assert.That(script, Does.Contain("ghp_token"));
        Assert.That(script, Does.Contain("cd /home/ubuntu && ./mp-host-bootstrap.sh"));
    }
}
