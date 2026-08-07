# pinqops

**Merge to your default branch → your closed server updates itself.** GitHub builds the
Docker image; a small self-hosted runner on your server pulls it and restarts
one compose project. Outbound-only — no open ports, no SSH, no git token on
the server.

## Pages

| Page | What it covers |
|---|---|
| [Quick Start](Quick-Start) | Empty server → first deploy, in minutes |
| [Web UI](Web-UI) | The minimal dashboard: views, auth, options |
| [Repository Wizard](Repository-Wizard) | GitHub sign-in, picking a repo, the step-by-step install wizard |
| [Deploy History & Rollback](Deploy-History-and-Rollback) | SHA-tagged deploys, health checks, one-click rollback |
| [Notifications](Notifications) | Deploy results via webhook / Slack / Telegram |
| [Alerts](Alerts) | Threshold rules on CPU, memory, disk and containers |
| [App Catalog](App-Catalog) | ~50 one-click apps and how installs work |
| [Runner Troubleshooting](Runner-Troubleshooting) | Why a runner shows offline / missing, and how pinqops fixes it |
| [Security Model](Security-Model) | Trust boundaries and hardening checklist |
| [Türkçe Rehber](Turkce-Rehber) | Hızlı başlangıç ve sihirbaz rehberi (Türkçe) |

## How it works

```
merge PR → GitHub Actions (cloud): docker build → push ghcr.io
                    ↓
      self-hosted runner on your server (outbound-only)
                    ↓
        pinqops deploy --tag sha-<commit>:
          pin tag → compose pull → up -d → health check
          → record history + notify → keep last N images
```

A failed health check shows red in CI and fires a notification; rolling back
(`pinqops rollback` or the dashboard button) is always an explicit action.

The server never exposes a port for deploys and never holds a git token. The
optional web UI is the only component that listens on a port — and it is
optional.

## Repository docs

The canonical reference lives in the repository:
[SETUP](https://github.com/pinqponq/pinqops/blob/master/docs/SETUP.md) ·
[TOKENS](https://github.com/pinqponq/pinqops/blob/master/docs/TOKENS.md) ·
[ARCHITECTURE](https://github.com/pinqponq/pinqops/blob/master/docs/ARCHITECTURE.md) ·
[CONFIGURATION](https://github.com/pinqponq/pinqops/blob/master/docs/CONFIGURATION.md) ·
[SECURITY](https://github.com/pinqponq/pinqops/blob/master/SECURITY.md)
