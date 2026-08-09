using LoopOrchestrator.Rag;

namespace LoopOrchestrator.Tests;

public class MarkdownChunkerTests
{
    [Fact]
    public void ChunkMarkdownFile_SplitsOnH2Headings()
    {
        const string text = """
            # Title

            Intro paragraph, not under any ## heading.

            ## First section

            First section body.

            ## Second section

            Second section body.
            """;

        var chunks = MarkdownChunker.ChunkMarkdownFile("doc.md", text);

        Assert.Equal(3, chunks.Count);
        Assert.All(chunks, c => Assert.Equal("doc.md", c.SourceFile));
        Assert.Contains("First section", chunks[1].Text);
        Assert.Contains("Second section", chunks[2].Text);
    }

    [Fact]
    public void ChunkMarkdownFile_NoHeadings_FallsBackToWholeFileAsOneChunk()
    {
        const string text = "Just a plain paragraph with no headings at all.";

        var chunks = MarkdownChunker.ChunkMarkdownFile("plain.md", text);

        Assert.Single(chunks);
        Assert.Equal(text, chunks[0].Text);
    }

    [Fact]
    public void ChunkMarkdownFile_EmptyFile_ReturnsNoChunks()
    {
        var chunks = MarkdownChunker.ChunkMarkdownFile("empty.md", "   ");
        Assert.Empty(chunks);
    }

    [Fact]
    public void ChunkMarkdownFile_IdsAreUniquePerChunk()
    {
        const string text = "## A\nbody a\n## B\nbody b\n## C\nbody c";
        var chunks = MarkdownChunker.ChunkMarkdownFile("doc.md", text);
        Assert.Equal(chunks.Select(c => c.Id).Distinct().Count(), chunks.Count);
    }
}
