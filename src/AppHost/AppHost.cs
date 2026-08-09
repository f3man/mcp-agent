using Aspire.Hosting;

var builder = DistributedApplication.CreateBuilder(args);

builder.AddDockerComposeEnvironment("docker-compose");

// Azure Container Apps target for `aspire deploy` — needs Aspire.Hosting.Azure.AppContainers.
var acaEnv = builder.AddAzureContainerAppEnvironment("aca-env");

// Provisioned once and referenced below — auto-injects APPLICATIONINSIGHTS_CONNECTION_STRING,
// which ServiceDefaults/Extensions.cs already conditionally wires up to UseAzureMonitor().
var appInsights = builder.AddAzureApplicationInsights("appinsights");

var apiKey = builder.AddParameter("mcp-api-key", secret: true);

var mcpServer = builder.AddProject<Projects.McpServer>("mcp-server")
    .WithEnvironment("MCP_API_KEY", apiKey)
    .WithExternalHttpEndpoints();

// Azure-only wiring, gated behind IsPublishMode: `.WithReference(appInsights)` needs to resolve a
// real Application Insights connection string before the resource can even start, which requires
// live Azure access — during a plain `aspire run` that just hangs (mcp-server never leaves
// "Starting"). App Insights has no local emulator, so the standard Aspire pattern is to skip this
// wiring entirely for local `run` and only apply it when actually publishing/deploying.
if (builder.ExecutionContext.IsPublishMode)
{
    mcpServer
        .WithReference(appInsights)
        // Required once a second compute environment (Docker Compose) exists alongside aca-env —
        // Aspire needs an explicit target per resource rather than guessing. This binds mcp-server
        // to Azure Container Apps specifically for `aspire deploy`; `aspire run` never evaluates
        // compute-environment bindings at all, so this is deploy-only regardless.
        .WithComputeEnvironment(acaEnv);
}

// Task 2 — state store for the loop orchestrator. RunAsEmulator() switches to Azurite locally and
// to the real Azure Storage account when publishing/deploying, automatically — unlike App
// Insights, this does NOT need IsPublishMode gating (Aspire's own docs: calling RunAsEmulator
// doesn't affect the publishing manifest).
var storage = builder.AddAzureStorage("storage").RunAsEmulator();
var tenderTable = storage.AddTables("tender-state");

// Unlike mcp-api-key (a hard requirement — McpServer fails fast without it), these three are
// genuinely optional at the app layer: LoopOrchestrator/Program.cs logs a warning and degrades
// gracefully (VerifyStage forces "uncertain" without embeddings, etc. — see docs/task-2 plan §8).
// A plain `AddParameter(..., secret: true)` with no configured value stays "ValueMissing" forever
// and blocks the referencing project resource from ever starting (observed directly: mcp-server
// stayed stuck in "Starting" — never even spawning a process — until mcp-api-key resolved). Reading
// the config value ourselves with a "" fallback keeps these resolvable (Running immediately) so
// loop-orchestrator can start without them, while a real secret set via `dotnet user-secrets`
// still flows through unchanged when present.
var anthropicApiKey = builder.AddParameter("anthropic-api-key", builder.Configuration["Parameters:anthropic-api-key"] ?? "", secret: true);
var slackWebhookUrl = builder.AddParameter("slack-webhook-url", builder.Configuration["Parameters:slack-webhook-url"] ?? "", secret: true);
var openAiApiKey = builder.AddParameter("openai-api-key", builder.Configuration["Parameters:openai-api-key"] ?? "", secret: true);

var loopOrchestrator = builder.AddProject<Projects.LoopOrchestrator>("loop-orchestrator")
    .WithReference(mcpServer)   // service discovery resolves "mcp-server" to the real endpoint, no hardcoded URL
    .WithReference(tenderTable)
    .WithEnvironment("MCP_API_KEY", apiKey)
    .WithEnvironment("ANTHROPIC_API_KEY", anthropicApiKey)
    .WithEnvironment("SLACK_WEBHOOK_URL", slackWebhookUrl)
    .WithEnvironment("OPENAI_API_KEY", openAiApiKey)
    .WithExternalHttpEndpoints() // so /run-now is reachable for testing/demo, same as mcp-server
    .WaitFor(mcpServer)
    .WaitFor(tenderTable);

if (builder.ExecutionContext.IsPublishMode)
{
    loopOrchestrator
        .WithReference(appInsights)
        .WithComputeEnvironment(acaEnv);
}

builder.Build().Run();
