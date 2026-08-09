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
    public void TriagePrompt_MatchesPromptBookDoc()
    {
        AssertPromptMatchesDoc(PromptBook.TriageSystemPrompt, PromptBook.TriageVersion, codeBlockIndex: 0);
    }

    [Fact]
    public void VerifierPrompt_MatchesPromptBookDoc()
    {
        AssertPromptMatchesDoc(PromptBook.VerifierSystemPrompt, PromptBook.VerifierVersion, codeBlockIndex: 1);
    }

    [Fact]
    public void HandoffPrompt_MatchesPromptBookDoc()
    {
        AssertPromptMatchesDoc(PromptBook.HandoffSystemPrompt, PromptBook.HandoffVersion, codeBlockIndex: 2);
    }

    [Fact]
    public void EachPrompt_StartsWithItsVersionCommentLine()
    {
        Assert.StartsWith("# " + PromptBook.TriageVersion, PromptBook.TriageSystemPrompt);
        Assert.StartsWith("# " + PromptBook.VerifierVersion, PromptBook.VerifierSystemPrompt);
        Assert.StartsWith("# " + PromptBook.HandoffVersion, PromptBook.HandoffSystemPrompt);
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
        // code block — those are the actual prompt bodies, in triage/verifier/handoff order.
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
