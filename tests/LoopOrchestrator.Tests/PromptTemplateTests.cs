using System.Text.RegularExpressions;
using LoopOrchestrator.Llm;

namespace LoopOrchestrator.Tests;

/// <summary>
/// Asserts PromptBook.cs's three system prompts are byte-for-byte in sync with the published
/// docs/prompt-book.md deliverable, so a docs-only edit (or a code-only edit) that lets the two
/// drift apart fails CI instead of silently shipping mismatched documentation.
/// </summary>
public class PromptTemplateTests
{
    [Fact]
    public void AssessPrompt_MatchesPromptBookDoc()
    {
        AssertPromptMatchesDoc(PromptBook.AssessSystemPrompt, PromptBook.AssessVersion, codeBlockIndex: 0);
    }

    [Fact]
    public void HandoffPrompt_MatchesPromptBookDoc()
    {
        AssertPromptMatchesDoc(PromptBook.HandoffSystemPrompt, PromptBook.HandoffVersion, codeBlockIndex: 1);
    }

    [Fact]
    public void AnalysisPrompt_MatchesPromptBookDoc()
    {
        AssertPromptMatchesDoc(PromptBook.AnalysisSystemPrompt, PromptBook.AnalysisVersion, codeBlockIndex: 2);
    }

    [Fact]
    public void EachPrompt_StartsWithItsVersionCommentLine()
    {
        Assert.StartsWith("# " + PromptBook.AssessVersion, PromptBook.AssessSystemPrompt);
        Assert.StartsWith("# " + PromptBook.HandoffVersion, PromptBook.HandoffSystemPrompt);
        Assert.StartsWith("# " + PromptBook.AnalysisVersion, PromptBook.AnalysisSystemPrompt);
    }

    private static void AssertPromptMatchesDoc(string constPrompt, string expectedVersion, int codeBlockIndex)
    {
        var lines = constPrompt.Replace("\r\n", "\n").Split('\n');
        var versionLine = lines[0];
        var body = string.Join('\n', lines[1..]).Trim();

        Assert.Equal("# " + expectedVersion, versionLine);

        var docBody = ExtractSystemPromptCodeBlocks()[codeBlockIndex].Trim();
        Assert.Equal(docBody, body);
    }

    private static List<string> ExtractSystemPromptCodeBlocks()
    {
        var docPath = Path.Combine(FindRepoRoot(), "docs", "prompt-book.md");
        var markdown = File.ReadAllText(docPath).Replace("\r\n", "\n");

        // Each of the three "**System prompt** (`# ... vN`):" sections is followed by one fenced
        // code block — those are the actual prompt bodies, in assess/handoff/analysis order.
        var matches = Regex.Matches(markdown, @"\*\*System prompt\*\*[^\n]*:\n```\n(.*?)\n```", RegexOptions.Singleline);
        Assert.Equal(3, matches.Count);
        return matches.Select(m => m.Groups[1].Value).ToList();
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "TenderWatch.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Could not locate repo root (TenderWatch.slnx) from " + AppContext.BaseDirectory);
    }
}
