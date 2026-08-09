namespace LoopOrchestrator.Rag;

/// <summary>
/// Local (in-memory) implementation of IEligibilityIndex: embed all chunks once at startup, hold
/// vectors in memory, cosine similarity for QueryAsync. Re-embedding on every startup is
/// acceptable at this PoC scale (10-20 chunks) per docs/task-2/02-rag-and-data.md — no caching.
///
/// If the embedding client isn't configured (no OPENAI_API_KEY), IndexAsync logs a warning and
/// leaves the index empty rather than throwing — QueryAsync then always returns an empty result,
/// which VerifyStage treats as "insufficient information", mapping to verdict "uncertain" rather
/// than a crash. That's consistent with the guardrail itself: escalate rather than decide silently.
///
/// The same degrade-to-empty behavior applies when the key IS configured but the call itself
/// fails (quota exhausted, rate limited, bad key, transient network error, etc.) — this runs once
/// at startup (see Program.cs), so an unhandled exception here would crash the whole process
/// before it ever serves a request, taking Discover/Classify/idempotency down with it even though
/// none of those need embeddings. Confirmed live: a real `HTTP 429 insufficient_quota` from OpenAI
/// did exactly that before this try/catch was added.
/// </summary>
public sealed class InMemoryEligibilityIndex(IEmbeddingClient embeddingClient, ILogger<InMemoryEligibilityIndex> logger)
    : IEligibilityIndex
{
    private IReadOnlyList<DocumentChunk> _indexed = [];

    public async Task IndexAsync(IEnumerable<DocumentChunk> chunks, CancellationToken cancellationToken)
    {
        if (!embeddingClient.IsConfigured)
        {
            logger.LogWarning(
                "OPENAI_API_KEY not configured — eligibility index left empty. Stage 3 verification " +
                "will always return verdict 'uncertain' until this is supplied.");
            _indexed = [];
            return;
        }

        try
        {
            var indexed = new List<DocumentChunk>();
            foreach (var chunk in chunks)
            {
                var embedding = await embeddingClient.EmbedAsync(chunk.Text, cancellationToken);
                indexed.Add(chunk with { Embedding = embedding });
            }

            _indexed = indexed;
            logger.LogInformation("Indexed {Count} qualification-doc chunks for eligibility retrieval.", indexed.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _indexed = [];
            logger.LogError(ex,
                "Embedding call failed while building the eligibility index — leaving it empty. " +
                "Stage 3 verification will return 'uncertain' until this is resolved and the app restarts.");
        }
    }

    public async Task<IReadOnlyList<DocumentChunk>> QueryAsync(string text, int topK, CancellationToken cancellationToken)
    {
        if (_indexed.Count == 0)
        {
            return [];
        }

        var queryEmbedding = await embeddingClient.EmbedAsync(text, cancellationToken);

        return _indexed
            .Select(chunk => (chunk, score: CosineSimilarity(chunk.Embedding!, queryEmbedding)))
            .OrderByDescending(x => x.score)
            .Take(topK)
            .Select(x => x.chunk)
            .ToList();
    }

    internal static double CosineSimilarity(float[] a, float[] b)
    {
        double dot = 0, normA = 0, normB = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }

        // + 1e-9 guards against a division by zero for an all-zero vector rather than returning NaN.
        return dot / (Math.Sqrt(normA) * Math.Sqrt(normB) + 1e-9);
    }
}
