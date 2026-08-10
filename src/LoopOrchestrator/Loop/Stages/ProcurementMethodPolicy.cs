namespace LoopOrchestrator.Loop.Stages;

/// <summary>
/// "limited" procurement method tenders (confirmed live against the real Prozorro API as one of
/// the real observed values, alongside "open"/"selective") are invite-only/pre-selected
/// procedures — an outside supplier can't realistically bid on one regardless of eligibility, so
/// there's no point spending a Handoff LLM call or Slack notification on it. Pure, no I/O,
/// mirrors HandoffPolicy's style (see HandoffStage.cs) — trivially unit-testable.
/// </summary>
public static class ProcurementMethodPolicy
{
    private const string Limited = "limited";

    public static bool IsExcluded(string? procurementMethod) =>
        string.Equals(procurementMethod, Limited, StringComparison.OrdinalIgnoreCase);
}
