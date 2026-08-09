namespace LoopOrchestrator.Loop;

/// <summary>Verbatim config table from docs/task-2/01-loop-orchestrator.md, plus MaxTendersPerRun
/// for the prompt book's "sanity-check limit" guardrail (not given a specific number by the
/// spec — defaulted here to something comfortably above McpServer's own bounded ~150-tender
/// upstream cache, so it only ever trips on a genuine anomaly, not normal operation).</summary>
public sealed record LoopOptions(
    int LoopIntervalMinutes,
    decimal HandoffValueThreshold,
    int MaxTendersPerRun)
{
    public const int DefaultLoopIntervalMinutes = 360;
    public const decimal DefaultHandoffValueThreshold = 500_000m;
    public const int DefaultMaxTendersPerRun = 50;
}
