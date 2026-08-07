# Security Model

Short version — the full document is
[SECURITY.md](https://github.com/pinqponq/pinqops/blob/master/SECURITY.md).

## The core idea

The server only makes **outbound** connections: the runner long-polls GitHub,
and Docker pulls from GHCR. No inbound ports, no SSH-based deploys, no git
token stored on the server.

```
GitHub (cloud)  ──build+push──▶  ghcr.io
      ▲                            │
      │ long-poll                  ▼ pull (outbound)
   runner ◀────────────────── your server
```

## Trust boundaries

- Deploys can only originate from a `master` push — protect the branch so
  merge is the only way in.
- The runner executes repo workflows: keep the repo private, review PRs.
- The web UI is the one optional component that listens on a port.

## Web UI controls

Setup-code-gated first run · PBKDF2-SHA256 (600k) passwords, minimum 12
characters · 256-bit bearer sessions with a 7-day ceiling, revoked on password
change · per-client login lockout + rate limit · strict CSP with a hash-pinned
inline script · `frame-ancestors 'none'` · 64 KB body cap · fixed `docker`
argument lists with allowlisted names · PAT and generated app passwords
encrypted at rest in `0600` files, the PAT only ever sent in Authorization
headers and only to `github.com`.

## Roles

| Role | Can |
|---|---|
| `viewer` | Read the dashboard. **Not** secrets: no database dumps, app credentials, runner logs, or container environments |
| `deployer` | Deploy, roll back, and operate the containers it owns (start/stop/restart/kill/pause) plus read their logs |
| `admin` | Everything, including exec, remove, container create, backup restore, user management, and exposing an app on all interfaces |

API token scopes (`read` / `deploy` / `admin`) map to the same three levels.

## Two things worth being clear about

- **Ports bind loopback.** A catalog app is reachable from the server only until
  someone deliberately publishes it. Give it a domain on the Domains page
  instead where you can.
- **The audit log's hash chain detects tampering with the file**, not a
  compromised pinqops process — that process holds the whole chain. Ship the log
  off the host if you need more.

## Hardening checklist

- [ ] Branch protection blocks direct pushes to `master`
- [ ] The repository is private
- [ ] Runner runs as a non-root user in the `docker` group
- [ ] No inbound ports (verify with your firewall)
- [ ] If `pinqops-ui` runs: firewalled port, TLS (`--cert`) or
      `--host 127.0.0.1` behind a tunnel
