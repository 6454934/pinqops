# API tokens, MCP, and AI agents

pinqops exposes its whole dashboard as a REST API, and ships an **MCP server** so
AI agents can drive deploys, rollbacks, status, logs, and metrics. Because
[MCP](https://modelcontextprotocol.io) is an open standard and the API is plain
HTTP + bearer tokens, this works with **any** agent — Claude Code / Claude
Desktop, Cursor, the OpenAI Agents SDK / Codex, LangChain, or your own script.

## 1. Create an API token

Dashboard → **Settings → API tokens** → Create. Pick a scope:

| Scope | Can do |
|---|---|
| `read` | All `GET`s: list apps, deploy status/history, logs, metrics. |
| `deploy` | `read` + trigger a deploy, roll back, apply env, install catalog apps, run a backup/restore. |
| `admin` | Everything, including settings, domains, backups config, and token management. |

The token (`pot_…`) is shown **once**. Store it as `PINQOPS_TOKEN`. Every
request sends it as `Authorization: Bearer pot_…`; a token used beyond its scope
gets `403` with a message saying which scope was needed.

## 2. Remote MCP (recommended) — URL only

The dashboard speaks MCP over **Streamable HTTP** at `/mcp`. No local binary:
point Cursor (or any MCP client) at the dashboard URL and send your token.

Tools: `list_apps`, `deploy_status`, `deploy_history`, `trigger_deploy`,
`rollback`, `app_metrics`, `container_logs`.

### Cursor (`~/.cursor/mcp.json`)

```json
{
  "mcpServers": {
    "pinqops": {
      "url": "http://YOUR_SERVER:7467/mcp",
      "headers": {
        "Authorization": "Bearer pot_…"
      }
    }
  }
}
```

Prefer HTTPS in production (`https://pinqops.example.com/mcp`).

### Claude Code / local stdio bridge (optional)

If the agent host cannot reach the dashboard URL, the local stdio bridge still
works — it needs the `pinqops` binary and:

```
PINQOPS_URL=https://pinqops.example.com
PINQOPS_TOKEN=pot_…
PINQOPS_INSECURE=1   # optional: accept a self-signed cert
```

```bash
claude mcp add pinqops --env PINQOPS_URL=https://pinqops.example.com \
  --env PINQOPS_TOKEN=pot_… -- pinqops mcp
```

### OpenAI Agents SDK / Codex

The OpenAI Agents SDK speaks MCP over stdio — point it at the same command:

```python
from agents.mcp import MCPServerStdio

pinqops = MCPServerStdio(params={
    "command": "pinqops",
    "args": ["mcp"],
    "env": {"PINQOPS_URL": "https://pinqops.example.com", "PINQOPS_TOKEN": "pot_…"},
})
# add `pinqops` to your Agent's mcp_servers=[...]
```

Codex and other MCP-aware CLIs use the same `command` / `args` / `env` shape in
their MCP config.

## 3. Plain REST (OpenAI function calling, curl, CI)

No MCP needed — any HTTP client works. The same token, the same scopes:

```bash
curl -H "Authorization: Bearer $PINQOPS_TOKEN" https://pinqops.example.com/api/settings
curl -H "Authorization: Bearer $PINQOPS_TOKEN" -X POST \
  "https://pinqops.example.com/api/setup/trigger-deploy?appId=acme-shop"
```

For an OpenAI function-calling agent, wrap the endpoints you need as tools (the
model calls them, your code makes the HTTP request). Useful ones:

| Purpose | Method + path |
|---|---|
| List apps | `GET /api/settings` → `apps[]` |
| Deploy status | `GET /api/deploy/state?appId=<id>` |
| Deploy history | `GET /api/deploy/history?appId=<id>` |
| Trigger deploy | `POST /api/setup/trigger-deploy?appId=<id>` |
| Roll back | `POST /api/deploy/rollback?appId=<id>` `{ "tag": "sha-…" }` |
| Container metrics | `GET /api/docker/stats` |
| Alert rules + state | `GET /api/alerts` |
| Firing summary | `GET /api/alerts/state` |
| Metric history | `GET /api/alerts/metrics?metric=host.cpu&hours=24` |
| Container logs | `GET /api/docker/containers/<id>/logs` |

`appId` is optional; with one app it defaults to that app.
