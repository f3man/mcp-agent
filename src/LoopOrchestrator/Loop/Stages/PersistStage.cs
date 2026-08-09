using LoopOrchestrator.State;
using LoopOrchestrator.Telemetry;

namespace LoopOrchestrator.Loop.Stages;

/// <summary>
/// Stage 4 — Persist. Thin traced wrapper over ITenderStateStore.UpsertAsync — called twice per
/// tender in a normal flow (once after Classify/Verify to record the verdict, again after
/// Handoff to record the final HandedOff/Verified status + HumanDecision) since UpsertAsync is
/// idempotent (TableUpdateMode.Replace on a fixed RowKey), each call fully overwrites the row
/// with the current known state rather than requiring a diff/patch.
/// </summary>
public sealed class PersistStage(ITenderStateStore stateStore)
{
    public Task RunAsync(TenderReviewRecord record, CancellationToken cancellationToken) =>
        LoopTelemetry.TraceStageAsync("persist", record.TenderId, async () =>
        {
            await stateStore.UpsertAsync(record, cancellationToken);
            return true; // TraceStageAsync is generic; no meaningful return value here.
        });
}
