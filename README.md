# Tender Watch

A .NET Aspire solution with two cooperating services: **`mcp-server`**, a read-only MCP server
over Ukraine's public Prozorro procurement feed, and **`loop-orchestrator`**, an autonomous agent
loop that discovers new tenders through it, screens them against a company profile, checks
eligibility against qualification docs (RAG), and escalates anything worth a human's attention to
Slack. Built as a course project — see `docs/initial-specs/00-overview.md` for the full scope and
`docs/initial-specs/task-1`/`docs/initial-specs/task-2` for the per-increment specs this
implements.

## What's here

```
TenderWatch.slnx
├── src/
│   ├── McpServer/          # MCP server — tools, auth, Prozorro client
│   ├── LoopOrchestrator/   # discover → classify → verify → persist → handoff loop
│   ├── ServiceDefaults/    # shared OTel/health-checks/resilience wiring
│   └── AppHost/            # Aspire orchestrator — source of truth for local dev + publish
├── tests/
│   ├── McpServer.Tests/          # 33 xUnit tests over the server's pure logic
│   └── LoopOrchestrator.Tests/   # 28 xUnit tests over the loop's pure logic
├── data/
│   ├── company-profile.json   # fictional supplier profile used by both services
│   └── qualification-docs/    # certifications/financial-capacity/past-bids/exclusions — RAG source
└── docs/
    ├── prompt-book.md                     # the loop's 3 system prompts + guardrails
    ├── conclusions-1st-iteration.md        # MCP server: deployment, auth, monitoring, difficulties
    ├── conclusions-2nd-iteration.md        # loop orchestrator: live verification, bugs found & fixed
    └── initial-specs/                      # the original per-increment assignment specs
```

## Running it

```bash
dotnet user-secrets set "Parameters:mcp-api-key" "local-dev-key" --project src/AppHost

# Optional — unlocks the loop's LLM/RAG/Slack stages, see "The autonomous loop" below
dotnet user-secrets set "Parameters:anthropic-api-key" "..." --project src/AppHost
dotnet user-secrets set "Parameters:openai-api-key" "..." --project src/AppHost
dotnet user-secrets set "Parameters:slack-webhook-url" "..." --project src/AppHost

aspire run --project src/AppHost/AppHost.csproj
```

This starts `mcp-server`, `loop-orchestrator`, Azure Table Storage (Azurite locally), and opens
the Aspire Dashboard (traces, metrics, structured logs). `MCP_API_KEY` is injected from the
`mcp-api-key` parameter — every MCP request (and `/health`) requires the matching `X-Api-Key`
header.

The three loop secrets above are optional: `loop-orchestrator` still starts and runs
Discover/Classify/idempotency without them, degrading gracefully per missing credential (see
"The autonomous loop" below).

> **Known environment issue**: on some sandboxed/virtualized Linux setups (WSL2 included),
> `aspire run`'s CLI wrapper can crash immediately on launch due to an unresolved upstream Aspire
> orphan-detector bug ([dotnet/aspire#8244](https://github.com/dotnet/aspire/issues/8244)). If
> that happens, run the AppHost directly instead — same orchestration, minus the CLI's wrapper:
> ```bash
> ASPNETCORE_ENVIRONMENT=Development dotnet run --project src/AppHost --no-launch-profile
> ```

Need a standalone `docker-compose.yml` (no Aspire CLI required to run it)?

```bash
aspire publish -p docker-compose -o ./docker
```

Regenerate rather than hand-edit the output — change `AppHost.cs`/`ServiceDefaults` and republish.

## Interacting with the MCP server

Every request — MCP calls and `/health`/`/alive` alike — needs an `X-Api-Key` header matching
whatever `MCP_API_KEY`/`mcp-api-key` was configured with. Find the port from the console output
of whichever run command you used: `http://localhost:5208` by default for a plain
`dotnet run --project src/McpServer`, or a randomly-assigned port printed by `aspire run`/the
dashboard when running through the AppHost.

The four tools:

| Tool | Params |
|---|---|
| `list_tenders` | `category?`, `region?`, `status?` (default `active`), `limit?` (default 20, max 100) |
| `get_tender` | `tenderId` (required) |
| `search_tenders` | `keywords` (required), `limit?` (default 20) |
| `get_company_profile` | none |

Three ways to call them, easiest first:

### Bruno collection

Open `bruno/` in [Bruno](https://www.usebruno.com/) (or run it headlessly:
`npx @usebruno/cli run --env Local` from inside that folder) — a ready-made, numbered set of
requests that runs the full handshake, calls all four tools against live data, auto-chains a real
`tenderId` from `list_tenders` into `get_tender`, and checks the 401/200 auth cases. See
`bruno/README.md` for setup (mainly: point its `apiKey`/`baseUrl` vars at your running server).

### MCP Inspector

```bash
npx @modelcontextprotocol/inspector
```

Transport: **Streamable HTTP**, URL `http://localhost:<port>/mcp`, header `X-Api-Key: <your key>`.
Connect, then "List Tools" and call any of the four directly from the UI.

### Raw curl / JSON-RPC

The transport is MCP's Streamable HTTP: every call is a `POST /mcp` with a JSON-RPC body, and
responses come back as a single SSE frame (`event: message` / `data: {...}`) — that's the
protocol working correctly, not an error. `Accept: application/json, text/event-stream` is
required or the server replies `406`. A session must be started with `initialize` first; the
`Mcp-Session-Id` header it returns has to be echoed on every subsequent call.

```bash
KEY="<your key>"; PORT=5208

# 1. Start a session
curl -si http://localhost:$PORT/mcp \
  -H "X-Api-Key: $KEY" -H "Content-Type: application/json" \
  -H "Accept: application/json, text/event-stream" \
  -d '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"curl","version":"1.0"}}}'
# → grab the Mcp-Session-Id response header, then:
SESSION="<value of that header>"

# 2. Complete the handshake
curl -s http://localhost:$PORT/mcp \
  -H "X-Api-Key: $KEY" -H "Content-Type: application/json" \
  -H "Accept: application/json, text/event-stream" -H "Mcp-Session-Id: $SESSION" \
  -d '{"jsonrpc":"2.0","method":"notifications/initialized"}'

# 3. Call a tool
curl -s http://localhost:$PORT/mcp \
  -H "X-Api-Key: $KEY" -H "Content-Type: application/json" \
  -H "Accept: application/json, text/event-stream" -H "Mcp-Session-Id: $SESSION" \
  -d '{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"list_tenders","arguments":{"limit":5}}}'
```

### Claude Desktop

Claude Desktop's config only reliably launches local **stdio** servers, and this server speaks
Streamable HTTP over `/mcp` — bridge the two with [`mcp-remote`](https://www.npmjs.com/package/mcp-remote),
which runs as a local stdio process Claude Desktop launches normally and forwards each call to the
HTTP endpoint with your `X-Api-Key` header attached.

Edit `claude_desktop_config.json` — `%APPDATA%\Claude\claude_desktop_config.json` on Windows,
`~/Library/Application Support/Claude/claude_desktop_config.json` on macOS — and add:

```json
{
  "mcpServers": {
    "tender-watch": {
      "command": "npx",
      "args": [
        "-y",
        "mcp-remote",
        "http://localhost:5208/mcp",
        "--transport", "http-only",
        "--header", "X-Api-Key:${MCP_API_KEY}"
      ],
      "env": {
        "MCP_API_KEY": "<your key>"
      }
    }
  }
}
```

No space around the colon in `--header` — Claude Desktop's arg escaping on Windows breaks on it;
keep the actual value (if it ever contains spaces) in the `env` block instead. Adjust the URL if
you're running via Aspire (dynamic port) or a deployed HTTPS endpoint. Fully quit and reopen
Claude Desktop after saving — the four tools then show up under the 🔌/tools icon in a chat.

Some newer Claude Desktop builds also accept a server entry shaped like
`{ "type": "http", "url": "...", "headers": { "X-Api-Key": "..." } }` directly, without
`mcp-remote` — worth trying first if your version supports it, but there are recurring reports of
custom headers not always being forwarded that way, so `mcp-remote` is the more reliable option.

## The autonomous loop

`loop-orchestrator` runs discover → classify → verify → persist → handoff on a timer
(`LOOP_INTERVAL_MINUTES`, default 360 minutes) and on demand:

```bash
curl -X POST http://localhost:<port>/run-now
```

- **Discover** — pulls active tenders from `mcp-server`; Table Storage's seen-tender-ID set is the
  sole idempotency guard, so reruns never reprocess the same tender.
- **Classify** — an LLM call (needs `ANTHROPIC_API_KEY`) judges relevance against
  `data/company-profile.json`.
- **Verify** — RAG over `data/qualification-docs/*.md` (embeddings need `OPENAI_API_KEY`) plus an
  LLM call decides eligibility, citing an actual clause from the tender's own text.
- **Persist** — every outcome is written to Table Storage (Azurite locally, real Storage when
  deployed).
- **Handoff** — escalates `uncertain` verdicts and high-value `eligible` ones to Slack (needs
  `SLACK_WEBHOOK_URL`), with an LLM-written brief.

Missing any of the three credentials degrades gracefully rather than crashing — Classify/Verify
just can't run without the LLM key, and Verify falls back to `uncertain` without embeddings — see
`docs/conclusions-2nd-iteration.md` for exactly what was confirmed live (including a real message
delivered to Slack) and the real bugs this testing found and fixed along the way.

## Conclusions

- [`docs/conclusions-1st-iteration.md`](docs/conclusions-1st-iteration.md) — the MCP server:
  deployment method and rationale, auth mechanism, logging/monitoring setup, difficulties
  encountered, and the current cloud deployment status (live endpoint, resources, verification
  results).
- [`docs/conclusions-2nd-iteration.md`](docs/conclusions-2nd-iteration.md) — the loop orchestrator:
  what was verified live end-to-end against real data and real credentials, and the real bugs
  found and fixed while confirming it.
