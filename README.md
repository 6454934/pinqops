# pinqops

**Merge to your default branch → your closed server updates itself.**

GitHub builds the Docker image; a self-hosted runner on your server pulls it and
restarts one compose project. Outbound-only — no open ports, no SSH, no git
token on the server.

![CI](https://github.com/pinqponq/pinqops/actions/workflows/ci.yml/badge.svg)
![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)
![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white)
![Docker](https://img.shields.io/badge/Docker-2496ED?logo=docker&logoColor=white)
![Ubuntu](https://img.shields.io/badge/Ubuntu%20%2F%20Debian-E95420?logo=ubuntu&logoColor=white)

## Quick start

**1. App repo** — add a `Dockerfile` and copy
[`examples/workflows/deploy.yml`](examples/workflows/deploy.yml) to
`.github/workflows/deploy.yml`.

**2. Server** (Ubuntu/Debian; Docker is installed for you if missing):

```bash
sudo curl -fsSL -o /usr/local/bin/pinqops \
  https://github.com/pinqponq/pinqops/releases/latest/download/pinqops
sudo chmod +x /usr/local/bin/pinqops

# CLI path
pinqops setup --repo-url https://github.com/<owner>/<repo>

# Or dashboard (also installs Docker when needed)
curl -fsSL -o /tmp/pinqops-ui \
  https://github.com/pinqponq/pinqops/releases/latest/download/pinqops-ui
chmod +x /tmp/pinqops-ui
sudo install /tmp/pinqops-ui /usr/local/bin/pinqops-ui
sudo pinqops-ui install-service
sudo journalctl -u pinqops-ui | grep "setup code"   # then open http://<server>:7467
```

**3. Deploy** — merge to your default branch. Rollback with `pinqops rollback`
or one click in the dashboard.

Full walkthrough: [docs/SETUP.md](docs/SETUP.md).

## What you get

- SHA-pinned deploys, health checks, deploy history, rollback
- Optional web UI: multi-app, GitHub publish wizard, domains/HTTPS (Caddy),
  PR previews, backups, alerts, teams/audit, API tokens
- Remote MCP at `/mcp` — Cursor/agents connect with URL + bearer token (no local binary)
- Self-update: `sudo pinqops update` / `sudo pinqops-ui update`

> The UI opens one inbound port on an otherwise closed server. Firewall it,
> bind to `127.0.0.1`, or skip the UI — CLI alone is enough. See
> [SECURITY.md](SECURITY.md).

## Docs

| | |
|---|---|
| [SETUP.md](docs/SETUP.md) | Bare server → first deploy |
| [CONFIGURATION.md](docs/CONFIGURATION.md) | Flags and defaults |
| [ARCHITECTURE.md](docs/ARCHITECTURE.md) | Flow and trust boundaries |
| [TOKENS.md](docs/TOKENS.md) | Which token goes where |
| [API-AND-AGENTS.md](docs/API-AND-AGENTS.md) | MCP / API tokens |
| [SECURITY.md](SECURITY.md) | Security model |
| [Wiki](https://github.com/pinqponq/pinqops/wiki) | Guides (incl. Turkish) |

## License

[MIT](LICENSE) © pinqops contributors
