# Hosting on Vultr (Vultr)

A UM-T26 plain-VPS cloud profile: `mp-host` creates the instance itself through Vultr's REST API
(`https://api.vultr.com/v2`) from an Ubuntu image, with cloud-init bootstrapping the container plus
rendezvous on first boot -- unlike GenericVps, there's no box to rent by hand first.

## What `mp-host deploy` does here

1. Builds (or reads an already-built) server package and its Docker image, then pushes the image to
   GHCR.
2. Registers your SSH public key with Vultr (`POST /ssh-keys`, reusing an already-registered key of
   the same name) and creates an instance (`POST /instances`) from Ubuntu 24.04 LTS with a
   `#cloud-config` user-data document (base64-encoded, as Vultr's API requires): the same
   `docker-compose.yml` and bootstrap script `GenericVpsProvisioner` uploads over SSH, but run
   automatically by cloud-init on first boot.
3. Creates a Vultr firewall group with one rule per declared port (your game port, at its own
   protocol) plus the rendezvous service's TCP port, and attaches it to the new instance -- a hard
   requirement here, since mp-host created the instance's firewall itself, unlike a bring-your-own
   GenericVps box.
4. Polls the instance until Vultr assigns it a real public IP (not the placeholder `0.0.0.0` it
   reports while still provisioning), then probes `GET http://127.0.0.1:<healthPort>/healthz` over
   SSH, the same way GenericVps does.
5. Watches `docker compose logs game` over SSH for the self-registration line
   (`[MindAttic.Multiplayer] Join code: ABC-DEF-GHJ`) and prints it.

## Before you deploy

Create a Vultr API key (Account > API, enable it and allow your IP or disable the access-control
list for scripted use) and generate a disposable SSH keypair for mp-host to register with Vultr and
use for status/logs/update calls. Store both, plus your GHCR token, in the vault -- never in the
repo, never on a shared command line:

```
mp-host credentials set-vultr --token <Vultr API key> \
  --ssh-user root --ssh-key C:\Users\me\.ssh\mp-host-vultr --ssh-public-key C:\Users\me\.ssh\mp-host-vultr.pub
mp-host credentials set-ghcr --username <github-username> --token <PAT with write:packages>
```

Then deploy:

```
mp-host deploy --profile Vultr --name prod-vultr --package ./build/GenericVps \
  --image myorg/mygame --tag v1 \
  --rendezvous-url http://<assigned-ip>:8080 \
  --public-address <assigned-ip> --region ewr --size vc2-1c-1gb
```

`--public-address` has to be filled in after the first deploy prints the assigned IP (or scripted
around it); `--region`/`--size` default to New Jersey (`ewr`)/`vc2-1c-1gb` when omitted. Add
`--dry-run` first to see every Vultr API call mp-host would make, without creating anything or
spending money.

## Open ports

| Port | Protocol | Purpose |
|---|---|---|
| your game port | UDP (or TCP for WebSocket) | Players connect here; opened in the Vultr firewall group mp-host creates. |
| 8080 | TCP | The rendezvous service: players resolve join codes here; also opened in that firewall group. |
| health port | TCP, bound to 127.0.0.1 | `GET /healthz`; not reachable from the internet, and not part of the firewall rule set. |

## Updating and tearing down

`mp-host update --profile Vultr --name prod-vultr --server-id <id> --image myorg/mygame --tag v2 ...`
resolves the instance's current IP from Vultr's API, then re-renders the compose file and runs
`docker compose up -d` over SSH in place -- an in-place rolling restart, the same mechanism
GenericVps uses once a box exists. `mp-host destroy ... --yes` deletes the instance outright
(`DELETE /instances/{id}`) -- unlike GenericVps, there's no box left over to keep or cancel.

## Cost and sizing notes

`vc2-1c-1gb` (1 shared vCPU, 1 GB RAM) is Vultr's cheapest Cloud Compute plan and suitable for a
small-lobby real-time game server; step up to a `vc2-1c-2gb` or `vhf` (high-frequency) plan if your
game needs more headroom or steadier CPU. New Jersey (`ewr`) is one of Vultr's cheapest/most-central
US regions; pick whichever region is closest to your players -- Vultr has a wide regional spread,
which is one of its advantages over the other UM-T26 providers. Verify current plan ids, pricing and
included bandwidth against Vultr's live console (`GET /plans`) before committing, since the
`os_id`/`plan` identifiers used here may drift.
