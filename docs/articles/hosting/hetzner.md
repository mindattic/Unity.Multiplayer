# Hosting on Hetzner Cloud (Hetzner)

A UM-T26 plain-VPS cloud profile: `mp-host` creates the server itself through Hetzner's REST API
(`https://api.hetzner.cloud/v1`) from an Ubuntu image, with cloud-init bootstrapping the container
plus rendezvous on first boot -- unlike GenericVps, there's no box to rent by hand first.

## What `mp-host deploy` does here

1. Builds (or reads an already-built) server package and its Docker image, then pushes the image to
   GHCR.
2. Registers your SSH public key with Hetzner (`POST /ssh_keys`, reusing an already-registered key
   of the same name) and creates a server (`POST /servers`) from the `ubuntu-24.04` image with a
   `#cloud-config` user-data document: the same `docker-compose.yml` and bootstrap script
   `GenericVpsProvisioner` uploads over SSH, but run automatically by cloud-init on first boot.
3. Creates a Hetzner Cloud Firewall with one rule per declared port (your game port, at its own
   protocol) plus the rendezvous service's TCP port, and applies it to the new server -- a hard
   requirement here, since mp-host created the server's firewall itself, unlike a bring-your-own
   GenericVps box.
4. Polls the server until it has a public IPv4 address, then probes
   `GET http://127.0.0.1:<healthPort>/healthz` over SSH, the same way GenericVps does.
5. Watches `docker compose logs game` over SSH for the self-registration line
   (`[MindAttic.Multiplayer] Join code: ABC-DEF-GHJ`) and prints it.

## Before you deploy

Create a Hetzner API token (Project > Security > API Tokens, Read & Write) and generate a
disposable SSH keypair for mp-host to register with Hetzner and use for status/logs/update calls.
Store both, plus your GHCR token, in the vault -- never in the repo, never on a shared command line:

```
mp-host credentials set-hetzner --token <hcloud API token> \
  --ssh-user root --ssh-key C:\Users\me\.ssh\mp-host-hetzner --ssh-public-key C:\Users\me\.ssh\mp-host-hetzner.pub
mp-host credentials set-ghcr --username <github-username> --token <PAT with write:packages>
```

Then deploy:

```
mp-host deploy --profile Hetzner --name prod-hetzner --package ./build/GenericVps \
  --image myorg/mygame --tag v1 \
  --rendezvous-url http://<assigned-ip>:8080 \
  --public-address <assigned-ip> --region nbg1 --size cpx11
```

`--public-address` has to be filled in after the first deploy prints the assigned IP (or scripted
around it); `--region`/`--size` default to Nuremberg/CPX11 when omitted. Add `--dry-run` first to see
every Hetzner API call mp-host would make, without creating anything or spending money.

## Open ports

| Port | Protocol | Purpose |
|---|---|---|
| your game port | UDP (or TCP for WebSocket) | Players connect here; opened in the Hetzner Cloud Firewall mp-host creates. |
| 8080 | TCP | The rendezvous service: players resolve join codes here; also opened in that firewall. |
| health port | TCP, bound to 127.0.0.1 | `GET /healthz`; not reachable from the internet, and not part of the firewall rule set. |

## Updating and tearing down

`mp-host update --profile Hetzner --name prod-hetzner --server-id <id> --image myorg/mygame --tag v2 ...`
resolves the server's current IP from Hetzner's API, then re-renders the compose file and runs
`docker compose up -d` over SSH in place -- an in-place rolling restart, the same mechanism
GenericVps uses once a box exists. `mp-host destroy ... --yes` deletes the server outright
(`DELETE /servers/{id}`) -- unlike GenericVps, there's no box left over to keep or cancel.

## Cost and sizing notes

CPX11 (2 shared vCPUs, 2 GB RAM) is Hetzner's cheapest shared-vCPU plan and plenty for a small-lobby
real-time game server; CX22 is the next step up if your game needs more headroom. Nuremberg (`nbg1`)
and Falkenstein (`fsn1`) are Hetzner's cheapest EU regions; pick whichever region is closest to your
players, and note Hetzner also has a US location (Ashburn, `ash`) at a similar price point. Hetzner's
traffic allowance is generous (20 TB/month included on most plans at the time this was written) --
verify current pricing, plan names and included traffic against Hetzner's live console before
committing, since Hetzner has renamed and repriced plans before.
