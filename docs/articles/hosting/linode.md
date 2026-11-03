# Hosting on Linode (Akamai Cloud Computing)

A UM-T26 plain-VPS cloud profile: `mp-host` creates the instance itself through Linode's REST API
(`https://api.linode.com/v4`) from an Ubuntu image, with cloud-init (Linode's Metadata service)
bootstrapping the container plus rendezvous on first boot -- unlike GenericVps, there's no box to
rent by hand first.

## What `mp-host deploy` does here

1. Builds (or reads an already-built) server package and its Docker image, then pushes the image to
   GHCR.
2. Creates an instance (`POST /linode/instances`) from `linode/ubuntu24.04` with your SSH public key
   embedded directly as an `authorized_keys` entry -- unlike the other UM-T26 providers, Linode has
   no separate "register a key" resource for instance creation, so `mp-host` skips that round trip --
   and a `#cloud-config` user-data document (base64-encoded into the instance's metadata, which
   requires an image with Metadata service support; Ubuntu 24.04 has it): the same
   `docker-compose.yml` and bootstrap script `GenericVpsProvisioner` uploads over SSH, but run
   automatically by cloud-init on first boot.
3. Creates a Linode Cloud Firewall with one inbound rule per declared port (your game port, at its
   own protocol) plus the rendezvous service's TCP port, attached to the new instance (inbound policy
   `DROP` otherwise) -- a hard requirement here, since mp-host created the instance's firewall
   itself, unlike a bring-your-own GenericVps box.
4. Polls the instance until Linode assigns it a public IPv4 address, then probes
   `GET http://127.0.0.1:<healthPort>/healthz` over SSH, the same way GenericVps does.
5. Watches `docker compose logs game` over SSH for the self-registration line
   (`[MindAttic.Multiplayer] Join code: ABC-DEF-GHJ`) and prints it.

## Before you deploy

Create a Linode personal access token (My Profile > API Tokens, with Linodes and Firewalls read/write
scopes) and generate a disposable SSH keypair for mp-host's `authorized_keys` entry and its
status/logs/update calls. Store both, plus your GHCR token, in the vault -- never in the repo, never
on a shared command line:

```
mp-host credentials set-linode --token <Linode personal access token> \
  --ssh-user root --ssh-key C:\Users\me\.ssh\mp-host-linode --ssh-public-key C:\Users\me\.ssh\mp-host-linode.pub
mp-host credentials set-ghcr --username <github-username> --token <PAT with write:packages>
```

Then deploy:

```
mp-host deploy --profile Linode --name prod-linode --package ./build/GenericVps \
  --image myorg/mygame --tag v1 \
  --rendezvous-url http://<assigned-ip>:8080 \
  --public-address <assigned-ip> --region us-east --size g6-nanode-1
```

`--public-address` has to be filled in after the first deploy prints the assigned IP (or scripted
around it); `--region`/`--size` default to Newark, NJ (`us-east`)/`g6-nanode-1` when omitted. Add
`--dry-run` first to see every Linode API call mp-host would make, without creating anything or
spending money.

## Open ports

| Port | Protocol | Purpose |
|---|---|---|
| your game port | UDP (or TCP for WebSocket) | Players connect here; opened in the Linode Cloud Firewall mp-host creates. |
| 8080 | TCP | The rendezvous service: players resolve join codes here; also opened in that firewall. |
| health port | TCP, bound to 127.0.0.1 | `GET /healthz`; not reachable from the internet, and not part of the firewall rule set. |

## Updating and tearing down

`mp-host update --profile Linode --name prod-linode --server-id <id> --image myorg/mygame --tag v2 ...`
resolves the instance's current IP from Linode's API, then re-renders the compose file and runs
`docker compose up -d` over SSH in place -- an in-place rolling restart, the same mechanism
GenericVps uses once a box exists. `mp-host destroy ... --yes` deletes the instance outright
(`DELETE /linode/instances/{id}`) -- unlike GenericVps, there's no box left over to keep or cancel.

## Cost and sizing notes

`g6-nanode-1` (1 shared vCPU, 1 GB RAM) is Linode's cheapest Shared CPU plan and suitable for a
small-lobby real-time game server; a `g6-standard-1` (2 GB RAM) is the next step up if your game
needs more headroom. Newark, NJ (`us-east`) is one of Linode's cheapest/most-central US regions; pick
whichever region is closest to your players. Linode (now Akamai Cloud Computing) bundles a
transfer-pool allowance shared across your account's instances rather than a per-instance cap, which
is worth knowing if you run more than one server -- verify current plan ids, pricing and transfer
pools against Linode's live pricing page before committing.
