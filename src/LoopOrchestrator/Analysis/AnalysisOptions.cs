namespace LoopOrchestrator.Analysis;

/// <summary>Config for the self-improvement ("hill-climbing") outer loop — see AnalysisRunner.cs.
/// Mirrors Loop/LoopOptions.cs's style; a separate, much slower cadence than the main loop since
/// this is occasional/expensive analysis, not per-tender work.</summary>
public sealed record AnalysisOptions(
    int AnalysisIntervalHours,
    int AnalysisLookbackDays,
    int MinDisagreementsForProposal)
{
    public const int DefaultAnalysisIntervalHours = 168; // weekly
    public const int DefaultAnalysisLookbackDays = 30;
    public const int DefaultMinDisagreementsForProposal = 3;
}
