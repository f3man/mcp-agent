using System.Text.Encodings.Web;
using System.Text.Json;
using LoopOrchestrator.Loop.Stages;
using LoopOrchestrator.Mcp;

namespace LoopOrchestrator.Tests;

/// <summary>
/// HandoffStage.BuildBlocks assembles the deterministic parts of the Slack Block Kit message
/// (tender id, formatted value/deadline/location, recommendation label/emoji, the two
/// bid/no-bid buttons) around the LLM-generated `brief`. Asserted via JSON serialization since
/// the blocks are anonymous objects — same "check the deterministic half" philosophy as
/// HandoffMessageContentTests.cs for BuildUserMessage.
/// </summary>
public class HandoffBlocksTests
{
    // System.Text.Json escapes non-ASCII (Cyrillic, emoji) as \uXXXX by default — irrelevant for
    // what Slack actually receives (its JSON parser decodes those escapes identically either
    // way), but it defeats a plain string.Contains check in a test. Relaxed encoding here is a
    // test-only convenience, not a change to what HandoffStage/SlackNotifier actually send.
    private static readonly JsonSerializerOptions Options = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    [Fact]
    public void BuildBlocks_HeaderIncludesCategoryEmojiAndShortTitle()
    {
        // ⭐ (U+2B50) rather than an astral-plane emoji (e.g. 🛣️, U+1F6E3) — those get validly
        // rendered as 🛣-style surrogate-pair escapes even under relaxed JSON
        // encoding, which is correct JSON (Slack's parser handles it identically) but defeats a
        // literal Assert.Contains on the raw character. Not a product bug, just a test nuance.
        var json = BlocksJson(Tender(), Brief(categoryEmoji: "⭐", shortTitle: "Ремонт доріг"));

        Assert.Contains("⭐ Новий тендер: Ремонт доріг", json);
    }

    [Fact]
    public void BuildBlocks_FieldsIncludeTenderIdFormattedValueAndDeadline()
    {
        var tender = Tender(tenderId: "UA-2020-03-17-000090-a", amount: 909601m, deadline: new DateTimeOffset(2026, 2, 8, 0, 0, 0, TimeSpan.Zero));

        var json = BlocksJson(tender, Brief());

        Assert.Contains("UA-2020-03-17-000090-a", json);
        Assert.Contains("909,601", json); // thousands separator, no decimals
        Assert.Contains("грн", json); // UAH translated for a Ukrainian-language message
        Assert.Contains("08.02.2026", json); // dd.MM.yyyy
    }

    [Fact]
    public void BuildBlocks_FieldsFallBackToInternalId_WhenTenderIdMissing()
    {
        var tender = Tender(tenderId: null) with { Id = "internal-hex-id" };

        var json = BlocksJson(tender, Brief());

        Assert.Contains("internal-hex-id", json);
    }

    [Fact]
    public void BuildBlocks_TenderIdField_LinksToProzorroPortal()
    {
        // Tender()'s defaults: tenderId "UA-1", SourceUrl "https://prozorro.gov.ua/tender/UA-1" —
        // the field must show just the tenderId text but link to the real portal page.
        var json = BlocksJson(Tender(), Brief());

        Assert.Contains("<https://prozorro.gov.ua/tender/UA-1|UA-1>", json);
    }

    [Fact]
    public void BuildBlocks_Location_PrefersItemDeliveryRegion_OverProcuringEntityRegion()
    {
        var tender = Tender(procuringEntityRegion: "Київ") with
        {
            Items = [new TenderItemInfo("item-1", "desc", null, null, new DeliveryAddressInfo("Львівська область", "Львів"))],
        };

        var json = BlocksJson(tender, Brief());

        Assert.Contains("Львівська область", json);
    }

    [Fact]
    public void BuildBlocks_Location_FallsBackToProcuringEntityRegion_WhenNoItemDeliveryAddress()
    {
        var tender = Tender(procuringEntityRegion: "Одеська область");

        var json = BlocksJson(tender, Brief());

        Assert.Contains("Одеська область", json);
    }

    [Theory]
    [InlineData("uncertain", "⚠️", "Потрібний огляд експертом")]
    [InlineData("eligible", "✅", "Рекомендується подати заявку")]
    [InlineData("ineligible", "❌", "Не рекомендується")] // defensive default — never actually reaches Handoff in practice
    public void RecommendationFor_MapsVerdictToEmojiAndLabel(string verdict, string expectedEmoji, string expectedLabel)
    {
        var (emoji, label) = HandoffStage.RecommendationFor(verdict);

        Assert.Equal(expectedEmoji, emoji);
        Assert.Equal(expectedLabel, label);
    }

    [Fact]
    public void BuildBlocks_EmbedsWhateverRecommendationItIsGiven()
    {
        var (emoji, label) = HandoffStage.RecommendationFor("uncertain");
        var json = JsonSerializer.Serialize(
            HandoffStage.BuildBlocks(Tender(), Brief(), emoji, label, "some rationale", NonLocalhostUrl), Options);

        Assert.Contains(emoji, json);
        Assert.Contains(label, json);
    }

    [Fact]
    public void BuildBlocks_KeyQuestionsSection_Present_WhenQuestionsExist()
    {
        var json = BlocksJson(Tender(), Brief(keyQuestions: ["Питання 1?", "Питання 2?"]));

        Assert.Contains("Ключові моменти для перевірки менеджером", json);
        Assert.Contains("Питання 1?", json);
        Assert.Contains("Питання 2?", json);
    }

    [Fact]
    public void BuildBlocks_KeyQuestionsSection_Absent_WhenNoQuestions()
    {
        var json = BlocksJson(Tender(), Brief(keyQuestions: []));

        Assert.DoesNotContain("Ключові моменти", json);
    }

    [Fact]
    public void BuildBlocks_ActionsContainBidAndNoBidButtons_WithCorrectValueAndActionId()
    {
        var json = BlocksJson(Tender(id: "abc123"), Brief());

        Assert.Contains("\"action_id\":\"tender_bid_action\"", json);
        Assert.Contains("\"value\":\"bid_abc123\"", json);
        Assert.Contains("\"action_id\":\"tender_nobid_action\"", json);
        Assert.Contains("\"value\":\"nobid_abc123\"", json);
    }

    [Fact]
    public void BuildBlocks_ShowsLinksInsteadOfButtons_WhenPublicBaseUrlIsLocalhost()
    {
        var json = BlocksJson(Tender(id: "abc123"), Brief(), publicBaseUrl: "http://localhost:5250");

        Assert.Contains("<http://localhost:5250/decisions/abc123/bid|", json);
        Assert.Contains("<http://localhost:5250/decisions/abc123/nobid|", json);
        Assert.DoesNotContain("\"action_id\":\"tender_bid_action\"", json);
        Assert.DoesNotContain("\"type\":\"actions\"", json);
    }

    [Theory]
    [InlineData("http://localhost:5250", true)]
    [InlineData("https://localhost", true)]
    [InlineData("http://127.0.0.1:5250", true)]
    [InlineData("http://[::1]:5250", true)]
    [InlineData("https://loop-orchestrator.mangohill-8bec81a9.germanywestcentral.azurecontainerapps.io", false)]
    [InlineData(null, false)]
    [InlineData("not a url", false)]
    [InlineData("", false)]
    public void IsLocalhost_DetectsLoopbackAddresses(string? publicBaseUrl, bool expected)
    {
        Assert.Equal(expected, HandoffStage.IsLocalhost(publicBaseUrl));
    }

    // Any real, Slack-reachable address works for the "keep buttons" default — this one is
    // deliberately not localhost so BlocksJson's default matches this file's pre-existing
    // (button-asserting) tests without them each having to say so explicitly.
    private const string NonLocalhostUrl = "https://loop-orchestrator.example.com";

    private static string BlocksJson(TenderDetail tender, HandoffBriefJsonResult brief, string? publicBaseUrl = NonLocalhostUrl) =>
        JsonSerializer.Serialize(
            HandoffStage.BuildBlocks(tender, brief, "⚠️", "Потрібний огляд експертом", "some rationale", publicBaseUrl), Options);

    private static HandoffBriefJsonResult Brief(
        string categoryEmoji = "📦",
        string shortTitle = "Тестовий тендер",
        string description = "Опис тендеру",
        string rationale = "Обґрунтування",
        IReadOnlyList<string>? keyQuestions = null) =>
        new(categoryEmoji, shortTitle, description, rationale, keyQuestions ?? ["Питання?"]);

    private static TenderDetail Tender(
        string id = "internal-id",
        string? tenderId = "UA-1",
        decimal amount = 100_000m,
        DateTimeOffset? deadline = null,
        string? procuringEntityRegion = "Kyiv") => new(
        Id: id,
        Title: "Test tender",
        TitleEn: null,
        CpvCategory: null,
        Value: new MoneyAmount(amount, "UAH"),
        ProcuringEntity: new ProcuringEntityInfo("Test Entity", procuringEntityRegion),
        TenderPeriod: new TenderPeriodInfo(null, deadline ?? DateTimeOffset.UtcNow.AddDays(7)),
        Status: "active",
        SourceUrl: "https://prozorro.gov.ua/tender/UA-1",
        EligibilityText: "some eligibility text",
        TenderId: tenderId,
        ProcurementMethod: "open",
        MainProcurementCategory: "services",
        Items: []);
}
