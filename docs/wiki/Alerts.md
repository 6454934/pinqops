# Alerts

Threshold rules over the server's own resources and its containers. The
dashboard samples every metric once a minute, evaluates each rule, and sends the
ones that changed to the notification channels — so a full disk or a container
that died at 3am reaches you instead of waiting to be noticed.

Everything is set up on the **Alerts** page. There is no rule file to edit.

## Metrics

| Metric | Unit | Read from |
|---|---|---|
| `host.cpu` | percent | `/proc/stat`, as the delta between two samples |
| `host.mem` | percent | `/proc/meminfo` — `(MemTotal − MemAvailable) / MemTotal` |
| `host.swap` | percent | `/proc/meminfo` — `SwapTotal` / `SwapFree` |
| `host.disk` | percent | the root filesystem's used space |
| `host.load1` / `load5` / `load15` | ratio | `/proc/loadavg`, **divided by the CPU count** |
| `container.cpu` | percent | `docker stats` |
| `container.mem` | percent | `docker stats` (falls back to used/limit when the container has no memory limit) |
| `container.down` | 0/1 | `docker ps -a` — exists but is not running |
| `container.unhealthy` | 0/1 | its health check is failing |
| `container.restarting` | 0/1 | docker is restarting it right now |

Load averages are divided by the core count on purpose, so `> 1` means the same
thing ("the machine is saturated") on a 2-core VPS and a 64-core box.

`container.restarting` with a `for` of 5 minutes *is* a restart-loop alert — no
separate metric needed.

A container rule targets one container by name, or **all containers** (`*`),
which tracks each one as its own independent series: one noisy container neither
fires for the rest nor silences them.

"All containers" means everything `docker ps -a` lists — including a one-off
container that ran once and exited, which `container.down` will happily report as
not running. On a host with leftovers like that, target the containers you
actually care about by name. A wildcard rule covers at most 200 containers; past
that the dashboard log says so rather than quietly watching a subset.

## How a rule fires

Each series moves through four states:

```
Normal ──breach──▶ Pending ──still breaching after "for"──▶ Alerting
   ▲                  │                                        │
   └──────no breach────┘                    no breach ─────────┘  (sends "resolved")
```

- **`for`** is the window the condition has to hold *continuously*. Set it to
  zero to fire on the first breaching sample; leave it at a few minutes and a
  one-off spike never pages anyone. A rule that recovers while Pending never
  fired, so it never sends anything.
- **Repeat while firing** re-sends on an interval for as long as the condition
  holds. The default is to notify once.
- **No data** is reported when a series stops producing readings for longer than
  its grace period — a deleted container, or docker being down. A single missed
  sample never flaps the state, and that grace covers a firing series too, so a
  one-tick docker hiccup does not disturb an alert that is up.

Three behaviours are deliberate and worth knowing:

- **No data is never an all-clear.** A series that stops reporting is written off
  as *no data*, not as resolved — including one that was firing at the time.
  Past the grace period a firing series is written off like any other, because a
  container deleted while its alert was up would otherwise leave that alert on
  screen for ever, with nothing left that could ever clear it.
- **A firing episode stays open across a gap.** If you were paged, the metric
  stopped arriving, and then came back healthy, you still get the "resolved" you
  are owed — the episode is remembered, not forgotten at the gap.
- **A silence delays an announcement rather than swallowing it.** If a rule
  fires while muted, it is announced when the silence lifts, provided it is still
  firing. Correspondingly, a firing nobody was told about never sends a
  "resolved".

## Silences

**Silence** on a rule stops delivery for a chosen period. Evaluation carries on
and the dashboard keeps showing the real state, so a muted rule is visibly muted
rather than invisibly broken. The alert history records what would have been
sent, with **Sent = no**.

## Channels

Alerts reuse the three delivery channels pinqops already has — **webhook**,
**Slack** and **Telegram** — configured under *Where alerts are sent* on the
Alerts page.

These settings are **server-global**, stored beside `ui.json`, not next to a
compose project. No app owns "host CPU above 90%", and alerting matters most on
a fresh install where no repository is connected yet. Deploy notifications stay
per app (see [Notifications](Notifications)); both are edited on the same page.

A rule with no channels ticked goes to every enabled channel, which is what keeps
it working after a channel is added later.

Webhooks receive a flat JSON body:

```json
{
  "event": "alert_firing",
  "ruleId": "abcd1234",
  "rule": "Disk almost full",
  "metric": "host.disk",
  "series": "",
  "severity": "critical",
  "condition": "> 90%",
  "value": 93.4,
  "repeat": false,
  "firingForSeconds": null,
  "host": "web-01",
  "timestamp": "2026-07-20T12:00:00+00:00"
}
```

`event` is `alert_firing`, `alert_resolved` or `alert_nodata`; a repeat of an
alert that is still firing has `repeat: true`.

Delivery is best-effort: five seconds per channel, failures logged and swallowed,
and at most 20 notifications per evaluation. Anything above that cap is still
written to the alert history. Sending runs detached from the evaluation loop, so
a slow channel can never delay the next sample.

## Metric history

Samples are kept on disk so the page can chart where a metric has been and where
the threshold sits across it — which is what makes a threshold possible to
choose rather than guess.

The file rotates by line count rather than by size, so the window is a
predictable length of *time*: 24 hours per file at one sample a minute, kept as
the live file plus two previous ones, giving **between 48 and 72 hours**. The
chart offers a 48-hour range, so that is the floor rather than an average.

Container series are recorded only for containers a rule watches — a `*` rule
opts all of them in — and at most 40 per sample, busiest first. That bounds the
*chart history* only: rules still evaluate every container.

A measured host-only line is about 70 bytes, and each recorded container adds
about 55. So a host with no container rules uses well under a megabyte in total,
one tracking 20 containers about 5 MB, and the 40-container cap holds the worst
case near 10 MB across all three files.

## Files

All of them beside `ui.json` (`~/.config/pinqops/` by default, or
`PINQOPS_UI_CONFIG`), mode `0600`:

| File | What it holds |
|---|---|
| `alerts.json` | the rules |
| `alert-channels.json` | webhook / Slack / Telegram settings (holds a bot token) |
| `alert-state.json` | each series' current state, so a restart does not re-page |
| `alert-history.jsonl` | every transition; 2 MB per file, the live one plus three previous, so 8 MB at most |
| `metrics.jsonl` | the sample window described above |

A corrupt file is treated as "no rules" or "nowhere to send" — never a dashboard
that will not start.

## Restarts, clocks and scope

- **Restarts don't re-page.** State is on disk, so a rule that was already firing
  stays firing quietly. State older than a couple of ticks is reset at startup:
  a `for` window is meant to prove a condition held *continuously*, and downtime
  proves nothing, so a rule left Pending before a long outage starts its window
  again rather than firing on the first tick back.
- **A clock stepped backwards** (an NTP correction, a VM resumed from a snapshot)
  can delay a transition but never trigger one early.
- **Alerts watch the server the dashboard runs on.** Remote hosts registered
  under Settings → Servers are not sampled: every reading would be an SSH round
  trip, and `/proc` would still describe this machine.

## Permissions

Reading alerts needs only the **viewer** role — what is firing, and the container
names and percentages behind it, are already on the Overview and Containers
views. Creating, editing, silencing and deleting rules, and reading or changing
the channels, are **admin**: silencing a rule turns off paging for everyone on
the host, and a Slack incoming-webhook URL is itself a credential.

## API

See [API & agents](../API-AND-AGENTS.md). `GET /api/alerts`,
`GET /api/alerts/state` and `GET /api/alerts/metrics` are readable with any
token; every write needs an admin token.
