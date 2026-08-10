using McpServer.Auth;
using McpServer.CompanyProfile;
using McpServer.Tenders;
using Microsoft.Extensions.Http.Resilience;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults(); // OTel + health checks + service discovery, one line — doesn't
                               // touch the network, so it doesn't compromise the fail-fast check
                               // below. Resilience is added explicitly per HttpClient instead
                               // (see the AddHttpClient<IProzorroClient, ProzorroClient> call
                               // below) — not a blanket ConfigureHttpClientDefaults default.

// 1) Fail fast: refuse to start unauthenticated. Checked before builder.Build() so a
// misconfigured deployment never binds a port or touches the network.
var apiKey = builder.Configuration["MCP_API_KEY"];
if (string.IsNullOrWhiteSpace(apiKey))
{
    using var bootstrapLoggerFactory = LoggerFactory.Create(b => b.AddSimpleConsole());
    bootstrapLoggerFactory.CreateLogger("Startup").LogCritical(
        "MCP_API_KEY is not set. Refusing to start unauthenticated. " +
        "Set the MCP_API_KEY environment variable and restart.");
    return 1;
}

// 2) Config (see docs/01-mcp-server.md's config table).
var tenderApiBaseUrl = builder.Configuration["TENDER_API_BASE_URL"]
    ?? "https://public.api.openprocurement.org/api/2.5";
var cacheSeconds = int.TryParse(builder.Configuration["TENDER_CACHE_SECONDS"], out var configuredCacheSeconds)
    ? configuredCacheSeconds
    : 300;

// 3) Services.
builder.Services.AddMemoryCache();
builder.Services.AddSingleton(new ProzorroClientOptions(tenderApiBaseUrl, TimeSpan.FromSeconds(cacheSeconds)));
builder.Services.AddHttpClient<IProzorroClient, ProzorroClient>((sp, client) =>
{
    var options = sp.GetRequiredService<ProzorroClientOptions>();
    client.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/"); // trailing slash: relative request URIs depend on it
    client.DefaultRequestHeaders.UserAgent.ParseAdd("TenderWatch-McpServer/1.0 (+https://github.com/example/mcp-agent)");
}).AddStandardResilienceHandler();
builder.Services.AddSingleton<ICompanyProfileService, CompanyProfileService>();

builder.Services
    .AddMcpServer()
    .WithHttpTransport()
    .WithToolsFromAssembly();

var app = builder.Build();

// 4) Auth middleware before any endpoint — a Use() placed before any Map() calls applies to all
// of them, so this protects /health and /alive (from ServiceDefaults) the same as /mcp. /health
// is deliberately NOT excluded: this AppHost config doesn't use .WithHttpHealthCheck(...), so
// Aspire's dashboard health indicator is process-liveness based, not an authenticated HTTP probe —
// see docs/02-aspire-and-observability.md for the reasoning and the fallback if that's ever wrong.
app.UseMiddleware<ApiKeyAuthMiddleware>();

app.MapDefaultEndpoints(); // /health, /alive — from ServiceDefaults

// 5) MCP endpoint, explicit path so client configs (curl, Inspector) are unambiguous.
app.MapMcp("/mcp");

app.Run();
return 0;
