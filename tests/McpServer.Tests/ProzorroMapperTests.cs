using McpServer.Tenders;
using McpServer.Tenders.Prozorro;

namespace McpServer.Tests;

public class ProzorroMapperTests
{
    [Fact]
    public void ToSummary_MapsUpstreamFields_IncludingSnakeCaseTitleEnAndCpvClassification()
    {
        var tender = MakeTender(
            titleEn: "Office supplies delivery",
            value: new ProzorroValue(150000m, "UAH"),
            procuringEntity: new ProzorroProcuringEntity("Kyiv City Council", new ProzorroAddress("Kyiv")),
            items: [new ProzorroItem(new ProzorroClassification("30192000-1", "Office supplies"))],
            tenderNumber: "UA-2026-01-01-000001-a");

        var summary = ProzorroMapper.ToSummary(tender);

        Assert.Equal(tender.Id, summary.Id);
        Assert.Equal("Office supplies delivery", summary.TitleEn);
        Assert.Equal("Office supplies", summary.CpvCategory);
        Assert.Equal(150000m, summary.Value!.Amount);
        Assert.Equal("UAH", summary.Value.Currency);
        Assert.Equal("Kyiv City Council", summary.ProcuringEntity.Name);
        Assert.Equal("Kyiv", summary.ProcuringEntity.Region);
        Assert.Equal("https://prozorro.gov.ua/tender/UA-2026-01-01-000001-a", summary.SourceUrl);
    }

    [Fact]
    public void ToSummary_FallsBackToInternalId_WhenTenderNumberMissing()
    {
        var tender = MakeTender(tenderNumber: null);

        var summary = ProzorroMapper.ToSummary(tender);

        Assert.Equal($"https://prozorro.gov.ua/tender/{tender.Id}", summary.SourceUrl);
    }

    [Fact]
    public void ToSummary_CpvCategory_FallsBackToClassificationId_WhenDescriptionMissing()
    {
        var tender = MakeTender(items: [new ProzorroItem(new ProzorroClassification("30192000-1", null))]);

        var summary = ProzorroMapper.ToSummary(tender);

        Assert.Equal("30192000-1", summary.CpvCategory);
    }

    [Fact]
    public void ToSummary_CpvCategory_IsNull_WhenNoItems()
    {
        var tender = MakeTender(items: null);

        var summary = ProzorroMapper.ToSummary(tender);

        Assert.Null(summary.CpvCategory);
    }

    [Fact]
    public void ToDetail_UsesEligibilityCriteria_WhenPresent()
    {
        var tender = MakeTender(eligibilityCriteria: "Must hold ISO 9001.", description: "General requirements apply.");

        var detail = ProzorroMapper.ToDetail(tender);

        Assert.Equal("Must hold ISO 9001.", detail.EligibilityText);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ToDetail_FallsBackToDescription_WhenEligibilityCriteriaMissingOrBlank(string? eligibilityCriteria)
    {
        var tender = MakeTender(eligibilityCriteria: eligibilityCriteria, description: "General requirements apply.");

        var detail = ProzorroMapper.ToDetail(tender);

        Assert.Equal("General requirements apply.", detail.EligibilityText);
    }

    [Fact]
    public void ToDetail_EligibilityText_IsEmpty_WhenNeitherFieldIsPresent()
    {
        var tender = MakeTender(eligibilityCriteria: null, description: null);

        var detail = ProzorroMapper.ToDetail(tender);

        Assert.Equal(string.Empty, detail.EligibilityText);
    }

    [Fact]
    public void ToDetail_MapsTenderId_FromTenderNumber_NotFromInternalId()
    {
        var tender = MakeTender(id: "internal-hex-id", tenderNumber: "UA-2020-03-17-000090-a");

        var detail = ProzorroMapper.ToDetail(tender);

        Assert.Equal("UA-2020-03-17-000090-a", detail.TenderId);
        Assert.Equal("internal-hex-id", detail.Id); // unchanged — TenderId is additive, not a replacement
    }

    [Fact]
    public void ToDetail_MapsProcurementMethodAndCategory_WhenPresent()
    {
        var tender = MakeTender(procurementMethod: "open", mainProcurementCategory: "services");

        var detail = ProzorroMapper.ToDetail(tender);

        Assert.Equal("open", detail.ProcurementMethod);
        Assert.Equal("services", detail.MainProcurementCategory);
    }

    [Fact]
    public void ToDetail_MainProcurementCategory_IsNull_WhenAbsentFromUpstream()
    {
        // Confirmed live against the real API: genuinely absent on legacy tender records —
        // must map to null, never default to empty string or a guessed value.
        var tender = MakeTender(mainProcurementCategory: null);

        var detail = ProzorroMapper.ToDetail(tender);

        Assert.Null(detail.MainProcurementCategory);
    }

    [Fact]
    public void ToDetail_MapsItems_FullyPopulated()
    {
        var tender = MakeTender(items: [MakeItem(
            id: "item-1", description: "Комп'ютерна техніка", unitName: "штука", quantity: 5,
            region: "Львівська область", locality: "Львів")]);

        var detail = ProzorroMapper.ToDetail(tender);

        var item = Assert.Single(detail.Items);
        Assert.Equal("item-1", item.Id);
        Assert.Equal("Комп'ютерна техніка", item.Description);
        Assert.Equal("штука", item.Unit!.Name);
        Assert.Equal(5, item.Quantity);
        Assert.Equal("Львівська область", item.DeliveryAddress!.Region);
        Assert.Equal("Львів", item.DeliveryAddress.Locality);
    }

    [Fact]
    public void ToDetail_Items_DefaultsToEmptyList_WhenUpstreamItemsIsNull()
    {
        var tender = MakeTender(items: null);

        var detail = ProzorroMapper.ToDetail(tender);

        Assert.Empty(detail.Items);
    }

    [Fact]
    public void ToDetail_ItemDeliveryAddress_IsNull_WhenUpstreamDeliveryAddressIsNull()
    {
        // Confirmed live: deliveryAddress can be entirely null on some (older/cancelled) tenders.
        var tender = MakeTender(items: [MakeItem(region: null, locality: null)]);

        var detail = ProzorroMapper.ToDetail(tender);

        Assert.Null(Assert.Single(detail.Items).DeliveryAddress);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ToDetail_ItemLocality_CanBeNullOrEmpty_WhileRegionIsPopulated(string? locality)
    {
        // Confirmed live: locality can independently be null OR an empty string even when
        // region is populated — both are real, distinct upstream shapes, neither should crash
        // or get silently coalesced into the other.
        var tender = MakeTender(items: [MakeItem(region: "Харківська область", locality: locality)]);

        var detail = ProzorroMapper.ToDetail(tender);

        var deliveryAddress = Assert.Single(detail.Items).DeliveryAddress!;
        Assert.Equal("Харківська область", deliveryAddress.Region);
        Assert.Equal(locality, deliveryAddress.Locality);
    }

    [Fact]
    public void ToDetail_ItemUnit_IsNull_WhenUpstreamUnitIsNull()
    {
        var tender = MakeTender(items: [MakeItem(unitName: null)]);

        var detail = ProzorroMapper.ToDetail(tender);

        Assert.Null(Assert.Single(detail.Items).Unit);
    }

    [Fact]
    public void ToDetail_MapsMultipleItems_InOrder()
    {
        var tender = MakeTender(items: [MakeItem(id: "item-1"), MakeItem(id: "item-2"), MakeItem(id: "item-3")]);

        var detail = ProzorroMapper.ToDetail(tender);

        Assert.Equal(["item-1", "item-2", "item-3"], detail.Items.Select(i => i.Id));
    }

    private static ProzorroTender MakeTender(
        string id = "id-1",
        string title = "Title",
        string? titleEn = null,
        string? description = "desc",
        string? eligibilityCriteria = null,
        ProzorroValue? value = null,
        ProzorroProcuringEntity? procuringEntity = null,
        ProzorroPeriod? tenderPeriod = null,
        string status = "active.tendering",
        List<ProzorroItem>? items = null,
        string? tenderNumber = "UA-1",
        string? procurementMethod = null,
        string? mainProcurementCategory = null) =>
        new(
            Id: id,
            Title: title,
            TitleEn: titleEn,
            Description: description,
            EligibilityCriteria: eligibilityCriteria,
            Value: value,
            ProcuringEntity: procuringEntity,
            TenderPeriod: tenderPeriod,
            Status: status,
            Items: items,
            TenderNumber: tenderNumber,
            ProcurementMethod: procurementMethod,
            MainProcurementCategory: mainProcurementCategory);

    private static ProzorroItem MakeItem(
        string? id = "item-1",
        string? description = "Item description",
        string? unitName = "штука",
        double? quantity = 1,
        string? region = "Київська область",
        string? locality = "Київ") =>
        new(
            Classification: null,
            Id: id,
            Description: description,
            Unit: unitName is null ? null : new ProzorroUnit(unitName),
            Quantity: quantity,
            DeliveryAddress: region is null && locality is null ? null : new ProzorroDeliveryAddress(region, locality));
}
