namespace LoopOrchestrator.Rag;

/// <summary>
/// Kept as its own seam (rather than calling OpenAI's EmbeddingClient directly from
/// InMemoryEligibilityIndex) so a future Azure OpenAI implementation — matching
/// docs/task-2/02-rag-and-data.md's literal tech-stack choice — is a config-only DI swap, not a
/// rewrite of the index or Stage 3 code.
/// </summary>
public interface IEmbeddingClient
{
    /// <summary>True if this client is actually configured to call a real embedding API (an API
    /// key was supplied). InMemoryEligibilityIndex checks this before indexing/querying so a
    /// missing OPENAI_API_KEY degrades to "index unavailable" rather than crashing the process —
    /// see VerifyStage for how that maps to an "uncertain" verdict instead of a hard failure.</summary>
    bool IsConfigured { get; }

    Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken);
}
