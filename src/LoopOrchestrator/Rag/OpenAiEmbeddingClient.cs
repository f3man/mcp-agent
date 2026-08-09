using OpenAI.Embeddings;

namespace LoopOrchestrator.Rag;

/// <summary>
/// Calls the plain OpenAI embeddings API (not Azure OpenAI) — same text-embedding-3-small model
/// docs/task-2/02-rag-and-data.md specifies, but needs only an API key rather than a provisioned
/// Azure OpenAI resource + model deployment. See IEmbeddingClient's remarks for the swap story.
/// </summary>
public sealed class OpenAiEmbeddingClient : IEmbeddingClient
{
    private const string Model = "text-embedding-3-small";
    private readonly EmbeddingClient? _client;

    public OpenAiEmbeddingClient(string? apiKey)
    {
        _client = string.IsNullOrWhiteSpace(apiKey) ? null : new EmbeddingClient(Model, apiKey);
    }

    public bool IsConfigured => _client is not null;

    public async Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken)
    {
        if (_client is null)
        {
            throw new InvalidOperationException(
                "OpenAiEmbeddingClient is not configured (OPENAI_API_KEY missing) — check IsConfigured before calling EmbedAsync.");
        }

        var result = await _client.GenerateEmbeddingAsync(text, cancellationToken: cancellationToken);
        return result.Value.ToFloats().ToArray();
    }
}
