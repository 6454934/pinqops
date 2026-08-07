# Deploy History & Rollback

Since 0.5.0 every deploy is versioned, recorded and reversible.

## How versioning works

- The build job pushes **two tags**: the moving `:latest` and an immutable
  `sha-<commit>`.
- The compose file references the image as
  `image: ghcr.io/<owner>/<repo>:${PINQOPS_TAG:-latest}`.
- `pinqops deploy --tag sha-<commit>` pins that tag in the compose directory's
  `.env` before pulling, so the exact deployed version is always on record.
- Without a `.env` (or with a plain `:latest` image line) everything behaves
  exactly like before 0.5 — the scheme is opt-in per compose file.

## Health check

After `up -d`, pinqops polls `docker compose ps` (default 60 seconds,
`--health-timeout-seconds`, `0` skips) until every service is `running` — and
`healthy`, when the image defines a `HEALTHCHECK`. A service that exits or
turns unhealthy fails the deploy immediately.

**Tip:** add a `HEALTHCHECK` to your Dockerfile; without one, only
"running" can be verified.

A failed check marks the deploy `failed` (the CI job shows red) and sends a
notification. **Nothing is reverted automatically** — rolling back is your
call.

## History

- `pinqops history` — recent deploys as a table (`--json` for scripts).
- Dashboard → **Deployments** → *Deploy history* card: current version badge
  and per-deploy tag / time / result / trigger / health.
- Stored in `<compose-dir>/.pinqops/history.json` (0600, newest first, capped
  at 100 entries).

## Rollback

- CLI: `pinqops rollback` (last successful tag) or
  `pinqops rollback --to sha-<commit>`.
- Dashboard: **Roll back** button on any successful history row, with
  confirmation; runs as a background job with live log.

Rollback is fast and credential-free because pinqops keeps the newest N
(default 5, `--keep-images`) `sha-*` images on disk instead of blanket-pruning.
Rolling back to something older than the retention window needs a
`docker login ghcr.io` with `read:packages` first.
