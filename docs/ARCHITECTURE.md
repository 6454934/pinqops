# Architecture

This document describes the components, the deploy lifecycle, and the trust
boundaries of pinqops.

## Goals and constraints

- The production server exposes **no inbound ports** — no 443, no SSH, no Docker
  daemon over TCP. It only makes **outbound** connections.
- A deployment happens **only** when a commit reaches `master` (via merged pull
  request).
- The deploy is **instant** (no polling): it runs the moment CI finishes.
- No long-lived secrets are stored on the server.
- The deploy engine is a small, testable .NET CLI that can grow into a broader
  DevOps toolkit.

## The core idea

A closed server cannot receive a webhook. Instead, an agent on the server holds
an **outbound** connection open to GitHub, and GitHub pushes work down that
connection. That agent is a **GitHub Actions self-hosted runner**. This is the
same pattern hosted platforms (e.g. Vercel) use: the deploy side is already
connected outbound, so nothing needs to be opened inbound.

## Components

### 1. `build` job — GitHub-hosted runner (cloud)

Runs on `ubuntu-latest`. Builds the application image from the repository's
`Dockerfile` and pushes it to GHCR twice: as the moving `:latest` and as an
immutable `sha-<commit>` tag. The production server is never involved in the
build and never receives the source code. The SHA tag is what the deploy pins
and what `pinqops rollback` returns to.

### 2. Self-hosted runner — production server

The official GitHub Actions runner, installed as a **systemd service** by
`pinqops install-runner`. It:

- dials `github.com` over **outbound** HTTPS and keeps the connection open;
- registers with the label `pinqops-prod`;
- receives and executes the `deploy` job when master's workflow runs;
- runs as a user in the `docker` group so it can talk to the host Docker daemon.

### 3. `pinqops` CLI — the deploy engine

A .NET 10 console app distributed as a self-contained single-file binary:

- `pinqops deploy [--tag <tag>]` — pins the tag in the project's `.env`, runs
  the fixed `docker compose pull && up -d`, health-checks the services, records
  the deploy in history, and keeps the newest N `sha-*` images for rollback.
- `pinqops rollback [--to <tag>]` — redeploys a previously deployed tag (the
  last successful one by default). The image is normally still local, so no
  registry credentials are needed.
- `pinqops history` — lists recent deploys (tag, time, result, health).
- `pinqops install-runner` — downloads, registers, and installs the self-hosted
  runner as a systemd service.

The interesting logic lives in `PinqOps.Core` behind small interfaces
(`IProcessRunner`, `IFileDownloader`) so it is unit-tested without Docker or the
network. The CLI (`PinqOps.Cli`) is a thin argument-parsing layer over it.

### 4. Application compose project — production server

A single, fixed `docker-compose.yml` (e.g. `/opt/pinqops/docker-compose.yml`)
whose service references `ghcr.io/<owner>/<repo>:${PINQOPS_TAG:-latest}`. The
`PINQOPS_TAG` variable lives in the project's `.env` and is written only by
`pinqops deploy`/`rollback`; without a `.env` the reference falls back to
`:latest`, which preserves the pre-0.5 behavior.

### 5. Server-side state — `.pinqops/` next to the compose file

Both the CLI (running on the runner) and the dashboard already know the compose
file path, so shared state derives from it:

```
/opt/pinqops/
├── docker-compose.yml
├── .env                    # PINQOPS_TAG=<deployed tag> + user app env (0600)
└── .pinqops/
    ├── history.json        # deploy history, newest first, capped (0600)
    └── notify.json         # notification channels + event toggles (0600)
```

### 6. Optional dashboard extras

- **Notifications** — after every deploy/rollback the outcome fans out to the
  enabled channels (generic webhook, Slack-compatible webhook, Telegram bot),
  best-effort with a per-channel timeout; a notification failure never fails a
  deploy.
- **Catalog credentials** — catalog apps install with generated passwords
  (stored 0600 in `~/.config/pinqops/app-credentials.json`, retrievable from
  the dashboard); a reinstall reuses the stored password so data in surviving
  volumes keeps working.

## Deploy lifecycle

```
Pull Request
   │  (review + merge — direct pushes to master are blocked)
   ▼
push: master ──► GitHub Actions
                    │
                    ├─ build job (GitHub-hosted, cloud)
                    │     docker build .
                    │     push ghcr.io/<owner>/<repo>:latest ─────► GHCR (private)
                    │
                    └─ deploy job (assigned to the self-hosted runner
                       over its OUTBOUND link — no inbound port used)
                             │  runs ON the production server:
                             │  docker login ghcr.io (per-job GITHUB_TOKEN)
                             │  pinqops deploy --compose-file <fixed> --tag sha-<commit>
                             │    → pin PINQOPS_TAG=sha-<commit> in <dir>/.env
                             │    → docker compose -f <fixed> pull  ◄── pulls from GHCR (443 outbound)
                             │    → docker compose -f <fixed> up -d
                             │    → health check (compose ps until running/healthy, default 60s)
                             │    → record deploy in .pinqops/history.json + send notifications
                             │    → image retention (keep latest + newest N sha-* images)
                             ▼
                       new container running (verified)
```

A failed health check marks the deploy `failed` (CI shows red) and fires a
notification — there is **no automatic rollback**. Rolling back is an explicit
action: `pinqops rollback` on the server or the Roll back button in the
dashboard's deploy history. Because retention keeps recent SHA images local,
rollback needs no registry credentials.

## Trust boundaries

| Boundary | What crosses it | Control |
|---|---|---|
| GitHub → server | The deploy job, over the runner's outbound link | Only `push: master` triggers it; PRs never run on the runner |
| pinqops → Docker daemon | Fixed compose commands | Discrete argument lists; no repo checkout; `docker` group access |
| Server → GHCR | Image pull (443 outbound) | Per-job `GITHUB_TOKEN` with `packages: read` |
| Server → GitHub | Runner control channel (443 outbound) | Runner registration (short-lived token at install) |

### The `docker` group / daemon boundary

The runner user is in the `docker` group, which is **root-equivalent** on the
host — the same practical trust level Watchtower/Portainer require. Mitigations:

- `pinqops deploy` never checks out or executes repository content on the server;
  it runs only the fixed compose commands.
- Keep the repository private.

The generated workflow's trigger is **not** one of the mitigations: the runner is
registered to the repository and executes any job from any ref whose `runs-on`
matches its labels, so a branch carrying its own workflow reaches it without
touching the default branch. Gate fork PRs under **Settings → Actions → General**
and treat push access to the repository as shell access to the server.

See [`../SECURITY.md`](../SECURITY.md) for the full threat model.

## Design decisions

- **Self-hosted runner over webhook/polling** — a webhook needs an inbound port;
  polling adds latency. A runner keeps the server fully closed *and* deploys
  instantly.
- **Build in the cloud, deploy on the runner** — the production server never
  builds and never receives the source; it only pulls a ready image.
- **Per-job `GITHUB_TOKEN` for GHCR** — no long-lived registry secret on the
  server.
- **SHA tags + `:latest`** — every build pushes an immutable `sha-<commit>` tag
  alongside `:latest`. The deploy pins the SHA in the project's `.env`
  (`${PINQOPS_TAG:-latest}` interpolation), which is what makes deploy history
  and rollback possible while staying backward compatible.
- **Manual rollback only** — a failed deploy or health check never auto-reverts;
  the operator decides. Retention keeps the newest N (default 5) SHA images
  local so a rollback is instant and credential-free.
- **State next to the compose file** — `.pinqops/` derives from the one path the
  CLI and the dashboard already share, so history and notification config work
  even when the runner user and the dashboard user differ.
- **.NET CLI with a testable core** — the team is a .NET shop; the deploy engine
  and installer are one small binary designed to grow into a wider DevOps toolkit.
