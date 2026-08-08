# Bruno collection — Tender Watch MCP Server

A minimal [Bruno](https://www.usebruno.com/) collection for exercising `src/McpServer` by hand.

## Setup

1. Open Bruno → **Open Collection** → select this `bruno/` folder.
2. Select the **Local** environment (top-right dropdown) and check the `apiKey` value there.
3. Start the server with that same key:
   ```bash
   MCP_API_KEY=<value of apiKey in environments/Local.bru> dotnet run --project src/McpServer
   ```
   (Or via Aspire: `aspire run --project src/AppHost/AppHost.csproj` — then update the
   environment's `baseUrl` to whatever port the console/dashboard reports for `mcp-server`,
   since Aspire assigns it dynamically. The `mcp-api-key` Aspire parameter must match `apiKey`
   above, or vice versa — see the repo README for `dotnet user-secrets set`.)

## Running it

Run requests **in order**, top to bottom:

1. **Initialize** — starts an MCP session, stores `Mcp-Session-Id` for every later request.
2. **Initialized Notification** — completes the handshake.
3. **List Tools** — sanity check: should list `list_tenders`, `get_tender`, `search_tenders`,
   `get_company_profile`.
4. **list_tenders** / **search_tenders** — hit the live Prozorro API for real data.
5. **get_tender** — auto-fills a real `tenderId` from step 4's response.
6. **get_tender (not found)** — deliberately bogus id, to see the clean MCP error shape.
7. **get_company_profile** — no params.
8. **Health Check** (no key / with key) — the 401-vs-200 auth check against `/health`.

Every MCP response is wrapped in an SSE frame (`event: message` / `data: {...}`) — that's the
Streamable HTTP transport working correctly, not an error; the actual JSON-RPC payload is on the
`data:` line.
