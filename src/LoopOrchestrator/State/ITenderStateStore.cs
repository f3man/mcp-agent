namespace LoopOrchestrator.State;

/// <summary>
/// One implementation only (TableStorageStateStore) — Azurite emulator locally via Aspire, real
/// Azure Table Storage in the cloud, same code path both times per docs/task-2/02-rag-and-data.md.
/// </summary>
public interface ITenderStateStore
{
    /// <summary>All tender ids already tracked, regardless of status — the sole authoritative
    /// idempotency guard for Stage 1 (Discover). A date-window filter is only ever an efficiency
    /// heuristic on top of this, never a substitute for it.</summary>
    Task<HashSet<string>> GetSeenTenderIdsAsync(CancellationToken cancellationToken);

    Task UpsertAsync(TenderReviewRecord record, CancellationToken cancellationToken);

    Task<TenderReviewRecord?> GetAsync(string tenderId, CancellationToken cancellationToken);

    /// <summary>Timestamp of the last fully-successful run (every discovered tender reached a
    /// terminal status). Null if no run has ever completed successfully.</summary>
    Task<DateTimeOffset?> GetLastSuccessfulRunAtAsync(CancellationToken cancellationToken);

    Task SetLastSuccessfulRunAtAsync(DateTimeOffset timestamp, CancellationToken cancellationToken);
}
