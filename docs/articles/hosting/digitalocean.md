# Hosting on DigitalOcean (DigitalOcean)

A UM-T26 plain-VPS cloud profile: `mp-host` creates the droplet itself through DigitalOcean's REST
API (`https://api.digitalocean.com/v2`) from an Ubuntu image, with cloud-init bootstrapping the
container plus rendezvous on first boot -- unlike GenericVps, there's no box to rent by hand first.

## What `mp-host deploy` does here

1. Builds (or reads an already-built) server package and its Docker image, then pushes the image to
   GHCR.
2. Registers your SSH public key with DigitalOcean (`POST /v2/account/keys`, reusing an
   already-registered key of the same name) and creates a droplet (`POST /v2/droplets`) from
   `ubuntu-24-04-x64` with a `#cloud-config` user-data document: the same `docker-compose.yml` and
   bootstrap script `GenericVpsProvisioner` uploads over SSH, but run automatically by cloud-init on
   first boot.
3. Creates a DigitalOcean Cloud Firewall with one inbound rule per declared port (your game port, at
   its own protocol) plus the rendezvous service's TCP port, attached to the new droplet -- a hard
   requirement here, since mp-host created the droplet's firewall itself, unlike a bring-your-own
   GenericVps box.
4. Polls the droplet until DigitalOcean assigns it a public IPv4 address, then probes
   `GET http://127.0.0.1:<healthPort>/healthz` over SSH, the same way GenericVps does.
5. Watches `docker compose logs game` over SSH for the self-registration line
   (`[MindAttic.Multiplayer] Join code: ABC-DEF-GHJ`) and prints it.

## Before you deploy

Create a DigitalOcean API token (API > Tokens, read and write) and generate a disposable SSH keypair
for mp-host to register with DigitalOcean and use for status/logs/update calls. Store both, plus your
GHCR token, in the vault -- never in the repo, never on a shared command line:

```
mp-host credentials set-digitalocean --token <DigitalOcean API token> \
  --ssh-user root --ssh-key C:\Users\me\.ssh\mp-host-do --ssh-public-key C:\Users\me\.ssh\mp-host-do.pub
mp-host credentials set-ghcr --username <github-username> --token <PAT with write:packages>
```

Then deploy:

```
mp-host deploy --profile DigitalOcean --name prod-do --package ./build/GenericVps \
  --image myorg/mygame --tag v1 \
  --rendezvous-url http://<assigned-ip>:8080 \
  --public-address <assigned-ip> --region nyc1 --size s-1vcpu-1gb
```

`--public-address` has to be filled in after the first deploy prints the assigned IP (or scripted
around it); `--region`/`--size` default to New York 1/`s-1vcpu-1gb` when omitted. Add `--dry-run`
first to see every DigitalOcean API call mp-host would make, without creating anything or spending
money.

## Open ports

| Port | Protocol | Purpose |
|---|---|---|
| your game port | UDP (or TCP for WebSocket) | Players connect here; opened in the DigitalOcean Cloud Firewall mp-host creates. |
| 8080 | TCP | The rendezvous service: players resolve join codes here; also opened in that firewall. |
| health port | TCP, bound to 127.0.0.1 | `GET /healthz`; not reachable from the internet, and not part of the firewall rule set. |

## Updating and tearing down

`mp-host update --profile DigitalOcean --name prod-do --server-id <id> --image myorg/mygame --tag v2 ...`
resolves the droplet's current IP from DigitalOcean's API, then re-renders the compose file and runs
`docker compose up -d` over SSH in place -- an in-place rolling restart, the same mechanism
GenericVps uses once a box exists. `mp-host destroy ... --yes` deletes the droplet outright
(`DELETE /v2/droplets/{id}`) -- unlike GenericVps, there's no box left over to keep or cancel.

## Cost and sizing notes

`s-1vcpu-1gb` (the smallest "Basic" droplet, 1 vCPU/1 GB RAM) is DigitalOcean's cheapest regular plan
and suitable for a small-lobby real-time game server; a shared-CPU `s-1vcpu-2gb` is the next step up
if you need more headroom. New York 1 (`nyc1`) is one of DigitalOcean's cheapest/most-central
regions; pick whichever region is closest to your players. DigitalOcean includes a flat outbound
transfer allowance per droplet size rather than metering every byte, which is simple to budget
against -- verify current plan ids, pricing and transfer allowances against DigitalOcean's live
pricing page before committing, since DigitalOcean has renamed and repriced plans before.
