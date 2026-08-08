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

builder.Build().Run();
