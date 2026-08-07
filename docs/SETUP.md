# Setup guide

End-to-end setup, from an empty server to a working `master`-merge deploy — with
**no inbound ports** on the server.

- [The one-command path: `pinqops setup`](#the-one-command-path-pinqops-setup)
- [1. Branch protection](#1-branch-protection)
- [2. Application Dockerfile](#2-application-dockerfile)
- [3. Server: bare-server bootstrap (Docker + a runner user)](#3-server-bare-server-bootstrap-docker--a-runner-user)
- [4. Server: install the pinqops CLI](#4-server-install-the-pinqops-cli)
- [5. Server: install the self-hosted runner](#5-server-install-the-self-hosted-runner)
- [6. Server: the application compose project](#6-server-the-application-compose-project)
- [7. Verify end-to-end](#7-verify-end-to-end)
- [Private repos & tokens: which token goes where](#private-repos--tokens-which-token-goes-where)
- [Troubleshooting](#troubleshooting)

---

## The one-command path: `pinqops setup`

Once the `pinqops` binary is on the server (section 4), a single command does
the rest — it installs Docker Engine + Compose on Ubuntu/Debian when they are
missing, checks the other prerequisites, obtains a runner registration token,
installs and registers the self-hosted runner, and prints the remaining compose
steps:

```bash
pinqops setup --repo-url https://github.com/<owner>/<repo>
```

Answer the prompts and you're done. For the registration token, setup tries the
authenticated `gh` CLI first, then a personal access token you paste, then a
registration token you paste — see [TOKENS.md](TOKENS.md).
`pinqops-ui install-service` bootstraps Docker the same way. On other
distributions, install Docker yourself (section 3.2) first.

Useful flags: `--pat <pat>` / `--token <registration-token>` (skip the prompt),
`--no-gh` (ignore the gh CLI), `--skip-preflight`, `--non-interactive` (for
scripted runs; also reads `REPO_URL` / `GITHUB_PAT` / `RUNNER_TOKEN` /
`APP_COMPOSE_PATH` from the environment), plus the runner pass-throughs
`--labels/--name/--version/--dir/--user`.

The rest of this guide is the **manual, step-by-step path** — do it yourself, or
read it to understand what `pinqops setup` automates (sections 5–6).

## 1. Branch protection

The deploy workflow triggers on `push` to your **default branch** (`main` for
repositories created since 2020; the dashboard wizard reads the real name and
fills it in — if you copy the example workflow by hand, change it yourself).
Make merge the only way to reach it:

1. GitHub → **Settings → Branches → Add branch ruleset** (or "Add rule").
2. Target branch: your default branch.
3. Enable **Require a pull request before merging**.
4. Enable **Block force pushes**.
5. (Recommended) Require at least one approval and require the **CI** check.

With this in place, direct pushes are rejected, so `on: push` effectively means
"a PR was merged".

> This is a change-management control, **not** a security boundary. A self-hosted
> runner is registered to the whole repository and runs any job from any ref
> whose `runs-on` matches its labels, so a branch carrying its own workflow file
> reaches the runner without touching the default branch. See
> [SECURITY.md](../SECURITY.md) for what actually gates that.

## 2. Application Dockerfile

Add a `Dockerfile` at the repository root that builds your application image.
The pipeline is Dockerfile-agnostic — the `build` job just runs `docker build .`.
See [`../examples/app/Dockerfile.example`](../examples/app/Dockerfile.example).

> The dashboard's publish wizard can **generate this for you**: for a repo with
> no Dockerfile it detects the stack and commits an editable starting Dockerfile
> (this manual step is only for a CLI-only setup). For a monorepo, pick the
> project subdirectory in the wizard — it sets the `PINQOPS_BUILD_CONTEXT`
> repository variable so the `build` job builds from there (default: repo root).

No repository secrets are needed: the `build` job pushes to GHCR with the
automatic `GITHUB_TOKEN`.

## 3. Server: bare-server bootstrap (Docker + a runner user)

This section assumes a **freshly provisioned Ubuntu/Debian server with nothing
installed** — a bare SSH box. Run these steps as a normal sudo-capable user.
(For other distributions, follow the equivalent steps from
[docs.docker.com/engine/install](https://docs.docker.com/engine/install/).)

### 3.1 Base tools

```bash
sudo apt-get update
sudo apt-get install -y ca-certificates curl tar
```

`curl` fetches the pinqops binary (step 4), `tar` extracts the runner archive
(step 5), and `ca-certificates` lets the box trust `github.com` / `ghcr.io`.

### 3.2 Docker Engine + the Compose plugin

Install from Docker's **official apt repository** (recommended over distro
packages, which are often outdated and may lack the Compose v2 plugin):

```bash
# Add Docker's official GPG key.
sudo install -m 0755 -d /etc/apt/keyrings
sudo curl -fsSL https://download.docker.com/linux/ubuntu/gpg -o /etc/apt/keyrings/docker.asc
sudo chmod a+r /etc/apt/keyrings/docker.asc

# Add the repository to apt sources.
echo \
  "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.asc] \
  https://download.docker.com/linux/ubuntu $(. /etc/os-release && echo "$VERSION_CODENAME") stable" \
  | sudo tee /etc/apt/sources.list.d/docker.list > /dev/null

sudo apt-get update
sudo apt-get install -y \
  docker-ce docker-ce-cli containerd.io docker-buildx-plugin docker-compose-plugin
```

> **On Debian**, replace both `ubuntu` occurrences above with `debian`.

### 3.3 Enable Docker and verify

```bash
sudo systemctl enable --now docker
docker version              # Docker Engine is installed and running
docker compose version      # the Compose v2 plugin is present (note: `docker compose`, not `docker-compose`)
```

### 3.4 A runner user in the `docker` group

Run the runner as a non-root user, and add it to the `docker` group so it can
reach the daemon:

```bash
sudo usermod -aG docker "$USER"
# Log out/in (or run `newgrp docker`) so the group change takes effect,
# then confirm you can talk to Docker without sudo:
docker ps
```

The `docker` group is **root-equivalent** on the host — see [`../SECURITY.md`](../SECURITY.md).

The server needs **outbound** HTTPS (443) to `github.com` and `ghcr.io`. It does
**not** need any inbound port open.

## 4. Server: install the pinqops CLI

`pinqops` ships as a self-contained single-file binary attached to each GitHub
Release — no .NET runtime is required on the server.

```bash
sudo curl -fsSL -o /usr/local/bin/pinqops \
  https://github.com/pinqponq/pinqops/releases/latest/download/pinqops
sudo chmod +x /usr/local/bin/pinqops
pinqops version
```

(If you prefer to build it yourself, see [CONTRIBUTING.md](../CONTRIBUTING.md).)

## 5. Server: install the self-hosted runner

> `pinqops setup` ([above](#the-one-command-path-pinqops-setup)) does this step
> and step 6 for you, including obtaining the token. The manual steps below are
> the equivalent.

> **Multiple apps on one server:** the dashboard installs one runner per
> connected repository (under `/opt/pinqops/runners/<app>`, each with its own
> systemd unit) because GitHub registration tokens are repo-scoped. If your
> repositories live in one organization, an **org-level runner** labelled
> `pinqops-prod` serves all of them from a single install — register it from
> the org's Settings → Actions → Runners; the per-repo `APP_COMPOSE_PATH`
> variable still routes each deploy to the right compose project. The
> dashboard's runner status card can't verify org runners (it checks the repo),
> so expect it to show "unknown" in that setup.

Get a registration token from **repo → Settings → Actions → Runners → New
self-hosted runner** (the token is short-lived — use it right away), then let
pinqops install and register the runner as a systemd service:

```bash
# Pre-create the install dir owned by your user (config.sh must not run as root).
sudo mkdir -p /opt/actions-runner && sudo chown "$USER" /opt/actions-runner

pinqops install-runner \
  --repo-url https://github.com/<owner>/<repo> \
  --token <registration-token> \
  --user "$USER"
```

`install-runner` downloads the official runner, registers it with the label
`pinqops-prod`, and installs + starts it as a systemd service. Verify:

```bash
sudo /opt/actions-runner/svc.sh status
```

> **On a minimal server**, the runner's `config.sh` may fail with a missing
> native dependency (for example `libicu`), because the GitHub Actions runner is
> a .NET application. If `install-runner` fails during registration, install the
> runner's own dependencies and re-run it:
> ```bash
> sudo /opt/actions-runner/bin/installdependencies.sh
> ```
> (This script ships inside the runner archive, so it only exists after the
> download in this step. Use `--dir` to match if you changed the install
> directory. The pinqops binary itself needs no such dependencies — it is
> published self-contained with invariant globalization.)

The runner should appear as **Idle** under Settings → Actions → Runners.

> The label `pinqops-prod` must match `runs-on: [self-hosted, pinqops-prod]` in
> your app repo's deploy workflow (copied from
> [`../examples/workflows/deploy.yml`](../examples/workflows/deploy.yml)).
> Override it with `--labels` if you change one — but change both.

## 6. Server: the application compose project

Create the single, fixed application project (default path
`/opt/pinqops/docker-compose.yml`):

```bash
sudo mkdir -p /opt/pinqops
sudo cp deploy/app.docker-compose.example.yml /opt/pinqops/docker-compose.yml
sudo nano /opt/pinqops/docker-compose.yml   # set name: <repo>, and the container port
```

Two things in that file matter:

- **`${PINQOPS_IMAGE}:${PINQOPS_TAG}`** — the deploy pins both in `/opt/pinqops/.env`
  (from `--image` and `--tag`), so the image follows the repository even after a
  rename, and each commit's `sha-<...>` tag enables deploy history and
  `pinqops rollback`.
- **`ports:`** — publishing is what makes the app reachable. Set the container
  side to whatever your Dockerfile `EXPOSE`s; the host side defaults to `8080`
  and is changeable later via `PINQOPS_HOST_PORT` in `.env`.

The dashboard's GitHub wizard generates this file for you — including reading the
container port out of your Dockerfile — so this manual step is only for a
CLI-only setup. See [CONFIGURATION.md](CONFIGURATION.md) for the details.

(If you use a different path, set the repository variable `APP_COMPOSE_PATH`
accordingly — Settings → Secrets and variables → Actions → Variables.)

## 7. Verify end-to-end

1. Open a pull request, get it approved, and **merge into your default branch**.
2. Watch **Actions** → the `Build and Deploy` run:
   - `build` (GitHub-hosted) builds and pushes `:latest` + `sha-<commit>`;
   - `deploy` (your self-hosted runner) runs `pinqops deploy --tag sha-<commit>`,
     which also health-checks the services and records the deploy.
3. On the server, confirm the new container is running and the deploy is
   recorded:
   ```bash
   docker compose -f /opt/pinqops/docker-compose.yml ps
   pinqops history
   ```

## Private repos & tokens: which token goes where

A common question when the app repository is **private**: *"Where do I enter a
git token?"* You don't — and here is why.

- **The server never clones your repository.** No source is ever checked out or
  executed on the box. So there is **no git PAT and no SSH key to configure** for
  the deploy. The `deploy` job only runs `pinqops deploy`, which pulls a
  pre-built image and restarts the fixed compose project.
- **The private image is pulled from GHCR with the per-job `GITHUB_TOKEN`.** The
  workflow does `docker login ghcr.io` with the ephemeral, per-run token (scoped
  `packages: read`) and logs out afterwards — see
  [`../examples/workflows/deploy.yml`](../examples/workflows/deploy.yml). Nothing
  long-lived is stored on the server. It works because the package is **connected
  to the repository** — the workflow's `org.opencontainers.image.source` label is
  what establishes that. Note that renaming a repository does *not* rename its
  packages: the new name is a new package with its own connection.
- **The only token you handle on the server is the runner registration token.**
  It is short-lived, obtained from **repo → Settings → Actions → Runners → New
  self-hosted runner**, and passed once to `pinqops install-runner --token` (step
  5). It is not a git credential and is not persisted as a secret.

**Optional — pulling the private image by hand.** If you want to test the image
pull directly on the server (outside the workflow), authenticate with a
**personal access token** that has the `read:packages` scope:

```bash
echo "<your-PAT>" | docker login ghcr.io -u "<your-github-username>" --password-stdin
docker pull ghcr.io/<owner>/<repo>:latest
docker logout ghcr.io
```

This is only a manual debugging aid — the automated deploy never needs it.

## Troubleshooting

- **`docker: command not found` or "Cannot connect to the Docker daemon"** —
  Docker isn't installed or isn't running on the server. Complete the bare-server
  bootstrap in [section 3](#3-server-bare-server-bootstrap-docker--a-runner-user)
  (`sudo systemctl enable --now docker`, then `docker version`).
- **`install-runner` fails during registration with a missing library (e.g.
  `libicu`)** — the GitHub Actions runner needs native dependencies the minimal
  server lacks. Run `sudo /opt/actions-runner/bin/installdependencies.sh` and
  re-run `install-runner` (see [section 5](#5-server-install-the-self-hosted-runner)).
- **"How do I authenticate to a private repo?"** — you don't configure a git
  token; the server never clones the repo and the private image is pulled with
  the per-job `GITHUB_TOKEN`. See
  [Private repos & tokens](#private-repos--tokens-which-token-goes-where).
- **`deploy` job stuck on "Waiting for a runner"** — the runner is offline or the
  label doesn't match. Check `sudo /opt/actions-runner/svc.sh status` and that
  the runner's labels include `pinqops-prod`.
- **`pinqops: command not found`** — the binary isn't on PATH for the runner
  service. Install it to `/usr/local/bin/pinqops`.
- **`permission denied while trying to connect to the Docker daemon`** — the
  runner user isn't in the `docker` group. Run `sudo usermod -aG docker <user>`
  and restart the runner service (`sudo /opt/actions-runner/svc.sh stop && start`).
- **Pull fails with `403 Forbidden` right after a successful build** — the
  package is not readable by `GITHUB_TOKEN`. `Login Succeeded` followed by `403`
  means authenticated but *not authorized*: the package is not connected to this
  repository. Common after a repository rename, because packages are not renamed
  with it. Check with
  `gh api /user/packages/container/<package> --jq .repository.full_name`, then
  package → **Package settings** → **Manage Actions access** → add the repository
  with role **Write**, and re-run the failed job (no rebuild needed). See
  [CONFIGURATION.md](CONFIGURATION.md).
- **`install-runner` fails creating `/opt/actions-runner`** — pre-create it and
  `chown` it to your user (see step 5); `config.sh` must not run as root.
- **Need to remove the runner** — `sudo /opt/actions-runner/svc.sh stop`, then
  `sudo /opt/actions-runner/svc.sh uninstall`, then `./config.sh remove --token
  <removal-token>` from `/opt/actions-runner`.
