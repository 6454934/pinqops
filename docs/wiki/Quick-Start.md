# Quick Start

From an empty Ubuntu/Debian server to a `master`-merge deploy.

## 1. In your application repository

- Add a `Dockerfile` at the repo root
  ([example](https://github.com/pinqponq/pinqops/blob/master/examples/app/Dockerfile.example)).
- Protect `master` (require PRs, block force pushes).

The deploy workflow can be committed for you by the web UI's
[Repository Wizard](Repository-Wizard), or copy
[`examples/workflows/deploy.yml`](https://github.com/pinqponq/pinqops/blob/master/examples/workflows/deploy.yml)
to `.github/workflows/deploy.yml` yourself.

## 2. On the server — CLI path

```bash
sudo curl -fsSL -o /usr/local/bin/pinqops \
  https://github.com/pinqponq/pinqops/releases/latest/download/pinqops
sudo chmod +x /usr/local/bin/pinqops

pinqops setup --repo-url https://github.com/<owner>/<repo>
```

`pinqops setup` checks prerequisites (Docker, compose, tar, systemd), mints a
runner registration token (gh CLI → PAT → paste), installs and registers the
self-hosted runner as a systemd service, and prints the remaining compose
steps. If a leftover runner from another repository is found, it is properly
de-registered first.

## 2'. On the server — Web UI path

```bash
sudo curl -fsSL -o /usr/local/bin/pinqops-ui \
  https://github.com/pinqponq/pinqops/releases/latest/download/pinqops-ui
sudo chmod +x /usr/local/bin/pinqops-ui

sudo pinqops-ui install-service
sudo journalctl -u pinqops-ui | grep "setup code"   # open http://<server>:7467
```

Enter the setup code, create a password, then open the **GitHub** menu and run
the wizard — see [Repository Wizard](Repository-Wizard).

## 3. Deploy

Merge a PR into your default branch. GitHub builds and pushes the image to GHCR; the
runner on your server pulls it and restarts the compose project. That's it.

Full manual walkthrough:
[docs/SETUP.md](https://github.com/pinqponq/pinqops/blob/master/docs/SETUP.md).
