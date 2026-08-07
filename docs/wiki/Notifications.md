# Notifications

pinqops reports every deploy result — from CI deploys on the runner and from
dashboard rollbacks alike.

> **Deploy events vs alerts.** This page covers *deploy* notifications, which
> belong to one app. Threshold rules on the server's CPU, memory, disk and
> containers are [Alerts](Alerts) — same three channels, but configured
> server-wide. Both are edited on the dashboard's **Alerts** page.

## Events

| Event | Fires when |
|---|---|
| `deploy_succeeded` | Pull, up and health check all passed |
| `deploy_failed` | Pull or `up -d` failed |
| `health_check_failed` | Containers came up but did not become running/healthy |
| `rolled_back` | A rollback completed |

Each event can be toggled independently.

## Channels

- **Webhook** — POSTs the full JSON payload
  (`event`, `tag`, `previousTag`, `host`, `error`, `timestamp`) to your URL.
- **Slack** — incoming-webhook text message. The same payload shape works for
  Discord (append `/slack` to the Discord webhook URL) and Mattermost.
- **Telegram** — a bot message via `sendMessage` (bot token + chat id).

## Setup

Dashboard → **Alerts** → *Notifications*: enable channels, fill in the
fields, hit **Test** per channel (it saves first, then sends a synthetic
`deploy_succeeded`). The card used to live under Settings; Settings now links
to it, so there is one page for everything that pings you.

Settings live in `<compose-dir>/.pinqops/notify.json` (0600) — next to the
compose file on purpose, so the CLI running under the runner user reads the
same config the dashboard writes.

Delivery is best-effort: 5 seconds per channel, failures are logged and
swallowed. A notification problem can never fail or slow down a deploy.
