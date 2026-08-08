using System.Diagnostics;

namespace McpServer.Telemetry;

/// <summary>
/// Wraps every MCP tool invocation in its own Activity so the Aspire Dashboard (or Azure Monitor,
/// once deployed) shows one span per tool call, tagged with the tool name and its parameters —
/// this is what turns "incoming request" logging into structured, queryable telemetry instead of
/// plain text lines. The source name below must be registered for export via
/// ServiceDefaults' <c>.AddSource("TenderWatch.McpServer.Tools")</c> (kept as a literal string
/// there rather than a reference to <see cref="SourceName"/>, to avoid a circular project
/// reference — keep the two in sync by convention).
/// </summary>
public static class ToolTelemetry
{
    public const string SourceName = "TenderWatch.McpServer.Tools";

    private static readonly ActivitySource Source = new(SourceName);

    public static async Task<T> TraceAsync<T>(
        string toolName,
        IReadOnlyDictionary<string, object?> parameters,
        Func<Task<T>> action)
    {
        using var activity = Source.StartActivity(toolName, ActivityKind.Server);
        activity?.SetTag("mcp.tool.name", toolName);
        foreach (var (key, value) in parameters)
        {
            activity?.SetTag($"mcp.tool.param.{key}", value);
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
}
