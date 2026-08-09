using LoopOrchestrator.Rag;

namespace LoopOrchestrator.Tests;

public class CosineSimilarityTests
{
    [Fact]
    public void CosineSimilarity_IdenticalVectors_ReturnsOne()
    {
        var v = new[] { 1f, 2f, 3f };
        Assert.Equal(1.0, InMemoryEligibilityIndex.CosineSimilarity(v, v), precision: 6);
    }

    [Fact]
    public void CosineSimilarity_OrthogonalVectors_ReturnsZero()
    {
        var a = new[] { 1f, 0f };
        var b = new[] { 0f, 1f };
        Assert.Equal(0.0, InMemoryEligibilityIndex.CosineSimilarity(a, b), precision: 6);
    }

    [Fact]
    public void CosineSimilarity_OppositeVectors_ReturnsNegativeOne()
    {
        var a = new[] { 1f, 0f };
        var b = new[] { -1f, 0f };
        Assert.Equal(-1.0, InMemoryEligibilityIndex.CosineSimilarity(a, b), precision: 6);
    }

    [Fact]
    public async Task QueryAsync_RanksChunksByRelevance()
    {
        // Fake embeddings: 2-D vectors chosen so similarity to the query is unambiguous.
        var embeddingClient = new FakeEmbeddingClient(text => text switch
        {
            "query" => [1f, 0f],
            "closest" => [0.9f, 0.1f],
            "middling" => [0.5f, 0.5f],
            "farthest" => [0f, 1f],
            _ => [0f, 0f],
        });

        var index = new InMemoryEligibilityIndex(embeddingClient, Microsoft.Extensions.Logging.Abstractions.NullLogger<InMemoryEligibilityIndex>.Instance);
        await index.IndexAsync(
            [
                new DocumentChunk("1", "f.md", "farthest"),
                new DocumentChunk("2", "f.md", "middling"),
                new DocumentChunk("3", "f.md", "closest"),
            ],
            CancellationToken.None);

        var result = await index.QueryAsync("query", topK: 3, CancellationToken.None);

        Assert.Equal(["closest", "middling", "farthest"], result.Select(c => c.Text));
    }

    private sealed class FakeEmbeddingClient(Func<string, float[]> embed) : IEmbeddingClient
    {
        public bool IsConfigured => true;
        public Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken) => Task.FromResult(embed(text));
    }
}
