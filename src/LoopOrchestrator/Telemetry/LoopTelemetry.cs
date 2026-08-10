using System.Diagnostics;

namespace LoopOrchestrator.Telemetry;

/// <summary>
/// Wraps each loop run, each of its 5 stages, and each LLM call in its own Activity — mirroring
/// McpServer/Telemetry/ToolTelemetry.cs's exact shape. Because Activities created while
/// Activity.Current is set automatically nest as parent/child, a full run shows up as ONE
/// connected trace (loop-run → stage → llm-call), and because ServiceDefaults already enables
/// AddHttpClientInstrumentation(), the underlying MCP tool calls McpTenderClient makes show up as
/// further child spans of McpServer's own ToolTelemetry spans via standard W3C trace-context
/// propagation — no extra wiring needed for that part.
///
/// SourceName must be registered for export via ServiceDefaults' .AddSource(...), alongside the
/// existing "TenderWatch.McpServer.Tools" source (kept as a literal string there to avoid a
/// circular project reference — see ServiceDefaults/Extensions.cs).
/// </summary>
public static class LoopTelemetry
{
    public const string SourceName = "TenderWatch.LoopOrchestrator.Stages";

    // Generous enough to be useful in the dashboard without bloating spans/exporter payloads.
    private const int MaxTagLength = 2000;

    private static readonly ActivitySource Source = new(SourceName);

    public static Activity? StartRunActivity() => Source.StartActivity("loop-run", ActivityKind.Internal);

    /// <summary>Root span for one Analysis/AnalysisRunner.cs pass — the self-improvement
    /// ("hill-climbing") outer loop, a separate, much-less-frequent trace root from loop-run.</summary>
    public static Activity? StartAnalysisRunActivity() => Source.StartActivity("analysis-run", ActivityKind.Internal);

    public static async Task<T> TraceStageAsync<T>(string stageName, string? tenderId, Func<Task<T>> action)
    {
        using var activity = Source.StartActivity(stageName, ActivityKind.Internal);
        if (tenderId is not null)
        {
            activity?.SetTag("tender.id", tenderId);
        }

        try
        {
            var result = await action().ConfigureAwait(false);
            activity?.SetStatus(ActivityStatusCode.Ok);
            return result;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.SetTag("error.type", ex.GetType().FullName);
            throw;
        }
    }

    /// <summary>Start a child activity for one LLM call. Caller is responsible for disposing it
    /// (via `using`) and calling SetLlmOutput once the result is known.</summary>
    public static Activity? StartLlmCallActivity(string promptVersion, string input)
    {
        var activity = Source.StartActivity("llm-call", ActivityKind.Client);
        activity?.SetTag("llm.prompt.version", promptVersion);
        activity?.SetTag("llm.input", Truncate(input));
        return activity;
    }

    public static void SetLlmOutput(Activity? activity, string output) =>
        activity?.SetTag("llm.output", Truncate(output));

    private static string Truncate(string s) => s.Length <= MaxTagLength ? s : s[..MaxTagLength] + "…(truncated)";
}
