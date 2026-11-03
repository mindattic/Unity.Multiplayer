# Hosting on OVHcloud (OvhCloud)

A UM-T26 plain-VPS cloud profile: `mp-host` creates a Public Cloud instance itself through OVH's REST
API (`https://<endpoint>.api.ovh.com/1.0/cloud/project/<serviceName>`) from an Ubuntu image, with
cloud-init bootstrapping the container plus rendezvous on first boot -- unlike GenericVps, there's no
box to rent by hand first. OVH is the odd one out among the UM-T26 providers in two ways: its API
uses a signature-based auth scheme instead of a bearer token, and its network model is OpenStack
security groups rather than a flat per-instance firewall list.

## What `mp-host deploy` does here

1. Builds (or reads an already-built) server package and its Docker image, then pushes the image to
   GHCR.
2. Registers your SSH public key with your OVH Public Cloud project (`POST .../sshkey`, reusing an
   already-registered key of the same name) and creates an instance (`POST .../instance`) from Ubuntu
   with a `#cloud-config` user-data document: the same `docker-compose.yml` and bootstrap script
   `GenericVpsProvisioner` uploads over SSH, but run automatically by cloud-init on first boot.
3. Creates an OVH security group in the instance's region with one ingress rule per declared port
   (your game port, at its own protocol) plus the rendezvous service's TCP port, source
   `0.0.0.0/0`, and attaches it to the new instance -- a hard requirement here, since mp-host created
   the instance's networking itself, unlike a bring-your-own GenericVps box.
4. Polls the instance until OVH assigns it a public IP address, then probes
   `GET http://127.0.0.1:<healthPort>/healthz` over SSH, the same way GenericVps does.
5. Watches `docker compose logs game` over SSH for the self-registration line
   (`[MindAttic.Multiplayer] Join code: ABC-DEF-GHJ`) and prints it.

Every call OVH's client makes is signed per-request
(`X-Ovh-Signature = "$1$" + sha1(appSecret+"+"+consumerKey+"+"+method+"+"+url+"+"+body+"+"+timestamp)`)
rather than carrying a static bearer header -- this is OVH's documented scheme, not something
`mp-host` invented, but it does mean the application secret and consumer key never travel as a
simple header the way every other UM-T26 provider's token does.

## Before you deploy

Create an OVH API application (`https://eu.api.ovh.com/createApp/`, or the matching `.ca`/`.us`
console for your account's region) to get an application key and secret, then request a consumer key
scoped to your Public Cloud project (`GET`/`POST`/`PUT`/`DELETE` on `/cloud/project/<serviceName>/*`).
Note your project id (OVH calls it `serviceName`, visible in the Public Cloud console URL) and your
API endpoint (`ovh-eu`, `ovh-us`, `ovh-ca`, ...). Generate a disposable SSH keypair for mp-host to
register with OVH and use for status/logs/update calls. Store all of it, plus your GHCR token, in the
vault -- never in the repo, never on a shared command line:

```
mp-host credentials set-ovh --endpoint ovh-eu --service-name <public cloud project id> \
  --app-key <application key> --app-secret <application secret> --consumer-key <consumer key> \
  --ssh-user root --ssh-key C:\Users\me\.ssh\mp-host-ovh --ssh-public-key C:\Users\me\.ssh\mp-host-ovh.pub
mp-host credentials set-ghcr --username <github-username> --token <PAT with write:packages>
```

Then deploy:

```
mp-host deploy --profile OvhCloud --name prod-ovh --package ./build/GenericVps \
  --image myorg/mygame --tag v1 \
  --rendezvous-url http://<assigned-ip>:8080 \
  --public-address <assigned-ip> --region GRA --size d2-2
```

`--public-address` has to be filled in after the first deploy prints the assigned IP (or scripted
around it); `--region`/`--size` default to Gravelines, France (`GRA`)/`d2-2` ("Discovery" tier) when
omitted. Add `--dry-run` first to see every OVH API call mp-host would make, without creating
anything or spending money. OVH's Public Cloud instance/image/flavor identifiers vary per project and
region (`imageId` in particular is a UUID, not a human name) -- resolve the real ones for your project
(`GET .../image`, `GET .../flavor`) before your first real deploy; `mp-host deploy --dry-run` prints
the exact values it would have sent so you can sanity-check them against what your project actually
has.

## DDoS protection

OVHcloud includes always-on anti-DDoS mitigation on every Public Cloud instance at no extra cost and
no opt-in required -- this is one of OVH's long-standing differentiators versus Hetzner/Vultr/
DigitalOcean/Linode, where meaningful DDoS protection is typically a paid add-on or a
bring-your-own-proxy (Cloudflare Spectrum and similar) setup. For a UDP game server this matters more
than for a typical web app: UDP floods are a common game-server attack and OVH's mitigation sits in
front of the instance without you having to configure anything beyond the security group `mp-host`
already opens. It's still worth sizing your expectations correctly -- always-on volumetric mitigation
protects availability, not integrity; keep your own application-level cheat/flood detection
regardless of host.

## Open ports

| Port | Protocol | Purpose |
|---|---|---|
| your game port | UDP (or TCP for WebSocket) | Players connect here; opened in the OVH security group mp-host creates. |
| 8080 | TCP | The rendezvous service: players resolve join codes here; also opened in that security group. |
| health port | TCP, bound to 127.0.0.1 | `GET /healthz`; not reachable from the internet, and not part of the security group's rule set. |

## Updating and tearing down

`mp-host update --profile OvhCloud --name prod-ovh --server-id <id> --image myorg/mygame --tag v2 ...`
resolves the instance's current IP from OVH's API, then re-renders the compose file and runs
`docker compose up -d` over SSH in place -- an in-place rolling restart, the same mechanism
GenericVps uses once a box exists. `mp-host destroy ... --yes` deletes the instance outright
(`DELETE .../instance/{id}`) -- unlike GenericVps, there's no box left over to keep or cancel.

## Cost and sizing notes

`d2-2` (OVH's cheapest "Discovery" Public Cloud flavor, 1 vCPU/2 GB RAM) is suitable for a
small-lobby real-time game server; step up to a `d2-4` or a "General Purpose" flavor if your game
needs more headroom or steadier CPU. Gravelines (`GRA`) and Strasbourg (`SBG`) are OVH's cheapest EU
regions; pick whichever region (and whichever of OVH's `ovh-eu`/`ovh-us`/`ovh-ca` endpoints owns your
project) is closest to your players. Verify current flavor ids, image ids and pricing against your
own OVH Public Cloud project console before committing, since both are project/region-scoped and
OVH's security-group API is the most OpenStack-shaped (and most likely to have drifted from this
guide) part of the integration -- the opt-in real-provider smoke test
(`tests/Hosting.Cli.Tests/Smoke/OvhSmokeTests.cs`) is what actually guards it against that drift.
