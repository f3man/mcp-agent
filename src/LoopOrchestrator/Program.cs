using LoopOrchestrator.Analysis;
using LoopOrchestrator.Llm;
using LoopOrchestrator.Loop;
using LoopOrchestrator.Loop.Stages;
using LoopOrchestrator.Mcp;
using LoopOrchestrator.Notifications;
using LoopOrchestrator.Rag;
using LoopOrchestrator.State;
using Microsoft.Extensions.Http.Resilience;

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
    if (string.IsNullOrWhiteSpace(builder.Configuration["SLACK_SIGNING_SECRET"]))
        startupLogger.LogWarning(
            "SLACK_SIGNING_SECRET is not set — POST /slack/interactions will reject every request (fail closed), " +
            "so clicking the Bid/No-Bid buttons in a Slack message will not record a decision until it's supplied.");
}

var loopIntervalMinutes = int.TryParse(builder.Configuration["LOOP_INTERVAL_MINUTES"], out var lim) ? lim : LoopOptions.DefaultLoopIntervalMinutes;
var handoffValueThreshold = decimal.TryParse(builder.Configuration["HANDOFF_VALUE_THRESHOLD"], out var hvt) ? hvt : LoopOptions.DefaultHandoffValueThreshold;
var maxTendersPerRun = int.TryParse(builder.Configuration["MAX_TENDERS_PER_RUN"], out var mtpr) ? mtpr : LoopOptions.DefaultMaxTendersPerRun;

var analysisIntervalHours = int.TryParse(builder.Configuration["ANALYSIS_INTERVAL_HOURS"], out var aih) ? aih : AnalysisOptions.DefaultAnalysisIntervalHours;
var analysisLookbackDays = int.TryParse(builder.Configuration["ANALYSIS_LOOKBACK_DAYS"], out var ald) ? ald : AnalysisOptions.DefaultAnalysisLookbackDays;
var minDisagreementsForProposal = int.TryParse(builder.Configuration["MIN_DISAGREEMENTS_FOR_PROPOSAL"], out var mdp) ? mdp : AnalysisOptions.DefaultMinDisagreementsForProposal;

// 2) Services.
builder.Services.AddSingleton(new LoopOptions(loopIntervalMinutes, handoffValueThreshold, maxTendersPerRun));
builder.Services.AddSingleton(new AnalysisOptions(analysisIntervalHours, analysisLookbackDays, minDisagreementsForProposal));

// State store — Azure Table Storage (Azurite emulator locally via Aspire, real Storage in the
// cloud; same code path both times, only the connection string differs).
builder.AddAzureTableServiceClient("tender-state");
builder.Services.AddSingleton<ITenderStateStore, TableStorageStateStore>();

// MCP client — Scoped (not the AddHttpClient default of Transient) so LoopRunner and every stage
// resolved from the same scope share the one McpTenderClient/McpClient session for a whole run.
// See Mcp/McpTenderClient.cs's remarks.
builder.Services.AddSingleton(new McpTenderClientOptions(mcpApiKey));
builder.Services.AddHttpClient<McpTenderClient>().AddStandardResilienceHandler();
builder.Services.AddScoped<IMcpTenderClient>(sp => sp.GetRequiredService<McpTenderClient>());

// RAG — Singleton: chunks are embedded once at startup (below) and held for the process lifetime.
builder.Services.AddSingleton<IEmbeddingClient>(new OpenAiEmbeddingClient(openAiApiKey));
builder.Services.AddSingleton<IEligibilityIndex, InMemoryEligibilityIndex>();

// LLM + Slack — stateless beyond the injected HttpClient, default Transient lifetime is fine.
// Resilience is added explicitly per client (not via ConfigureHttpClientDefaults — see
// ServiceDefaults/Extensions.cs) so AnthropicClient can get a longer timeout than the standard
// handler's default (30s total-request, 10s per-attempt) without affecting every other client.
// LLM completions routinely exceed that default, especially AnalysisRunner's larger
// structured-output call (MaxTokens=2048 over a prompt containing every resolved handoff in the
// lookback window) — observed live as a Polly TimeoutRejectedException on POST /analyze-now.
builder.Services.AddHttpClient<AnthropicClient>(client =>
{
    client.BaseAddress = new Uri("https://api.anthropic.com/");
    client.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
    if (!string.IsNullOrWhiteSpace(anthropicApiKey))
    {
        client.DefaultRequestHeaders.Add("x-api-key", anthropicApiKey);
    }
}).AddStandardResilienceHandler(options =>
{
    options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(60);
    options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(120);
    // Must be >= 2x AttemptTimeout.Timeout, or HttpStandardResilienceOptions' own validation
    // throws at startup.
    options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(130);
});
builder.Services.AddHttpClient<SlackNotifier>(client =>
{
    if (!string.IsNullOrWhiteSpace(slackWebhookUrl))
    {
        client.BaseAddress = new Uri(slackWebhookUrl);
    }
}).AddStandardResilienceHandler();
// No BaseAddress — response_url (used to hide the clicked button and show a confirmation line)
// is a full, one-time-use absolute URL Slack supplies per interaction, different every time.
builder.Services.AddHttpClient<SlackInteractionHandler>().AddStandardResilienceHandler();

// Loop stages + runner — Scoped, so a single run's object graph (including the shared
// McpTenderClient above) is fully independent from any other concurrent/subsequent run.
builder.Services.AddScoped<DiscoverStage>();
builder.Services.AddScoped<ClassifyStage>();
builder.Services.AddScoped<VerifyStage>();
builder.Services.AddScoped<PersistStage>();
builder.Services.AddScoped<HandoffStage>();
builder.Services.AddScoped<LoopRunner>();

// The self-improvement ("hill-climbing") outer loop — same Scoped-per-run pattern as LoopRunner,
// just triggered far less often (see Analysis/AnalysisBackgroundWorker.cs).
builder.Services.AddScoped<AnalysisRunner>();

builder.Services.AddHostedService<LoopBackgroundWorker>();
builder.Services.AddHostedService<AnalysisBackgroundWorker>();

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

// Manual/fallback decision-recording path — same DecisionUpdater logic the real Slack button
// clicks use (POST /slack/interactions below), reachable directly (e.g. via curl) without
// needing Slack interactivity configured at all. See PromptProposalRecord's doc comment for
// why the self-improvement outer loop depends on a real HumanDecision value existing at all.
app.MapGet("/decisions/{tenderId}/{decision}", async (
    string tenderId, string decision, ITenderStateStore stateStore, CancellationToken cancellationToken) =>
{
    var recorded = await RecordDecisionAsync(stateStore, tenderId, decision, note: null, cancellationToken);
    return recorded is null
        ? Results.NotFound($"No tender review record found for '{tenderId}'.")
        : Results.Content(
            $"Recorded: {recorded.HumanDecision} for tender {tenderId} at {recorded.HumanDecidedAt:u}. You can close this tab.",
            "text/plain");
});

app.MapPost("/decisions/{tenderId}", async (
    string tenderId, DecisionRequest request, ITenderStateStore stateStore, CancellationToken cancellationToken) =>
{
    var recorded = await RecordDecisionAsync(stateStore, tenderId, request.Decision, request.Note, cancellationToken);
    return recorded is null
        ? Results.NotFound($"No tender review record found for '{tenderId}'.")
        : Results.Ok(recorded);
});

app.MapPost("/analyze-now", async (AnalysisRunner runner, CancellationToken cancellationToken) =>
{
    var result = await runner.TryRunOnceAsync(cancellationToken);
    return result.Started ? Results.Accepted(value: result) : Results.Conflict("An analysis run is already in progress.");
});

app.MapGet("/proposals", async (ITenderStateStore stateStore, CancellationToken cancellationToken) =>
    Results.Ok(await stateStore.GetProposalsAsync(take: 20, cancellationToken)));

// The real Slack Bid/No-Bid button callback (HandoffStage.BuildBlocks) — see
// SlackInteractionHandler's doc comment for what's required on Slack's own side (App
// interactivity enabled, Request URL pointed here, SLACK_SIGNING_SECRET supplied) before a
// button click actually reaches this endpoint at all.
app.MapPost("/slack/interactions", (SlackInteractionHandler handler, HttpRequest request, CancellationToken cancellationToken) =>
    handler.HandleAsync(request, cancellationToken));

app.Run();
return 0;

static async Task<TenderReviewRecord?> RecordDecisionAsync(
    ITenderStateStore stateStore, string tenderId, string decision, string? note, CancellationToken cancellationToken)
{
    var canonicalDecision = DecisionUpdater.ParseCanonicalDecision(decision);
    if (canonicalDecision is null)
    {
        return null;
    }

    var existing = await stateStore.GetAsync(tenderId, cancellationToken);
    if (existing is null)
    {
        return null;
    }

    var updated = DecisionUpdater.ApplyDecision(existing, canonicalDecision, note, DateTimeOffset.UtcNow);
    await stateStore.UpsertAsync(updated, cancellationToken);
    return updated;
}

static async Task IndexQualificationDocsAsync(WebApplication app)
{
    // AppContext.BaseDirectory (the running assembly's own directory), not
    // app.Environment.ContentRootPath — the latter is the process's working directory at
    // startup, which disagrees between local dev (dotnet run's cwd is the source project
    // directory, so "../../data" happens to reach the repo root) and a container (WORKDIR is
    // the publish output itself, so "../../" overshoots past the filesystem root). See
    // LoopOrchestrator.csproj's <Content Include> item, which bundles this folder into the
    // project's own build/publish output — and therefore into its container image — so
    // AppContext.BaseDirectory finds it consistently everywhere.
    var qualificationDocsPath = Path.Combine(AppContext.BaseDirectory, "data", "qualification-docs");

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

/// <summary>Body for POST /decisions/{tenderId} — the GET variant carries the decision in the
/// route instead, since it has to be a plain clickable link from Slack.</summary>
internal sealed record DecisionRequest(string Decision, string? Note);
