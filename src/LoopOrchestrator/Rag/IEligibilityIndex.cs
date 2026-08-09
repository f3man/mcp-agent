namespace LoopOrchestrator.Rag;

/// <summary>
/// Verbatim from docs/task-2/02-rag-and-data.md. In scope for this increment: the in-memory
/// implementation only (InMemoryEligibilityIndex). The "azure-search" cloud variant (same
/// interface, Azure AI Search-backed) is explicitly deferred — selecting it later via
/// VECTOR_STORE=azure-search should require zero changes to Stage 3 code, only a different DI
/// registration for this interface.
/// </summary>
public interface IEligibilityIndex
{
    Task IndexAsync(IEnumerable<DocumentChunk> chunks, CancellationToken cancellationToken);
    Task<IReadOnlyList<DocumentChunk>> QueryAsync(string text, int topK, CancellationToken cancellationToken);
}
