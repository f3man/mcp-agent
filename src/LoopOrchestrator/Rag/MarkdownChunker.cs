using System.Text.RegularExpressions;

namespace LoopOrchestrator.Rag;

/// <summary>
/// Splits a qualification doc into one chunk per `## ` (H2) section — the natural semantic unit
/// for docs written as "a few paragraphs each" (per docs/task-2/02-rag-and-data.md), so no
/// sliding-window/overlap logic is needed at this PoC scale (10-20 chunks total across 4 files).
/// </summary>
public static partial class MarkdownChunker
{
    [GeneratedRegex(@"(?=^## )", RegexOptions.Multiline)]
    private static partial Regex HeadingSplitter();

    public static IReadOnlyList<DocumentChunk> ChunkMarkdownFile(string sourceFile, string text)
    {
        var sections = HeadingSplitter().Split(text)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToList();

        // Whole-file fallback if the doc has no `## ` headings at all — one chunk, not zero.
        if (sections.Count == 0)
        {
            var whole = text.Trim();
            return whole.Length == 0
                ? []
                : [new DocumentChunk($"{sourceFile}#0", sourceFile, whole)];
        }

        return sections
            .Select((section, i) => new DocumentChunk($"{sourceFile}#{i}", sourceFile, section))
            .ToList();
    }
}
