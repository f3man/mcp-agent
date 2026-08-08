using Aspire.Hosting;

var builder = DistributedApplication.CreateBuilder(args);

// Required for `aspire publish -p docker-compose` in this Aspire version (13.4.6) — without an
// explicit environment resource, publish fails ("Run completed without returning a backchannel").
// Needs the Aspire.Hosting.Docker package reference (added to AppHost.csproj).
builder.AddDockerComposeEnvironment("docker-compose");

var apiKey = builder.AddParameter("mcp-api-key", secret: true);

var mcpServer = builder.AddProject<Projects.McpServer>("mcp-server")
    .WithEnvironment("MCP_API_KEY", apiKey)
    .WithExternalHttpEndpoints();

builder.Build().Run();
