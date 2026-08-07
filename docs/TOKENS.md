# Tokens & authentication

Short version: **for normal deploys you need no git token on the server.** The
only token `pinqops setup` needs is a short-lived *runner registration token*,
and it can get one for you three different ways.

- [Why deploys need no git token](#why-deploys-need-no-git-token)
- [How `pinqops setup` gets a registration token](#how-pinqops-setup-gets-a-registration-token)
  - [1. The gh CLI (easiest)](#1-the-gh-cli-easiest)
  - [2. A personal access token (PAT)](#2-a-personal-access-token-pat)
  - [3. Paste a registration token](#3-paste-a-registration-token)
- [Which token is which](#which-token-is-which)

## Why deploys need no git token

The closed server never clones your repository and never checks out source. The
deploy job only pulls a **pre-built image** from GHCR, and it authenticates with
the per-job, ephemeral `GITHUB_TOKEN` that GitHub Actions injects automatically
(scoped `packages: read`). Nothing long-lived is stored on the server. See
[SETUP.md → Private repos & tokens](SETUP.md#private-repos--tokens-which-token-goes-where).

So the PAT below is **not** a git credential. It is only an admin credential used
**once**, during setup, to mint the registration token — and it is never stored.

## How `pinqops setup` gets a registration token

`pinqops setup` walks a fixed fallback chain and stops at the first one that
works. You only get prompted when the automatic paths can't produce a token.

### 1. The gh CLI (easiest)

If the [GitHub CLI](https://cli.github.com) is installed and logged in, setup
mints the token automatically — you type nothing.

```bash
# once, on the server:
gh auth login          # follow the device-code prompts (outbound only)

pinqops setup --repo-url https://github.com/<owner>/<repo>
# -> "authenticated gh CLI detected — minting a registration token automatically"
```

### 2. A personal access token (PAT)

If gh isn't available, setup can mint the token from a PAT via the GitHub API.
You must be a **repo admin**. Create a token with the right scope:

**Classic PAT** (simplest):
1. GitHub → your avatar → **Settings → Developer settings → Personal access
   tokens → Tokens (classic) → Generate new token (classic)**.
2. Give it a short expiry and select the **`repo`** scope.
3. Generate, copy it, and paste it when setup asks (or pass `--pat`).

**Fine-grained PAT** (least privilege): create a token scoped to the one
repository with **Repository permissions → Administration: Read and write**. If
your org enforces SSO, authorize the token for the org.

```bash
pinqops setup --repo-url https://github.com/<owner>/<repo>
# when prompted, paste the PAT (input is hidden). Or, non-interactively:
GITHUB_PAT=<pat> pinqops setup --repo-url https://github.com/<owner>/<repo> --non-interactive
```

> The PAT is used once, sent only in the `Authorization` header, and never
> written to disk or logs. Prefer `$GITHUB_PAT` or the masked prompt over
> `--pat` (a flag can land in your shell history / the process list).

### 3. Paste a registration token

The final fallback needs no PAT and no gh. Grab a token straight from GitHub:

1. Repo → **Settings → Actions → Runners → New self-hosted runner**.
2. Copy the token shown in the `./config.sh --token <TOKEN>` line.
3. Paste it when setup asks (or pass `--token`, or set `RUNNER_TOKEN`).

This token is short-lived (~1 hour), so use it right away.

## Which token is which

| Token | Where it comes from | Lifetime | Who needs it |
|---|---|---|---|
| Per-job `GITHUB_TOKEN` | Injected by GitHub Actions at deploy time | One job | Nobody — it's automatic |
| Runner registration token | gh / a PAT / Settings → Actions → Runners | ~1 hour | `pinqops setup` / `install-runner`, once |
| Personal access token (PAT) | You create it (repo admin) | You choose | Only to *mint* a registration token in setup |
| `read:packages` PAT | You create it | You choose | Optional — only for a manual `docker pull` test (see [SETUP.md](SETUP.md#private-repos--tokens-which-token-goes-where)) |
