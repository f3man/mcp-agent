# Conclusions — Task 1, 1st Iteration

Referenced from the main [`README.md`](../README.md).

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
    — the client CLI worked, but its named-pipe connection to `dockerDesktopLinuxEngine` didn't
    resolve through the WSL interop boundary). Resolved for .NET by installing a user-local Linux
    .NET 10 SDK (`dotnet-install.sh`, no sudo). Resolved for Docker by enabling Docker Desktop's
    **WSL integration** for this specific distro (Settings → Resources → WSL Integration) — once
    on, `/var/run/docker.sock` appears inside the shell talking to the same daemon, and both
    `aspire publish -p docker-compose` and the container build step of `aspire deploy` started
    working normally.
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
    (checklist item 2) was **not** independently verified locally beyond code-level assurance (the
    `ActivitySource` is registered for export and `OTEL_EXPORTER_OTLP_ENDPOINT` is confirmed set
    in the running process's environment) — recommended as a 2-minute manual check: run
    `aspire run` yourself and open the printed dashboard URL. This *was* independently confirmed
    against the cloud deployment, though — see below.
  - `aspire deploy` needed a similarly undocumented fix on the Azure side: an explicit
    `builder.AddAzureContainerAppEnvironment("aca-env")` (from `Aspire.Hosting.Azure.AppContainers`)
    plus `.WithComputeEnvironment(acaEnv)` on `mcp-server` once a second compute environment
    (Docker Compose) existed in the same `AppHost.cs`. An early, wrong diagnosis blamed the two
    environments coexisting for a generic "no backchannel" failure — the actual cause was just the
    missing Docker daemon (see above); once Docker was reachable, `docker-compose` and
    `aca-env` deploy from the same AppHost with no conflict.
  - `aspire deploy` itself needs several interactive answers on a first run (Azure tenant,
    subscription, resource group, region) that this non-interactive/piped shell couldn't provide
    normally (`--non-interactive` fails outright with "Cannot show selection prompt"; blank input
    can also silently accept the wrong list item — one pass landed on a literal "asia" grouping,
    which isn't a valid resource-group location and failed provisioning). Resolved by driving the
    CLI through a real pseudo-terminal (`script -qc "aspire deploy ..." /dev/null`) with the region
    typed explicitly rather than blindly accepted; answers then cache in
    `~/.aspire/deployments/<hash>/production.json` so reruns don't re-prompt. Also hit one
    transient `Text file busy: .../bicep` error from two provisioning steps racing to use an
    auto-downloaded CLI binary — simply retrying (ARM deployments are idempotent by resource name)
    picked up exactly where it left off.
  - Azure Container Apps *environment* provisioning (the underlying Consumption-plan
    infrastructure, not the container app itself) took roughly 16 minutes on this attempt — much
    slower than the container app/image build/push steps combined. Confirmed via
    `az deployment group list` that this was genuine, ongoing Azure-side work rather than a stuck
    CLI, so the only real fix was patience.
  - A later change (adding the Azure Container App environment + Application Insights reference
    to `AppHost.cs` for the cloud leg) broke local `aspire run` — `mcp-server` got stuck in the
    `Starting` state forever, because `.WithReference(appInsights)` needs to resolve a real Azure
    connection string before the resource can even launch, and there's no local emulator for
    Application Insights. Fixed with Aspire's standard pattern: gate that reference (and the
    `WithComputeEnvironment` binding) behind `builder.ExecutionContext.IsPublishMode`, so it only
    applies during `aspire publish`/`aspire deploy`, never during a plain local `aspire run`.

## Cloud deployment status

**Done.** `mcp-server` is deployed and verified on Azure Container Apps:

- **Live endpoint**: `https://mcp-server.mangohill-8bec81a9.germanywestcentral.azurecontainerapps.io`
- **Resource group**: `rg-aspire-apphost` (subscription `3aa7ce12-0f0b-42d1-b16c-398ad71bff09`, region `germanywestcentral`)
- **Resources provisioned by `aspire deploy`**: the Container Apps environment + its managed identity,
  an Azure Container Registry, a Log Analytics workspace, an Application Insights resource, and the
  `mcp-server` Container App itself, built from the same `AppHost.cs` model used locally.
- **Verified live** (`curl` against the HTTPS endpoint): `/mcp` with the correct `X-Api-Key` completes
  the MCP handshake and returns real Prozorro data for `list_tenders`; missing/wrong key → `401`; a
  bogus `tenderId` on `get_tender` → a clean `isError: true` response, same as local. (`/health`
  itself 404s in this deployment — Container Apps runs the app in the `Production` ASP.NET Core
  environment by default, and `MapDefaultEndpoints()` only maps `/health`/`/alive` when
  `IsDevelopment()`, per the design decision in the previous increment; the auth middleware still
  correctly 401s an unauthenticated request to that path regardless of whether the route exists.)
- **Verified in Application Insights** via `az monitor app-insights query` (KQL): the `list_tenders`
  and `get_tender` tool-call spans appear in the `requests` table (tagged by tool name, `get_tender`
  correctly marked `success=False`), and the forced error appears in the `exceptions` table
  (severity `3`/Error, message `Tender '...' was not found.`, both the wrapped `McpException` and the
  original `TenderNotFoundException`) — logging with an attached exception routes to `exceptions`,
  not `traces`.
- **Also live**: the Aspire Dashboard itself was deployed alongside `mcp-server` (Aspire does this
  automatically for an Azure Container Apps environment) at
  `https://aspire-dashboard.ext.mangohill-8bec81a9.germanywestcentral.azurecontainerapps.io`.

To tear it down when you're done with the submission window: `az group delete -n rg-aspire-apphost --yes`.
