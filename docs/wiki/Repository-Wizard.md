# Repository Wizard

The **GitHub** menu in the web UI is the whole onboarding. It stays visible
with a lock icon until connected; clicking it lands on the sign-in flow.

## 1. Connect

- **Sign in with GitHub** — OAuth device flow: the UI shows a short code, you
  confirm it on github.com. Requires an OAuth App client id (asked once, then
  saved; or set `PINQOPS_GITHUB_CLIENT_ID`).
- **Continue without GitHub** — paste a PAT instead. It must cover everything the
  wizard does, including **committing the deploy workflow**:
  - classic: `repo` **+ `workflow`** (+ `read:packages`)
  - fine-grained: Contents read/write, **Workflows write**, Actions read,
    Administration read

  `workflow` / Workflows: write is not optional — GitHub refuses to create or
  update a file under `.github/workflows/` without it, so the wizard's workflow
  step fails with 403. See
  [TOKENS](https://github.com/pinqponq/pinqops/blob/master/docs/TOKENS.md).

## 2. Pick a repository

Search your repositories (the list narrows as you type; private repos are
marked), select one, press **Install**.

## 3. The wizard

A step-by-step modal with a progress bar and a live log:

| Step | What happens |
|---|---|
| Connect | The repository is saved as the deploy target |
| Dockerfile | Checked only — a missing Dockerfile is a warning (the wizard can't write your app's code) |
| Deploy workflow | `.github/workflows/deploy.yml` is committed to the repo if missing |
| Compose project | `/opt/pinqops/docker-compose.yml` is generated for the repo's GHCR image if missing |
| Self-hosted runner | Downloaded, registered to **this** repository, installed as a systemd service. A leftover runner registered to a *different* repository is stopped, uninstalled, and de-registered first (see [Runner Troubleshooting](Runner-Troubleshooting)) |
| Verification | The wizard asks GitHub whether the runner actually appeared — success is verified, not assumed |

The modal is sticky while running; **Run in background** hides it and the
steps keep executing server-side. The readiness card shows the state of all
four pieces afterwards and has a **Re-run the wizard** button.

## After the wizard

Merge a PR into your default branch. The workflow builds the image in the cloud and the
runner deploys it on your server. Nothing else to click.
