using LoopOrchestrator.Llm;
using LoopOrchestrator.Loop;
using LoopOrchestrator.Loop.Stages;
using LoopOrchestrator.Mcp;
using LoopOrchestrator.Notifications;
using LoopOrchestrator.Rag;
using LoopOrchestrator.State;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();

// 1) Fail fast on MCP_API_KEY only — Discover (and therefore the whole loop) is useless without
// it, same reasoning as McpServer's own fail-fast check. ANTHROPIC_API_KEY / SLACK_WEBHOOK_URL /
// OPENAI_API_KEY are NOT fail-fast: Discover + idempotency + AppHost topology are all meant to be
// independently verifiable without them (see docs/task-2/01-loop-orchestrator.md's verification
// plan) — a run started without them will fail loudly at the first stage that actually needs the
// missing credential, rather than refusing to start at all.
var mcpApiKey = builder.Configuration["MCP_API_KEY"];
if (string.IsNullOrWhiteSpace(mcpApiKey))
{
    using var bootstrapLoggerFactory = LoggerFactory.Create(b => b.AddSimpleConsole());
    bootstrapLoggerFactory.CreateLogger("Startup").LogCritical(
        "MCP_API_KEY is not set. Refusing to start — the loop can't discover tenders without it. " +
        "Set the MCP_API_KEY environment variable and restart.");
    return 1;
}

var anthropicApiKey = builder.Configuration["ANTHROPIC_API_KEY"];
var slackWebhookUrl = builder.Configuration["SLACK_WEBHOOK_URL"];
var openAiApiKey = builder.Configuration["OPENAI_API_KEY"];

using (var bootstrapLoggerFactory = LoggerFactory.Create(b => b.AddSimpleConsole()))
{
    var startupLogger = bootstrapLoggerFactory.CreateLogger("Startup");
    if (string.IsNullOrWhiteSpace(anthropicApiKey))
        startupLogger.LogWarning("ANTHROPIC_API_KEY is not set — Classify/Verify/Handoff-summary stages will fail when reached.");
    if (string.IsNullOrWhiteSpace(slackWebhookUrl))
        startupLogger.LogWarning("SLACK_WEBHOOK_URL is not set — Handoff notifications will fail when reached.");
    if (string.IsNullOrWhiteSpace(openAiApiKey))
        startupLogger.LogWarning("OPENAI_API_KEY is not set — eligibility index will stay empty; Verify will always return 'uncertain'.");
}

var loopIntervalMinutes = int.TryParse(builder.Configuration["LOOP_INTERVAL_MINUTES"], out var lim) ? lim : LoopOptions.DefaultLoopIntervalMinutes;
var handoffValueThreshold = decimal.TryParse(builder.Configuration["HANDOFF_VALUE_THRESHOLD"], out var hvt) ? hvt : LoopOptions.DefaultHandoffValueThreshold;
var maxTendersPerRun = int.TryParse(builder.Configuration["MAX_TENDERS_PER_RUN"], out var mtpr) ? mtpr : LoopOptions.DefaultMaxTendersPerRun;

// 2) Services.
builder.Services.AddSingleton(new LoopOptions(loopIntervalMinutes, handoffValueThreshold, maxTendersPerRun));

// State store — Azure Table Storage (Azurite emulator locally via Aspire, real Storage in the
// cloud; same code path both times, only the connection string differs).
builder.AddAzureTableServiceClient("tender-state");
builder.Services.AddSingleton<ITenderStateStore, TableStorageStateStore>();

// MCP client — Scoped (not the AddHttpClient default of Transient) so LoopRunner and every stage
// resolved from the same scope share the one McpTenderClient/McpClient session for a whole run.
// See Mcp/McpTenderClient.cs's remarks.
builder.Services.AddSingleton(new McpTenderClientOptions(mcpApiKey));
builder.Services.AddHttpClient<McpTenderClient>();
builder.Services.AddScoped<IMcpTenderClient>(sp => sp.GetRequiredService<McpTenderClient>());

// RAG — Singleton: chunks are embedded once at startup (below) and held for the process lifetime.
builder.Services.AddSingleton<IEmbeddingClient>(new OpenAiEmbeddingClient(openAiApiKey));
builder.Services.AddSingleton<IEligibilityIndex, InMemoryEligibilityIndex>();

// LLM + Slack — stateless beyond the injected HttpClient, default Transient lifetime is fine.
builder.Services.AddHttpClient<AnthropicClient>(client =>
{
    client.BaseAddress = new Uri("https://api.anthropic.com/");
    client.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
    if (!string.IsNullOrWhiteSpace(anthropicApiKey))
    {
        client.DefaultRequestHeaders.Add("x-api-key", anthropicApiKey);
    }
});
builder.Services.AddHttpClient<SlackNotifier>(client =>
{
    if (!string.IsNullOrWhiteSpace(slackWebhookUrl))
    {
        client.BaseAddress = new Uri(slackWebhookUrl);
    }
});

// Loop stages + runner — Scoped, so a single run's object graph (including the shared
// McpTenderClient above) is fully independent from any other concurrent/subsequent run.
builder.Services.AddScoped<DiscoverStage>();
builder.Services.AddScoped<ClassifyStage>();
builder.Services.AddScoped<VerifyStage>();
builder.Services.AddScoped<PersistStage>();
builder.Services.AddScoped<HandoffStage>();
builder.Services.AddScoped<LoopRunner>();

builder.Services.AddHostedService<LoopBackgroundWorker>();

var app = builder.Build();

// 3) Index the qualification docs once at startup (re-embedding every startup is fine at this
// PoC scale per docs/task-2/02-rag-and-data.md — no caching).
await IndexQualificationDocsAsync(app);

app.MapDefaultEndpoints();

app.MapPost("/run-now", async (LoopRunner runner, CancellationToken cancellationToken) =>
{
    var result = await runner.TryRunOnceAsync(cancellationToken);
    return result.Started ? Results.Accepted(value: result) : Results.Conflict("A run is already in progress.");
});

app.Run();
return 0;

static async Task IndexQualificationDocsAsync(WebApplication app)
{
    var qualificationDocsPath = Path.GetFullPath(
        Path.Combine(app.Environment.ContentRootPath, "..", "..", "data", "qualification-docs"));

    var chunks = new List<DocumentChunk>();
    if (Directory.Exists(qualificationDocsPath))
    {
        foreach (var file in Directory.GetFiles(qualificationDocsPath, "*.md"))
        {
            var text = await File.ReadAllTextAsync(file);
            chunks.AddRange(MarkdownChunker.ChunkMarkdownFile(Path.GetFileName(file), text));
        }
    }
    else
    {
        app.Logger.LogWarning("Qualification docs directory not found at {Path} — eligibility index will be empty.", qualificationDocsPath);
    }

    await app.Services.GetRequiredService<IEligibilityIndex>().IndexAsync(chunks, CancellationToken.None);
}
