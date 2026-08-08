# Tender Watch — MCP Server (Task 1)

A .NET Aspire solution exposing a read-only MCP server over Ukraine's public Prozorro
procurement feed (the "national e-procurement feed"), built as Part 1 of the Tender Watch
course project. See `docs/00-overview.md` for the full project scope and `docs/` for the
per-increment specs this implements.

## What's here

```
TenderWatch.slnx
├── src/
│   ├── McpServer/        # the MCP server itself — tools, auth, Prozorro client (docs/01-mcp-server.md)
│   ├── ServiceDefaults/   # shared OTel/health-checks/resilience wiring (docs/02-aspire-and-observability.md)
│   └── AppHost/           # Aspire orchestrator — source of truth for local dev + publish
├── tests/McpServer.Tests/ # 33 xUnit tests over the server's pure logic
└── data/company-profile.json
```

## Running it

```bash
dotnet user-secrets set "Parameters:mcp-api-key" "local-dev-key" --project src/AppHost
aspire run --project src/AppHost/AppHost.csproj
```

This starts `mcp-server` and opens the Aspire Dashboard (traces, metrics, structured logs).
`MCP_API_KEY` is injected from the `mcp-api-key` parameter — every MCP request (and `/health`)
requires the matching `X-Api-Key` header.

Need a standalone `docker-compose.yml` (no Aspire CLI required to run it)?

```bash
aspire publish -p docker-compose -o ./docker
```

Regenerate rather than hand-edit the output — change `AppHost.cs`/`ServiceDefaults` and republish.

## Interacting with the server

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

## Conclusions

- **Deployment method chosen and why**: .NET Aspire for local dev-time orchestration and
  observability parity, targeting Azure Container Apps via `aspire deploy` for the cloud leg.
  Container Apps over App Service/a VM: scale-to-zero for a low-traffic PoC, no infrastructure to
  patch or manage by hand, and Aspire's own deployment model is built directly around it — no
  hand-written Bicep/Dockerfiles needed for the common case.
- **Authorization mechanism used**: a static API key via the `X-Api-Key` header, checked in
  `ApiKeyAuthMiddleware` before any request reaches an endpoint (`/mcp`, `/health`, `/alive`
  alike), and the server refuses to start at all if `MCP_API_KEY`/the `mcp-api-key` Aspire
  parameter isn't configured. Tradeoff: simple and sufficient for a single-tenant PoC with one
  trusted client, but there's no per-caller identity, scoping, or expiry — a real multi-tenant
  deployment would need to move to OAuth 2.0/Bearer tokens (e.g. Entra ID) so individual callers
  can be identified, scoped, and revoked independently instead of sharing one long-lived secret.
- **Logging and monitoring tools configured**: OpenTelemetry (traces, metrics, structured logs)
  via the shared `ServiceDefaults` project, exported over OTLP. Locally this lands in the Aspire
  Dashboard; in the cloud the exact same instrumentation redirects to Azure Monitor via a single
  conditional (`APPLICATIONINSIGHTS_CONNECTION_STRING` presence, see `ServiceDefaults/Extensions.cs`)
  — no code changes between environments, just where telemetry is sent. Each of the four MCP tools
  (`list_tenders`, `get_tender`, `search_tenders`, `get_company_profile`) is wrapped in its own
  `Activity` (`McpServer/Telemetry/ToolTelemetry.cs`) so tool calls show up as individual, taggable
  spans rather than opaque HTTP requests, and a deliberately bad `tenderId` produces an explicit
  `fail`-level structured log entry (`McpServer.Tools.TenderTools[0]`) correlated to that span.
- **Difficulties encountered and how resolved**:
  - The sandboxed WSL shell used to build/verify this couldn't reach processes started by the
    Windows-side `dotnet.exe` over `localhost` at all (confirmed for both a plain Kestrel server
    in the previous increment and, separately, the Windows Docker Desktop daemon via `docker.exe`
    — the client CLI works, but its named-pipe connection to `dockerDesktopLinuxEngine` doesn't
    resolve through the WSL interop boundary in this environment). Resolved for .NET by installing
    a user-local Linux .NET 10 SDK (`dotnet-install.sh`, no sudo) and doing all builds/runs/curls
    through that instead. The Docker daemon side was **not** resolved — see the docker-compose
    note below.
  - Aspire's `mcp-api-key` parameter name contains a hyphen, which bash's `export NAME=value`
    syntax can't express as an env var name (`Parameters__mcp-api-key`). Used
    `dotnet user-secrets set "Parameters:mcp-api-key" ...` instead, which sidesteps the shell
    entirely and is also the documented "proper" local-dev pattern.
  - The native `AppHost` apphost executable failed with "You must install .NET" until
    `DOTNET_ROOT` was set explicitly — it doesn't consult `PATH`/`dotnet` the way the CLI does, so
    it couldn't find the custom Linux SDK install on its own.
  - `aspire publish -p docker-compose` initially failed with an opaque "Run completed without
    returning a backchannel" error. Root cause: this Aspire version (13.4.6) requires an explicit
    Docker Compose *environment* resource — `builder.AddDockerComposeEnvironment("docker-compose")`
    in `AppHost.cs`, backed by the `Aspire.Hosting.Docker` NuGet package — neither of which is
    mentioned in `docs/02-aspire-and-observability.md` (likely written against an earlier Aspire
    release where this was built into the core hosting package). Once added, publish succeeded and
    produced a valid `docker-compose.yaml` + `.env` (confirmed via `docker compose config`, which
    resolves and prints a fully valid merged config).
  - Verifying telemetry headlessly (no browser in this environment) was the trickiest part: the
    Aspire Dashboard is a Blazor Server app with in-memory OTLP storage and no plain REST API to
    script against. Structured logs turned out to be retrievable directly from per-resource log
    files DCP writes under its temp working directory, which is how the forced `get_tender`
    error's `fail`-level log entry was confirmed end-to-end. Full trace-waterfall confirmation
    (checklist item 2) was **not** independently verified beyond code-level assurance (the
    `ActivitySource` is registered for export and `OTEL_EXPORTER_OTLP_ENDPOINT` is confirmed set
    in the running process's environment) — recommended as a 2-minute manual check: run
    `aspire run` yourself and open the printed dashboard URL.
  - Could not actually build+run the generated `docker-compose.yaml` end-to-end in this
    environment, for the Docker-daemon-unreachable reason above — `.NET`'s container publish
    tooling (`dotnet publish -p:PublishProfile=DefaultContainer`) also failed for the same root
    cause (`Cannot find docker/podman executable` / daemon unreachable once a wrapper script was
    added). The compose file's structure and variable wiring were validated statically instead.

### Cloud deployment status

**Not yet done — pending Azure credentials.** This increment implements and verifies everything
that works without an Azure subscription: Aspire orchestration, custom tracing, local dashboard/
log verification, and `aspire publish -p docker-compose` producing a valid compose stack.
`aspire deploy` to Azure Container Apps, and the corresponding Azure Monitor verification, were
intentionally not attempted — that requires interactive cloud login this environment can't
perform. To complete that leg yourself:

1. `az login` — Azure CLI auth.
2. `aspire login` (or the equivalent Azure auth flow for the installed Aspire CLI version), if required.
3. `aspire deploy` — provisions/reuses an Azure Container Registry and Container Apps environment,
   builds and pushes the `mcp-server` image, and deploys it with `MCP_API_KEY` as a Container Apps
   secret.
4. Set `APPLICATIONINSIGHTS_CONNECTION_STRING` on the deployed Container App (from an Application
   Insights resource in the same environment) — the `Azure.Monitor.OpenTelemetry.AspNetCore` wiring
   in `ServiceDefaults` is already in place and picks it up with zero code changes.
5. Re-run the five steps in `docs/03-verification.md` against the deployed HTTPS endpoint and
   confirm the same tool-call trace appears in Azure Monitor.
