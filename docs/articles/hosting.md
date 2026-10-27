# Hosting

Every dedicated-server option in Unity.Multiplayer runs the same provider-neutral server package
(UM-T23: a self-contained Linux build with `server.json`, declared ports, `Logs/`, `Saves/`, clean
SIGTERM shutdown and a health endpoint) through the same `mp-host` pipeline (UM-T25: build -> push
to GHCR -> provision -> boot the container(s) -> open ports -> health check -> register with
rendezvous -> print join code). Choosing a host later never changes game code; it only changes which
`IHostingProfile`/`IProvisioner` `mp-host` uses. This page is the map of that hosting table: how to
pick a path, what's common to all of them (ports, secrets, TLS, updates), and where each provider's
own page covers its specifics. See
[RFC 0001 § Hosting](https://github.com/mindattic/Unity.Multiplayer/blob/main/docs/rfc/0001-architecture.md#hosting)
for the design this table implements.

## Deploy any game with one command (`mp-host game`)

A game built on Unity.Multiplayer needs no deploy script and no build method of its own (UM-T58). Install
`mp-host` once as a .NET tool, then point it at the Unity project:

```powershell
# once per machine, from a Unity.Multiplayer checkout: packs into C:\LocalNuGet and installs mp-host on PATH
powershell -File tools/install-mp-host.ps1
# (equivalent: dotnet pack src/Hosting.Cli -c Release -o C:\LocalNuGet
#              dotnet tool install -g MindAttic.Unity.Multiplayer.Hosting.Cli --add-source C:\LocalNuGet)

cd <your Unity project>
mp-host game local                       # build the server, run it + rendezvous on this PC (Docker)
mp-host game down                        # stop it
mp-host game deploy --dry-run            # see every step of a hosted deploy without doing any of it
mp-host game deploy                      # GenericVps (default): build, push to GHCR, deploy, print the join code
mp-host game deploy --profile RunPod --rendezvous-url http://<vps>:8080
mp-host game status | logs | update | destroy --yes   # the profile this project last deployed with
```

What it works out from the project, so nothing is per-game:

| Value | Default | Override |
|---|---|---|
| Project | the current folder | `--project <dir>` |
| Unity editor | the exact `ProjectSettings/ProjectVersion.txt` version, from `UNITY_HUB_EDITORS`, Unity Hub's editor list and install location, then `…/Unity/Hub/Editor/<version>` | `--unity <exe>` or `UNITY_EDITOR` |
| Server scene | the first enabled scene in Build Settings | `"scene"` in `mp-host.json`, or `--scene Assets/…/Main.unity` |
| Server name | `productName`, slugged (`Unity.Cursory` → `unity-cursory`); `<name>-local` for Local | `--name` |
| Image | `ghcr.io/<ghcr user>/<name>:latest` | `--image owner/repo`, `--tag` |
| Public address | GenericVps: the stored SSH target's host; RunPod: cosmetic | `--public-address` |
| Rendezvous | hosted: `http://<public address>:8080` beside the game; Local: a rendezvous container, built from the Unity.Multiplayer checkout the project's `Packages/manifest.json` points at by `file:` (else `ghcr.io/mindattic/rendezvous`) | `--rendezvous-url`, `--rendezvous-image`, `--no-rendezvous` |
| Package | built into `Builds/ServerPackage/<profile>` (log beside it) | `--skip-build` reuses it, `--package <dir>` uses another |
| Profile | `game deploy`: `"profile"` in `mp-host.json`, else GenericVps. `game status`, `logs`, `update`, `destroy`: the profile this project last deployed with from this machine (`game local` counts as Local), else the same as deploy | `--profile` |
| Server id | remembered from the last `game deploy` to that profile on this machine | `--server-id` |

The build is the library's own batch entry point,
`Unity -batchmode -executeMethod Unity.Multiplayer.Editor.ServerPackageBuild.BuildFromCommandLine -serverScene <scene> …`:
it opens the scene first, so `server.json` carries that scene's whole NetBootstrap (UM-T50) and the scene boots first.
`--profile` takes every profile below (`Local` is the same as `game local`); `--server-password`, `--env KEY=VALUE`,
`--region`, `--size`, `--development` and `--upnp` pass through. On Windows with Docker Desktop in Windows-containers
mode, `game` uses Docker Desktop's Linux engine for that run.

An optional **`mp-host.json`** at the project root sets defaults for any option (a flag always wins), e.g.
`{ "name": "cursory", "image": "mindattic/unity-cursory" }`. The repo's own `run.ps1`, if it keeps one for the
`/deploy` router, is then just `mp-host game deploy --project $PSScriptRoot @args`.

`mp-host game smoke` builds the same project as one Linux player and runs it as a dedicated server, the rendezvous
service and N bots under Docker Compose, until every bot's driver passes: see [Bots and end-to-end smoke tests](bots.md).

`mp-host game` resolves everything above and then runs the lower-level commands this page describes
(`mp-host deploy`, `local up`, …), printing each one, so those remain available for CI or a package built elsewhere.

## Which path do you need?

```
Do you want one always-on dedicated server you rent/manage yourself?
├─ Yes, and it can have a UDP-reachable port
│   └─ GenericVps profile (the default) -- any UDP-capable Linux box with Docker + SSH.
│      Hetzner, Vultr, DigitalOcean, Linode/Akamai, OVHcloud, AWS EC2/Lightsail, GCE, Azure VM,
│      Fly.io and Oracle Cloud's free tier all provision one of these for you; "bring your own box"
│      (a VPS you already rented, or your own home PC) uses the same profile directly.
│
├─ Yes, but the only host you want only exposes TCP/HTTP (RunPod)
│   └─ RunPod profile -- WebSocket transport over a RunPod-mapped TCP port, registered with
│      rendezvous the same way. See "The RunPod path" below.
│
├─ No -- I want a server spun up per match, on demand, and torn down after
│   └─ Edgegap, Gameye or AWS GameLift -- an `IMatchAllocator` requests a server for a match and
│      gets back an endpoint + join code; there's no `mp-host deploy`/`update` box to manage.
│
└─ No host at all -- just my own PC / a LAN party
    └─ Local profile (`mp-host local up`/`down`) -- runs the package on this machine with Docker
       Desktop, no GHCR push, no SSH, no cloud API, no credential to store. See "The no-port-forwarding
       path" below for reaching players off the LAN.
```

### The default: GenericVps-shaped dedicated servers

[GenericVps](hosting/genericvps.md) is the first profile built and the one every other
"always-on box" profile is shaped after: the server image runs beside the `mindattic/rendezvous`
container on any UDP-capable Linux VPS, and `mp-host` configures it over SSH -- no cloud API
involved, because you treat the box as already provisioned. Point `mp-host deploy --profile
GenericVps` at a box you rented from anywhere (Hetzner, Vultr, DigitalOcean, a home server with a
forwarded port) and it works the same way.

Every other cloud profile in the table automates the "rent the box" step on top of that same shape
(create the VM/instance/droplet through the provider's API, open a firewall rule for the game port
and rendezvous, boot via cloud-init/startup-script running the same compose stack):

| Provider | Profile | Page |
|---|---|---|
| Hetzner Cloud | `Hetzner` | [hosting/hetzner.md](hosting/hetzner.md) |
| Vultr | `Vultr` | [hosting/vultr.md](hosting/vultr.md) |
| DigitalOcean | `DigitalOcean` | [hosting/digitalocean.md](hosting/digitalocean.md) |
| Linode / Akamai | `Linode` | [hosting/linode.md](hosting/linode.md) |
| OVHcloud | `OvhCloud` | [hosting/ovhcloud.md](hosting/ovhcloud.md) |
| AWS EC2 | `AwsEc2` | [hosting/aws-ec2.md](hosting/aws-ec2.md) |
| AWS Lightsail | `AwsLightsail` | [hosting/aws-lightsail.md](hosting/aws-lightsail.md) |
| Google Compute Engine | `Gce` | [hosting/gce.md](hosting/gce.md) |
| Azure VM | `AzureVm` | [hosting/azure-vm.md](hosting/azure-vm.md) |
| Fly.io | `Fly` | [hosting/flyio.md](hosting/flyio.md) |
| Oracle Cloud (Always Free tier) | `Oci` | [hosting/oracle-oci.md](hosting/oracle-oci.md) |
| Your own box, or `mp-host local` | `GenericVps` | [hosting/genericvps.md](hosting/genericvps.md) |

Pick whichever of these is cheapest or closest to your players; the guide page for each one has its
own sizing/egress-cost notes. All of them update with an in-place rolling restart
(`docker compose up -d` with the new image) rather than replacing the box.

### The RunPod path

RunPod is the one profile in the table that is not GenericVps-shaped, because **RunPod pods do not
support UDP.** This was re-verified against RunPod's current docs for this guide (`docs.runpod.io`,
checked 2026-10-21): the "Expose ports" page states plainly that pods don't support UDP connections
and that applications needing it should use TCP-based communication instead -- RunPod exposes a pod
over an HTTP proxy (10-port max, HTTPS only, 100 s idle timeout -- unsuitable for a game connection)
or a Direct TCP port (RunPod assigns the public IP and external port, reported through
`RUNPOD_TCP_PORT_<port>`). Nothing in RunPod's recent changelog (the 2026 API v2 beta, Instant
Cluster, Serverless worker affinity) adds UDP support. If that ever changes, this profile could move
to the plain `Udp` transport like every other profile; until then:

- The server package builds with `UseWebSockets=true` (Unity Transport over WebSockets, a TCP/HTTP
  framing of the same NGO traffic) instead of raw UDP.
- RunPod's `podFindAndDeployOnDemand` mutation declares the game port in the pod's TCP port list;
  RunPod maps it to a public port at creation time, so there's no separate firewall step.
- The pod reads its own `RUNPOD_PUBLIC_IP`/`RUNPOD_TCP_PORT_<port>` and advertises that address when
  it self-registers with rendezvous (UM-T13's `ServerAdvertisement`) -- you never wire the mapping
  through by hand.
- There's no in-place swap: RunPod pods are immutable once created, so `mp-host update` terminates
  the old pod and creates a new one (players on the old pod are dropped). Plan updates for a quiet
  period, or run two pods behind your own matchmaking if that's not acceptable.

See [hosting/runpod.md](hosting/runpod.md) for the full deploy/update/teardown walkthrough
and cost notes. RunPod still needs its own rendezvous service reachable from the internet (a
GenericVps box, or a shared one your studio runs) since the pod itself runs only the game server, no
side-car.

### Per-match allocation, instead of one always-on box

Three profiles don't run a server that stays up at all; they request one through an
`IMatchAllocator` when a match starts and release it when the match ends:

- [Edgegap](hosting/edgegap.md) -- deploys a container per match, close to the players in
  it, through Edgegap's deployment API.
- [Gameye](hosting/gameye.md) -- starts a session per match in a location you pick, through
  Gameye's session API.
- [AWS GameLift](hosting/gamelift.md) -- the odd one out: a fleet is provisioned once (like
  GenericVps) but game/player sessions are allocated per match on top of it (like Edgegap/Gameye).

Pick one of these instead of the always-on profiles above when your game runs short matches rather
than a persistent world -- there's no box to size, update or leave running between matches.

### The no-port-forwarding path

If you have nowhere to rent a box and no port forwarding at home (an HOA-controlled network is RFC
0001's documented case for this), you still have two options that need no forwarded port at all:

1. **Development and LAN play:** localhost, LAN-by-IP, Multiplayer Play Mode and Unity Relay all work
   behind NAT with nothing to configure.
2. **A listen server reachable off the LAN:** use **Unity Relay** as the join-code provider
   (`JoinCodeProvider.Relay`, the default `Auto` choice for a `Host` topology) instead of the
   self-hosted rendezvous service -- Relay gets through NAT; rendezvous maps codes to addresses but
   cannot. Relay needs the package and a Unity Cloud project link, set up in a few minutes by the New Game wizard's
   step 3 or `tools/link-unity-cloud.ps1` (see [Unity Relay for a home host](join-codes.md#unity-relay-for-a-home-host-no-port-forwarding)).
   `mp-host local up` (the `Local` profile) also makes a best-effort UPnP port-mapping
   attempt for players connecting by IP, and reports plainly when the router refuses it (the expected
   outcome on a managed/HOA network) rather than treating that as an error.

Internet play by IP (not Relay) still needs a dedicated server on a rented host from the table above
-- see [hosting/local.md](hosting/local.md) for the full UPnP story.

## Ports

Every profile opens the same three kinds of port; only the game port's protocol and who opens the
firewall rule differ:

| Port | Protocol | Purpose | Who opens it |
|---|---|---|---|
| your game port | UDP (GenericVps-shaped profiles, GameLift, Edgegap, Gameye) or TCP via WebSockets (RunPod) | Players connect here. | `mp-host` (a provider firewall rule/security group, or `ufw` best-effort on a box you already own) |
| 8080 | TCP | The rendezvous service: dedicated servers self-register here; players resolve join codes here. | `mp-host`, alongside the game port, wherever rendezvous runs |
| health port | TCP, bound to `127.0.0.1` | `GET /healthz`. Never reachable from the internet by design -- `mp-host` polls it over SSH (GenericVps-shaped) or loopback (Local), never through a public firewall rule. | N/A -- intentionally not opened |

Per-match profiles (Edgegap, Gameye, GameLift) manage the game port through their own allocation API
instead of a firewall rule `mp-host` owns directly; see each profile's page for specifics.

## Secrets

Nothing credential-shaped is ever written to the repo or passed on a shared command line. Every
profile's own key/token/password lives in `MindAttic.Vault` behind `mp-host credentials set-<thing>`,
and `mp-host deploy`/`update`/`destroy` read it back by name:

| What | Command |
|---|---|
| GHCR push access | `mp-host credentials set-ghcr --username <user> --token <PAT with write:packages>` |
| An SSH key for a box you already own | `mp-host credentials set-ssh --name <server> --host <h> --user <u> --key <path>` |
| A cloud provider's API token/key | `mp-host credentials set-<hetzner\|vultr\|digitalocean\|linode\|ovh\|aws\|gce\|azure\|fly\|oci\|runpod>` (plus, for the provisioned-VM profiles, an SSH keypair alongside the token) |
| Per-server launch options (AMI, key pair, resource group, subnet, ...) | `mp-host credentials set-cloud-options --profile <AwsEc2\|AwsLightsail\|Gce\|AzureVm\|Fly\|GameLift> --name <server> --opt KEY=VALUE ...` |
| A per-match allocator's API credential | `mp-host credentials set-edgegap` / `set-gameye` |
| The rendezvous registration secret | `mp-host credentials set-rendezvous [--secret <s>]` (without `--secret` it generates a random one). `mp-host deploy`/`update`/`local up` read it from the vault -- generating and storing one on first use when none is stored -- and inject it into both the game server (`MINDATTIC_RENDEZVOUS_SECRET`) and its rendezvous container (`Rendezvous__RegistrationSecret`). `--rendezvous-secret <s>` on a single command overrides it, e.g. to match a rendezvous service you run elsewhere |

`--dry-run` (every `mp-host` command accepts it) prints the exact API call, SSH command or compose
file it would send, so you can check a deploy before it touches a credential-backed resource or
spends provider credit. A dry run needs no stored GHCR credential or SSH target: when one is
missing it warns and prints `<dry-run placeholder ...>` values in its place, so a game's
`run.ps1 -DryRun` works on a fresh machine (the big-cloud launch options above still have to be
stored, since they name real local key files).

## TLS

The rendezvous service (`server/Rendezvous`) serves plain HTTP on port 8080 by default --
`RendezvousOptions.RequireHttps` is off, and the shared `RegistrationSecret` authenticates who may
register/heartbeat/delete a listing, not the transport. For a rendezvous instance reachable from the
public internet, pick one of:

- **Terminate TLS at a reverse proxy or your cloud provider's load balancer** in front of port 8080,
  and turn on `RendezvousOptions.UseForwardedHeaders` with the proxy's address in `KnownProxies` so
  rendezvous trusts its `X-Forwarded-For`/`X-Forwarded-Proto` headers (this is the normal shape for a
  box that already runs behind something like Caddy/nginx for its own certificate renewal).
- **Serve TLS directly from Kestrel** (a certificate you provide) and set `RequireHttps` so plain
  HTTP is redirected and HSTS is sent.

Neither is wired up by `mp-host` itself today -- it deploys rendezvous exactly as shown in each
profile's compose/cloud-init template, on plain HTTP port 8080 -- so add whichever of the two fits
your box before relying on a public join-code service for anything beyond testing. The game port
itself has no built-in transport encryption on the `Udp`/`WebSocket` transports either profile
uses; **Unity Relay**, the no-port-forwarding path above, is the one join path in this table that
doesn't need you to add this yourself.

## Updates

How `mp-host update` rolls out a new image differs by profile shape:

| Shape | Profiles | What `update` does |
|---|---|---|
| GenericVps-shaped box | GenericVps and every provisioned-VM profile (Hetzner, Vultr, DigitalOcean, Linode, OVHcloud, AWS EC2/Lightsail, GCE, Azure VM, Fly.io, Oracle OCI) | In-place rolling restart: re-renders the compose file, pulls the new image, `docker compose up -d` on the same box. |
| RunPod | RunPod | Blue/green by necessity: pods are immutable, so `update` terminates the old pod and creates a new one. Players on the old pod are dropped during the swap. |
| Managed fleet | AWS GameLift | A blue/green fleet swap. |
| Per-match allocation | Edgegap, Gameye | Nothing to roll: the next match simply allocates against the newly registered app version/image; there's no running box to update in place. |
| Local | Local | `mp-host local up` again with the new package/image; you control when that happens. |

`mp-host destroy ... --yes` tears the resource down outright (compose stack, pod, VM + its firewall
resource, fleet, or UPnP mapping, depending on profile) -- see each profile's own page for exactly
what it deletes and what, if anything, it leaves for you to cancel yourself (a rented VPS is yours to
keep or cancel; `mp-host` only ever tears down what it created on it).
