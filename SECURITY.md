# Security policy

## Reporting a vulnerability

Please report security issues **privately**. Do not open a public issue for a
suspected vulnerability.

- Preferred: use GitHub's **Report a vulnerability** (Security → Advisories) to
  open a private advisory.
- Alternative: email the maintainers (see the repository owner's profile).

Please include a description, reproduction steps, and impact. We aim to
acknowledge reports within a few business days.

## Threat model

pinqops deploys to a server that exposes **no inbound ports**. The server only
makes outbound connections, which removes the entire class of inbound attacks.

### Assets

- The host Docker daemon (reachable by the runner via the `docker` group).
- The GitHub repository and its workflow definition.

Note there is **no long-lived deploy secret on the server**: the runner
registration token is short-lived, and registry auth uses the per-job
`GITHUB_TOKEN`.

### Controls

| Threat | Control |
|---|---|
| Inbound network attack | The server listens on nothing; only outbound connections exist |
| Deploying from an untrusted event | The generated workflow triggers only on a push to the default branch — but this is **not** a boundary: a repo-scoped runner runs any matching job from any ref. Gate fork PRs in Settings → Actions, and treat push access as server access |
| Untrusted code executing on the runner | `pinqops deploy` does not check out or run repository content; it runs only the fixed compose commands |
| Command injection | Command arguments are built as discrete list items (never a shell string); the compose path is fixed server-side |
| Direct push to `master` | Branch protection requires a reviewed pull request |
| Registry credential leakage | No stored registry secret; `GITHUB_TOKEN` is ephemeral and scoped to `packages: read` on deploy |
| Over-privileged build token | `packages: write` only on the cloud `build` job; `deploy` gets `packages: read` |

### The self-hosted runner trade-off

The runner user is in the `docker` group, which is **root-equivalent** on the
host. This is inherent to running Docker deploys. It is bounded by:

- **No repo checkout on deploy.** `pinqops deploy` runs only the fixed commands.
- **Keep the repository private.** Public repositories increase the risk of
  someone crafting a workflow/event that targets your runner.

> **The deploy workflow's trigger is not a security control.** pinqops generates
> it with `on: push` to your default branch, but the runner is registered to the
> whole *repository*: it will execute **any** job, from **any** ref, whose
> `runs-on` matches its labels. A branch carrying its own workflow file reaches
> the runner without ever touching the default branch, so branch protection does
> not gate it either.
>
> The controls that do apply are GitHub's, and you must set them yourself:
> **Settings → Actions → General** → require approval for **all** outside
> collaborators' fork PRs, and treat anyone with push access to the repository as
> having code execution on the server. Do not give push access more widely than
> you would give a shell account.

If you need stronger isolation, run the runner in an ephemeral/container mode or
restrict the host Docker API with a socket proxy. These are out of scope for the
base project but compatible with it.

## The web UI (`pinqops-ui`)

The optional dashboard is the one component that **does** listen on a port
(default `7467`), which is why it is optional. If you run it, its built-in
controls are:

| Threat | Control |
|---|---|
| First-visitor claims an unconfigured dashboard | Creating the password requires a one-time **setup code** printed only on the server console |
| Password brute force | Per-client lockout (5 failures → 15 min) + per-client request rate limit + slow failure responses. Behind a reverse proxy, set [`--trusted-proxy`](docs/CONFIGURATION.md#--trusted-proxy) or both collapse into one shared bucket and a single attacker locks out every user |
| Weak password storage | PBKDF2-SHA256, 600k iterations, per-password salt, constant-time compare; legacy hashes upgrade on login |
| Weak passwords | Minimum 12 characters, with a deny-list for the guesses credential-stuffing opens with. Length rather than composition rules, which mostly produce `Password1!` |
| Session theft/abuse | 256-bit random bearer tokens, 24h sliding expiry with a **7-day hard ceiling**, per-user session cap; **all sessions revoked on password change**, that user's on a role change |
| Stale API tokens | Tokens can carry an expiry (90 days by default in the UI); expired ones stop validating and are listed as expired |
| XSS / script injection | Strict CSP — only the page's own inline script (pinned by SHA-256 hash) can execute; all rendered values are HTML-escaped |
| Clickjacking / embedding | `frame-ancestors 'none'` + `X-Frame-Options: DENY` |
| CSRF | Auth is a Bearer header (never a cookie), so cross-site requests carry no credentials |
| Token/PAT leakage | PAT and generated app passwords **encrypted at rest** (AES-256-GCM) in `0600` files, sent only in Authorization headers, returned to the UI only masked. The token is sent only to `github.com` unless a GHES host is named in `PINQOPS_GITHUB_HOST` |
| Reading secrets through the API | Reads that return a secret — database dumps, app credentials, runner logs, `docker inspect` — are **admin-only**. `inspect` masks environment values below admin. Per-container reads are also gated by the container's owner |
| Command injection | Fixed `docker` argument lists; container ids/actions/policies validated against strict allowlists; exec runs an argv list (no host shell) and create builds a constrained argv (named volumes only — no host bind mounts, `--privileged`, `--cap-add`, `--device` or host namespaces) |
| Privileged container management (exec, remove, commit, rename, create) | **Admin-only.** A deployer may operate containers it owns (start/stop/restart/kill/pause) and read their logs, nothing more |
| Privilege escalation to host root | The wizard steps that reach root — `install-runner` (which runs `sudo ./svc.sh`) and `create-dockerfile` (whose content the pipeline builds and runs) — are admin-only, as is a backup restore |
| Unnoticed abuse | Append-only audit log records every mutation, **every read that returns a secret, and every denied request** (including failed logins) with the client address. Entries are hash-chained; **Verify chain** on the Audit page detects an edit after the fact |
| Catalog apps exposed on install | Published ports bind `127.0.0.1`; exposing one on all interfaces is an explicit admin choice. Apps that cannot authenticate say so before you install them |
| Oversized/hostile requests | Request bodies capped at 64 KB; process calls time-bounded |
| Internal detail in error responses | Unhandled failures return a correlation id; the detail goes to the log |
| Plain-HTTP interception | Optional TLS via `--cert <pfx>` (HSTS enabled), with the password readable from a file rather than the command line; or bind `--host 127.0.0.1` and reach it through a tunnel. Serving plain HTTP on a network-reachable address warns loudly at startup |
| Tampered self-update | `pinqops update` verifies the download against the release's `SHA256SUMS` and refuses to swap the binary if it cannot. Releases also publish a keyless cosign signature over that manifest, and an SBOM |

### What the audit log's hash chain does and does not prove

Each entry carries a SHA-256 over its own content and the previous entry's hash,
so editing, removing or inserting a line breaks the chain from that point on.
That detects tampering **with the file**. It is not proof against a compromised
pinqops process, which holds the whole chain and could rewrite it end to end.
Shipping entries off the host is the only defence against that.

### What encryption at rest does and does not buy

The key lives in `~/.config/pinqops/secret.key` beside the data it protects, so
it is **no defence against someone who can already read that directory as the
dashboard user** — the process decrypts unattended, so anything it can do, so
can they. What it stops is a config file leaking on its own: copied into a
backup or a support bundle, synced somewhere, pasted into an issue, or left on a
stale disk. Set `PINQOPS_MASTER_PASSPHRASE` to derive the key instead of storing
it if you need real protection at rest.

The dashboard still opens one inbound port on an otherwise closed server —
firewall it to trusted addresses, keep TLS on if it crosses a network you do
not own, or simply do not run it.

## Hardening checklist

- [ ] Branch protection blocks direct pushes to `master`.
- [ ] The repository is private.
- [ ] The runner runs as a non-root user that is in the `docker` group.
- [ ] The server has **no** inbound ports open (verify with your firewall/host).
- [ ] Outbound access is limited to what's needed (`github.com`, `ghcr.io`).
- [ ] The runner label in the deploy workflow matches the installed runner.
- [ ] If `pinqops-ui` runs: its port is firewalled to trusted addresses, and it
      serves TLS (`--cert`) or binds `127.0.0.1` behind a tunnel.
- [ ] If it runs behind a reverse proxy: `--trusted-proxy` names that proxy, so
      the login lockout sees real clients instead of one shared bucket.
- [ ] `~/.config/pinqops/secret.key` is backed up with the config files (without
      it, a restored token and the app passwords cannot be decrypted).
- [ ] Dashboard users have the lowest role that works — `viewer` cannot read
      secrets, `deployer` cannot exec into containers or reach host root.
- [ ] API tokens carry an expiry unless something genuinely unattended needs
      otherwise.
- [ ] Catalog apps stay on loopback unless they are meant to be public, and any
      marked "no authentication" are never exposed.

## Supported versions

This project follows a rolling model — only the latest `master` is supported.
Fixes are released as new commits/tags; there is no long-term maintenance branch.
