using LoopOrchestrator.Loop.Stages;
using LoopOrchestrator.Mcp;

namespace LoopOrchestrator.Tests;

/// <summary>
/// HandoffStage.BuildUserMessage is the deterministic half of the Handoff stage — what the LLM
/// is given, as opposed to what it writes back (which can't be unit-tested deterministically;
/// see PromptBook.HandoffSystemPrompt's Ukrainian-output rule, verified live instead — see
/// docs/conclusions-2nd-iteration.md). This asserts the new tenderId/procurementMethod/
/// mainProcurementCategory/items fields actually reach the prompt.
/// </summary>
public class HandoffMessageContentTests
{
    [Fact]
    public void BuildUserMessage_IncludesTenderIdProcurementMethodAndCategory()
    {
        var tender = Tender(tenderId: "UA-2020-03-17-000090-a", procurementMethod: "open", mainProcurementCategory: "services");

        var message = HandoffStage.BuildUserMessage(tender, "uncertain", "some rationale", 0.8);

        Assert.Contains("UA-2020-03-17-000090-a", message);
        Assert.Contains("open", message);
        Assert.Contains("services", message);
    }

    [Fact]
    public void BuildUserMessage_IncludesItemDescriptionQuantityUnitAndDeliveryLocation()
    {
        var tender = Tender(items: [
            new TenderItemInfo("item-1", "Комп'ютерна техніка", new UnitInfo("штука"), 5,
                new DeliveryAddressInfo("Львівська область", "Львів")),
        ]);

        var message = HandoffStage.BuildUserMessage(tender, "eligible", "rationale", 0.9);

        Assert.Contains("Комп'ютерна техніка", message);
        Assert.Contains("5", message);
        Assert.Contains("штука", message);
        Assert.Contains("Львівська область", message);
        Assert.Contains("Львів", message);
    }

    [Fact]
    public void BuildUserMessage_MultipleItems_AllAppear()
    {
        var tender = Tender(items: [
            new TenderItemInfo("item-1", "First item", null, null, null),
            new TenderItemInfo("item-2", "Second item", null, null, null),
        ]);

        var message = HandoffStage.BuildUserMessage(tender, "eligible", "rationale", 0.9);

        Assert.Contains("First item", message);
        Assert.Contains("Second item", message);
    }

    [Fact]
    public void BuildUserMessage_NoItems_ShowsPlaceholder_NotBlankOrCrash()
    {
        var tender = Tender(items: []);

        var message = HandoffStage.BuildUserMessage(tender, "eligible", "rationale", 0.9);

        Assert.Contains("(none listed)", message);
    }

    [Fact]
    public void BuildUserMessage_ItemWithNoUnitOrDeliveryAddress_StillRendersDescription_NoCrash()
    {
        var tender = Tender(items: [new TenderItemInfo("item-1", "Bare item", Unit: null, Quantity: null, DeliveryAddress: null)]);

        var message = HandoffStage.BuildUserMessage(tender, "eligible", "rationale", 0.9);

        Assert.Contains("Bare item", message);
    }

    [Fact]
    public void BuildUserMessage_ItemWithNoDescription_FallsBackToItemId()
    {
        var tender = Tender(items: [new TenderItemInfo("item-42", Description: null, Unit: null, Quantity: null, DeliveryAddress: null)]);

        var message = HandoffStage.BuildUserMessage(tender, "eligible", "rationale", 0.9);

        Assert.Contains("item-42", message);
    }

    private static TenderDetail Tender(
        string tenderId = "UA-1",
        string? procurementMethod = "open",
        string? mainProcurementCategory = "services",
        IReadOnlyList<TenderItemInfo>? items = null) => new(
        Id: "internal-id",
        Title: "Test tender",
        TitleEn: null,
        CpvCategory: null,
        Value: new MoneyAmount(100_000m, "UAH"),
        ProcuringEntity: new ProcuringEntityInfo("Test Entity", "Kyiv"),
        TenderPeriod: new TenderPeriodInfo(null, DateTimeOffset.UtcNow.AddDays(7)),
        Status: "active",
        SourceUrl: "https://prozorro.gov.ua/tender/UA-1",
        EligibilityText: "some eligibility text",
        TenderId: tenderId,
        ProcurementMethod: procurementMethod,
        MainProcurementCategory: mainProcurementCategory,
        Items: items ?? []);
}
