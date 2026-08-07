# App Catalog

The **Apps** view is a curated catalog of ~50 self-hosted apps — databases
(Redis, PostgreSQL, MySQL, MongoDB, ClickHouse, …), search & queues
(Elasticsearch, RabbitMQ, …), monitoring (Grafana, Prometheus, Uptime
Kuma, …), dev tools (Gitea, …), auth, and more.

## How installs work

- Click **Install**, adjust host ports if you like (every port can be
  remapped), confirm.
- The install runs as a **background job**: the modal shows live
  `pulling the image… → starting the container…` progress, and the row shows
  an *installing* badge. No page refresh needed. Large images are given up to
  30 minutes to pull.
- Containers are named `pinqops-<id>`, labeled, attached to a shared
  `pinqops-apps` network, and restart unless stopped.
- Data lives in named volumes; **Remove** deletes the container but keeps the
  volumes.
- **Open** links to the port the container actually binds (even if you
  remapped it).

Only catalog apps can be installed — the endpoint accepts no arbitrary
images. Concurrent installs of the same app are rejected.

## Notes

- Default credentials (when an app needs one) are shown in the install
  dialog — change them after first login.
- The catalog list lives in
  [`AppCatalog.cs`](https://github.com/pinqponq/pinqops/blob/master/src/PinqOps.Web/AppCatalog.cs);
  PRs adding well-known apps are welcome.
