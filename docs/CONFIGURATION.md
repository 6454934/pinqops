# Configuration reference

pinqops is intentionally almost configuration-free. The little server-side
state that exists lives next to the compose file and is written by pinqops
itself. This page lists the knobs that exist.

## Server-side files and permissions

| Path | Written by | Mode | Contents |
|---|---|---|---|
| `<compose-dir>/.env` | `pinqops deploy`/`rollback`, dashboard env editor | 0600 | `PINQOPS_IMAGE`, `PINQOPS_TAG`, `PINQOPS_HOST_PORT`, `PINQOPS_CONTAINER_PORT` + your app env |
| `<compose-dir>/.pinqops/history.json` | deploy engine | 0600 | Deploy history (newest first, capped at 100) |
| `<compose-dir>/.pinqops/notify.json` | dashboard (read by the CLI) | 0600 | Notification channels + event toggles |
| `~/.config/pinqops/ui.json` | dashboard | 0600 | Dashboard password hash, GitHub token (PAT), and the list of connected apps (`apps: [{id, repoUrl, composeFile, runnerDirectory}]`). A pre-multi-app config (single top-level `RepoUrl`) is migrated automatically on first load — the existing app keeps its paths; don't downgrade the binary after adding more apps. |
| `~/.config/pinqops/app-credentials.json` | dashboard | 0600 | Generated catalog app credentials (values encrypted) |
| `~/.config/pinqops/secret.key` | dashboard | 0600 | Encryption key for the stored secrets — back it up with the config, or those secrets are unreadable |
| `/opt/pinqops/proxy/domains.json` | dashboard (read by the runner CLI) | 0600 | Managed-proxy domain routes + ACME settings |
| `/opt/pinqops/proxy/Caddyfile` | dashboard | 0644 | Generated from `domains.json`; mounted read-only into the `pinqops-proxy` container |
| `~/.config/pinqops/backups.json` | dashboard | 0600 | Scheduled backup targets |
| `~/.config/pinqops/alerts.json` | dashboard | 0600 | [Alert rules](wiki/Alerts.md) |
| `~/.config/pinqops/alert-channels.json` | dashboard | 0600 | Where alerts are sent (holds a Telegram bot token) |
| `~/.config/pinqops/alert-state.json` | dashboard | 0600 | Each alert series' current state, so a restart does not re-page |
| `~/.config/pinqops/alert-history.jsonl` | dashboard | 0600 | Alert transitions; 2 MB per file, the live one plus three previous (8 MB at most) |
| `~/.config/pinqops/metrics.jsonl` | dashboard | 0600 | Metric samples for the alert charts — one a minute, 24h per file, the live one plus two previous, so 48-72h is always on hand |
| `/opt/pinqops/backups/<target>/<ts>.<ext>` | dashboard | — | Backup snapshots (pruned to each target's retention) |

## Dashboard (`pinqops-ui`) options

Every flag has an environment-variable equivalent, so it works the same when the
dashboard runs as the systemd service. `install-service` writes the flags you
pass it into the unit's `ExecStart`.

| Flag | Environment variable | Default | What it does |
|---|---|---|---|
| `--port <n>` | `PINQOPS_UI_PORT` | `7467` | Port to listen on |
| `--host <addr>` | `PINQOPS_UI_HOST` | `0.0.0.0` | Address to bind. Use `127.0.0.1` to keep it off the network and reach it through a tunnel |
| `--cert <pfx>` | `PINQOPS_UI_CERT` | — | Serve HTTPS from a PKCS#12 file (enables HSTS) |
| `--cert-password <pw>` | `PINQOPS_UI_CERT_PASSWORD` | — | Password for that file |
| `--trusted-proxy <addr\|cidr,…>` | `PINQOPS_TRUSTED_PROXIES` | — | Reverse-proxy hops whose `X-Forwarded-For` may be believed |

### `--trusted-proxy`

Set this whenever the dashboard sits behind a reverse proxy — including the
Caddy the **Domains** page installs.

Without it, every request appears to come from the proxy's own address, so the
per-client login lockout and the request rate limiter collapse into a single
shared bucket: **one attacker's failed logins lock out every user**, and the
300 req/min ceiling becomes a shared denial-of-service lever.

It is opt-in because trusting `X-Forwarded-For` unconditionally would let any
caller set the header and choose its own throttle bucket. Name only the hops you
control:

```bash
pinqops-ui --trusted-proxy 127.0.0.1            # a proxy on the same host
pinqops-ui --trusted-proxy 10.0.0.0/8,::1       # addresses and CIDR ranges
```

Entries may be separated by commas, spaces or semicolons. An entry that parses
as neither an address nor a CIDR range is logged and ignored — a typo leaves
that hop untrusted rather than stopping the dashboard from starting. When the
list is empty the header is ignored entirely.

### Secrets at rest

The GitHub token in `ui.json` and the generated passwords in
`app-credentials.json` are stored encrypted (AES-256-GCM), keyed by
`~/.config/pinqops/secret.key`. Existing plaintext files are read as-is and
re-written encrypted on the next change, so there is nothing to migrate.

Be clear about what this buys. The key sits beside the data, so it is **no
defence against someone who can already read that directory as the dashboard
user** — the process decrypts unattended, so anything it can do, so can they.
What it stops is a config file leaking *on its own*: copied into a backup or a
support bundle, synced somewhere, pasted into an issue, or left on a stale disk.

For real protection at rest, set `PINQOPS_MASTER_PASSPHRASE`. The key is then
derived from it (PBKDF2-SHA256, 600k iterations) and never stored — the key file
holds only a salt. The cost is that the passphrase has to be supplied on every
start, so the dashboard cannot come back up unattended after a reboot.

> **Back up `secret.key` with the config files.** Restoring `ui.json` without it
> leaves the token and the app passwords undecryptable; the dashboard says so
> rather than failing silently, and you re-enter them.

### GitHub Enterprise

The API host is derived from the connected repository's URL, and every request
carries the stored token. To stop a repository URL from deciding where that
token gets sent, only `github.com` is allowed by default. Name your GHES host
explicitly:

```bash
PINQOPS_GITHUB_HOST=ghe.example.com
```

## Scheduled backups (optional)

The **Backups** page schedules dumps of a database container or a docker volume
(hourly / daily / weekly, with a retention count). A per-minute background
worker runs whatever is due; **Run now** triggers one immediately. Database
dumps use the container's own tools and read the password from the container's
environment (`sh -c` inside the container), so no credential is ever passed on
the command line: `pg_dumpall` for PostgreSQL, `mysqldump` / `mariadb-dump`,
`mongodump`, and `redis-cli SAVE`; volumes are tarred through a throwaway
`alpine` container. Restore overwrites the target in place (a Redis restore
stops the container, swaps the RDB, and starts it). Snapshots can be downloaded
or deleted from the UI. A run refuses to start under 500 MB of free disk.

## Domains & HTTPS (optional reverse proxy)

The **Domains** page installs a managed Caddy container (`pinqops-proxy`) that
publishes ports 80/443 and gives your apps real domains with automatic Let's
Encrypt certificates (HTTP/3 included). It forwards to each app by container
name over the shared `pinqops-apps` network — `reverse_proxy <repo>-app-1:<port>`
— so domain access and the plain `host:port` publish coexist. A DNS preflight
warns when a domain does not yet point at this server, and a staging-CA toggle
lets you validate the setup without hitting Let's Encrypt's rate limits.
Catalog apps (Grafana, etc.) can take domains the same way. The dashboard's own
port (7467) stays direct, so the control plane is reachable even if the proxy is
down.

## The compose project pinqops generates

| | |
|---|---|
| Project name | Your repository name, so containers read `<repo>-app-1` |
| Image | `${PINQOPS_IMAGE}:${PINQOPS_TAG}` — both pinned by `pinqops deploy`, so the image follows the repository even after a rename |
| Published port | `${PINQOPS_HOST_PORT:-8080}:${PINQOPS_CONTAINER_PORT}` — the container side is read from your Dockerfile's `EXPOSE` (`80` when there is none), the host side is the first free port from `8080`. The dashboard's publish wizard shows both up front and lets you override them before (or after) going live |

### Changing the port

`PINQOPS_HOST_PORT` (and `PINQOPS_CONTAINER_PORT`) are ordinary `.env` values:
edit them in the dashboard's **Deployments → Environment variables (.env)**
editor (fold it open at the bottom of the page) and press **Apply** — no YAML
editing, no redeploy. From a shell it is the same file:

```bash
sudo nano /opt/pinqops/.env                                    # PINQOPS_HOST_PORT=81
docker compose -f /opt/pinqops/docker-compose.yml up -d
```

`PINQOPS_IMAGE` and `PINQOPS_TAG` are rejected by the editor — every deploy
re-pins them, so a manual edit would silently disappear.

A host port that is out of range or **already bound on the server** is rejected
too. That matters because `docker compose up -d` removes the old container
before creating the new one: a port clash would fail the deploy *and* leave the
app stopped. pinqops does not probe the port at deploy time — by then the app's
own container holds it, so every redeploy would look like a conflict.

## Runner label

The `deploy` job targets the runner with:

```yaml
runs-on: [self-hosted, pinqops-prod]
```

The label `pinqops-prod` is assigned when you install the runner
(`pinqops install-runner --labels pinqops-prod`, the default). If you change the
label, change it in **both** places.

## Application compose path

The `deploy` job passes this path to `pinqops deploy`:

```yaml
APP_COMPOSE_PATH: ${{ vars.APP_COMPOSE_PATH || '/opt/pinqops/docker-compose.yml' }}
```

- Default: `/opt/pinqops/docker-compose.yml`.
- To override, set a **repository variable** `APP_COMPOSE_PATH`
  (Settings → Secrets and variables → Actions → **Variables**).

`pinqops deploy` also reads `APP_COMPOSE_PATH` from its environment when
`--compose-file` is not given.

## Image reference

The application compose file references the image the pipeline builds:

```yaml
services:
  app:
    image: ghcr.io/<owner>/<repo>:${PINQOPS_TAG:-latest}
```

Every build pushes both `:latest` and an immutable `sha-<commit>` tag. The
deploy job passes `--tag sha-<commit>`, which pins `PINQOPS_TAG` in the compose
directory's `.env` — that is what enables deploy history and
`pinqops rollback`. Without a `.env` (or with a plain `:latest` reference) the
old moving-tag behavior applies unchanged.

**Migrating from ≤0.4:** change the `image:` line to the interpolated form
above; nothing else is required. Until you do, deploys keep working but history
records tag `latest` and rollback is refused with a clear error.

## GHCR package visibility

The image is private. No token is stored on the server — the `deploy` job
authenticates with the per-job `GITHUB_TOKEN` (granted `packages: read`).

A `GITHUB_TOKEN` has no intrinsic access to a package: it can read one only
because the package is **connected to the repository**. The generated workflow
establishes that connection with the `org.opencontainers.image.source` label on
the built image. Do not rely on it happening implicitly — in particular,
**renaming a repository does not rename its packages**. A push under the new name
creates a *new* package whose connection is independent of the old one, which is
the usual way a deploy ends up able to push but not pull.

### `403 Forbidden` pulling your own image

`docker login` printing `Login Succeeded` only proves the token is valid; GHCR
checks package access later. `Login Succeeded` followed by `403` on a manifest
request means **authenticated but not authorized** — the package is not readable
by this repository. Check the connection:

```bash
gh api /user/packages/container/<package> --jq '{visibility, repo: .repository.full_name}'
```

If `repo` is `null` or an old name, open the package → **Package settings** →
**Manage Actions access** → **Add repository** → pick the repository, role
**Write** — then re-run the failed job. The image does not need rebuilding.

## `pinqops deploy` options

```
pinqops deploy [--compose-file <path>] [--tag <image-tag>] [--no-prune]
               [--timeout-seconds <n>] [--health-timeout-seconds <n>] [--keep-images <n>]
```

| Option | Default | Purpose |
|---|---|---|
| `--compose-file` | `$APP_COMPOSE_PATH` or `/opt/pinqops/docker-compose.yml` | The fixed compose project to deploy |
| `--tag` | — | Image tag to pin as `PINQOPS_TAG` in the project's `.env` (CI passes `sha-<commit>`) |
| `--no-prune` | prune enabled | Skip image cleanup after a successful update |
| `--timeout-seconds` | `300` | Maximum time for the whole deploy |
| `--health-timeout-seconds` | `60` | Wait for services to be running/healthy after `up -d`; `0` skips the check |
| `--keep-images` | `5` | How many recent `sha-*` images to keep locally for rollback |

## `pinqops rollback` / `pinqops history`

```
pinqops rollback [--to <tag>] [--compose-file <path>] [--health-timeout-seconds <n>]
pinqops history  [--compose-file <path>] [--json]
```

`rollback` defaults to the last successful tag before the current one (from
deploy history) and skips the registry pull when the image is still local —
which it is, within the retention window. If the image is gone, the pull needs
a `docker login ghcr.io` with a token that has `read:packages`. There is no
automatic rollback: a failed health check marks the deploy failed and notifies,
and the revert is always an explicit operator action.

## Notifications (`.pinqops/notify.json`)

Managed from the dashboard (Settings → Notifications) and read by the CLI on
every deploy/rollback. Channels: generic webhook (full JSON payload), Slack
incoming webhook (also Discord `/slack` and Mattermost), Telegram bot
(token + chat id). Per-event toggles: deploy succeeded / deploy failed /
health check failed / rolled back. Delivery is best-effort with a 5s
per-channel timeout and never affects the deploy result.

## `pinqops install-runner` options

```
pinqops install-runner --repo-url <url> --token <token> [options]
```

| Option | Required | Default | Purpose |
|---|---|---|---|
| `--repo-url` | ✅ | — | `https://github.com/<owner>/<repo>` (or env `REPO_URL`) |
| `--token` | ✅ | — | Short-lived registration token (or env `RUNNER_TOKEN`) |
| `--labels` | — | `pinqops-prod` | Must match `runs-on` in the deploy workflow |
| `--name` | — | `<hostname>-pinqops` | Display name on GitHub |
| `--version` | — | `2.319.1` | Runner release to download |
| `--dir` | — | `/opt/actions-runner` | Install directory |
| `--user` | — | current user | User the systemd service runs as |

## `pinqops setup` options

```
pinqops setup --repo-url <url> [options]
```

| Option | Default | Purpose |
|---|---|---|
| `--repo-url` | — (prompted) | `https://github.com/<owner>/<repo>` (or env `REPO_URL`) |
| `--pat` | — | GitHub PAT to mint a registration token via the API (or env `GITHUB_PAT`) |
| `--token` | — | A registration token you already have (or env `RUNNER_TOKEN`) |
| `--compose-file` | `/opt/pinqops/docker-compose.yml` | App compose path to reference (or env `APP_COMPOSE_PATH`) |
| `--no-gh` | gh enabled | Don't use the `gh` CLI to mint a token |
| `--skip-preflight` | preflight on | Skip the docker/compose/tar/systemd check |
| `--non-interactive` | auto if stdin redirected | Never prompt; fail if an input is missing |
| `--labels` / `--name` / `--version` / `--dir` / `--user` | as `install-runner` | Pass-throughs to the runner install |

The token fallback chain is: `--token` → authenticated `gh` CLI → `--pat` via the
GitHub API → a pasted token. The PAT is used once and never stored. See
[TOKENS.md](TOKENS.md).

## Workflow permissions

Set per job in the deploy workflow template
[`../examples/workflows/deploy.yml`](../examples/workflows/deploy.yml):

| Job | `contents` | `packages` |
|---|---|---|
| `build` (cloud) | read | write |
| `deploy` (runner) | read | read |
