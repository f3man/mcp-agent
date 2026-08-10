using System.Globalization;
using System.Text.Json.Serialization;
using LoopOrchestrator.Llm;
using LoopOrchestrator.Mcp;
using LoopOrchestrator.Notifications;
using LoopOrchestrator.State;
using LoopOrchestrator.Telemetry;

namespace LoopOrchestrator.Loop.Stages;

/// <summary>
/// Pure, network-free escalation decision — kept separate from HandoffStage's LLM/Slack side
/// effects so it's trivially unit-testable (see HandoffThresholdTests.cs) without any fakes.
/// Value is assumed UAH throughout (matches the company profile and HANDOFF_VALUE_THRESHOLD's own
/// documented denomination); a missing tender value does not, by itself, trigger value-based
/// escalation — only verdict "uncertain" always escalates regardless of value.
/// </summary>
public static class HandoffPolicy
{
    public static bool ShouldEscalate(string verdict, MoneyAmount? value, decimal handoffValueThreshold) =>
        verdict switch
        {
            "uncertain" => true,
            "eligible" => value is not null && value.Amount > handoffValueThreshold,
            _ => false, // "ineligible" never escalates — persisted as Verified, no Slack noise.
        };
}

public sealed record HandoffOutcome(string FinalStatus, DateTimeOffset? HandoffSentAt);

/// <summary>
/// Stage 5 — Handoff. Escalates to Slack when HandoffPolicy.ShouldEscalate says so; otherwise logs
/// and skips (no notification noise for the obvious eligible-and-cheap-enough cases). Never calls
/// any tender-submission action — there isn't one exposed, and none should ever be added.
///
/// The Slack message is real Block Kit with interactive Bid/No-Bid buttons (tender_bid_action/
/// tender_nobid_action, value "bid_{tenderId}"/"nobid_{tenderId}"), handled by
/// POST /slack/interactions (see Program.cs) — clicking them requires the owning Slack App to
/// have "Interactivity &amp; Shortcuts" enabled with its Request URL pointed there, and
/// SLACK_SIGNING_SECRET supplied, both one-time setup steps outside this codebase's control.
/// Buttons only work at all if Slack's servers can reach PUBLIC_BASE_URL — when that resolves to
/// localhost (running locally, not deployed), IsLocalhost/BuildBlocks swap the buttons for plain
/// mrkdwn links to the existing GET /decisions/{tenderId}/{decision} fallback endpoint instead,
/// which a human's own browser on the same machine can follow even though Slack's servers can't
/// POST to it. Deterministic fields (tender id — linked to the tender's real Prozorro portal
/// page via SourceUrl, value, deadline, location, recommendation label/emoji) are assembled in
/// code from the verdict/TenderDetail directly — only the free-text parts (category emoji, short
/// title, description, rationale, key questions) come from the model, and only those need to be
/// in Ukrainian per the model's own instructions (PromptBook.HandoffSystemPrompt).
/// </summary>
public sealed class HandoffStage(
    AnthropicClient anthropicClient, SlackNotifier slackNotifier, IConfiguration configuration, ILogger<HandoffStage> logger)
{
    private const int MaxTokens = 1024;

    public Task<HandoffOutcome> RunAsync(
        TenderDetail tender, string verdict, string rationale, double relevanceScore, decimal handoffValueThreshold,
        CancellationToken cancellationToken) =>
        LoopTelemetry.TraceStageAsync("handoff", tender.Id, async () =>
        {
            if (!HandoffPolicy.ShouldEscalate(verdict, tender.Value, handoffValueThreshold))
            {
                logger.LogInformation(
                    "Tender {TenderId}: verdict={Verdict}, below handoff threshold — logged, no Slack notification.",
                    tender.Id, verdict);
                return new HandoffOutcome(TenderReviewStatus.Verified, HandoffSentAt: null);
            }

            var userMessage = BuildUserMessage(tender, verdict, rationale, relevanceScore);

            using var llmActivity = LoopTelemetry.StartLlmCallActivity(PromptBook.HandoffVersion, userMessage);
            var brief = await anthropicClient.CompleteStructuredAsync<HandoffBriefJsonResult>(
                PromptBook.HandoffSystemPrompt, userMessage, JsonSchemas.HandoffBrief, MaxTokens, cancellationToken);
            LoopTelemetry.SetLlmOutput(llmActivity, $"{brief.ShortTitle}: {brief.Description}");
            // Logged at Information, not just tagged on the trace span — the trace is only
            // inspectable from the Aspire Dashboard/Azure Monitor UI; this makes the actual
            // brief content (audience: whoever's watching stdout/structured logs) checkable the
            // same way ClassifyStage/VerifyStage's LLM outputs already are via LoopTelemetry.
            logger.LogInformation(
                "Tender {TenderId}: brief — {ShortTitle}: {Description} | {Rationale}",
                tender.Id, brief.ShortTitle, brief.Description, brief.Rationale);

            var (recommendationEmoji, recommendationLabel) = RecommendationFor(verdict);
            var publicBaseUrl = configuration["PUBLIC_BASE_URL"];
            var blocks = BuildBlocks(tender, brief, recommendationEmoji, recommendationLabel, rationale, publicBaseUrl);
            var fallbackText = $"{brief.CategoryEmoji} {brief.ShortTitle} — {recommendationLabel}";

            await slackNotifier.SendBlocksAsync(fallbackText, blocks, cancellationToken);

            logger.LogInformation("Tender {TenderId}: handed off to Slack (verdict={Verdict}).", tender.Id, verdict);
            return new HandoffOutcome(TenderReviewStatus.HandedOff, HandoffSentAt: DateTimeOffset.UtcNow);
        });

    /// <summary>Recommendation label/emoji are chosen in code from the verdict this stage
    /// already has, never left to the model — same reasoning as VerifyStage's citedClause
    /// enforcement: a confidence-adjacent decision shouldn't depend on the model consistently
    /// self-classifying it correctly every time.</summary>
    internal static (string Emoji, string Label) RecommendationFor(string verdict) => verdict switch
    {
        "uncertain" => ("⚠️", "Потрібний огляд експертом"),
        "eligible" => ("✅", "Рекомендується подати заявку"),
        _ => ("❌", "Не рекомендується"), // defensive default — HandoffPolicy never escalates "ineligible"
    };

    /// <summary>internal, not private, so HandoffMessageContentTests.cs can assert the new
    /// fields actually reach the LLM prompt without needing a live Anthropic call.</summary>
    internal static string BuildUserMessage(TenderDetail tender, string verdict, string rationale, double relevanceScore) =>
        $"""
         Tender ID: {tender.TenderId}
         Tender: {tender.Title}
         Value: {tender.Value?.Amount} {tender.Value?.Currency}
         Deadline: {tender.TenderPeriod.EndDate}
         Procurement method: {tender.ProcurementMethod}
         Category: {tender.MainProcurementCategory}
         Source: {tender.SourceUrl}
         Relevance score: {relevanceScore:0.00}
         Eligibility verdict: {verdict}
         Eligibility rationale: {rationale}
         Items:
         {FormatItems(tender.Items)}
         """;

    /// <summary>internal for the same testability reason as BuildUserMessage — asserts the
    /// deterministic parts (id/value/deadline/location/buttons-or-links) land correctly without a
    /// live LLM call standing in for `brief`.</summary>
    internal static List<object> BuildBlocks(
        TenderDetail tender, HandoffBriefJsonResult brief, string recommendationEmoji, string recommendationLabel,
        string rationale, string? publicBaseUrl)
    {
        // Always a link to the tender's real Prozorro portal page (tender.SourceUrl is already
        // exactly that — see ProzorroMapper.ToSourceUrl) — independent of the buttons-vs-links
        // decision below, since Prozorro's own portal is public regardless of where we run.
        var tenderIdText = tender.TenderId ?? tender.Id;
        var tenderIdField = $"*ID Тендеру:*\n<{tender.SourceUrl}|{tenderIdText}>";

        var blocks = new List<object>
        {
            new
            {
                type = "header",
                text = new { type = "plain_text", text = $"{brief.CategoryEmoji} Новий тендер: {brief.ShortTitle}", emoji = true },
            },
            new
            {
                type = "section",
                fields = new object[]
                {
                    new { type = "mrkdwn", text = tenderIdField },
                    new { type = "mrkdwn", text = $"*Вартість:*\n{FormatValue(tender.Value)}" },
                    new { type = "mrkdwn", text = $"*Дедлайн:*\n{FormatDeadline(tender.TenderPeriod.EndDate)}" },
                    new { type = "mrkdwn", text = $"*Місцезнаходження:*\n{ResolveLocation(tender)}" },
                },
            },
            new { type = "section", text = new { type = "mrkdwn", text = $"*Опис:*\n{brief.Description}" } },
            new { type = "divider" },
            new
            {
                type = "section",
                text = new { type = "mrkdwn", text = $"{recommendationEmoji} *Рекомендація: {recommendationLabel}*\n\n{brief.Rationale}" },
            },
        };

        if (brief.KeyQuestions.Count > 0)
        {
            var questionsText = string.Join("\n\n", brief.KeyQuestions.Select(q => $"• {q}"));
            blocks.Add(new
            {
                type = "section",
                text = new { type = "mrkdwn", text = $"*❓ Ключові моменти для перевірки менеджером:*\n\n{questionsText}" },
            });
        }

        blocks.Add(new { type = "divider" });

        if (IsLocalhost(publicBaseUrl))
        {
            // Slack's servers can't reach localhost to deliver a button click's callback — fall
            // back to plain mrkdwn links to the existing GET /decisions/{tenderId}/{decision}
            // manual/fallback endpoint (Program.cs), which a human's own browser on the same
            // machine can follow even though Slack's servers can't POST to it.
            blocks.Add(new
            {
                type = "section",
                text = new
                {
                    type = "mrkdwn",
                    text = $"<{publicBaseUrl}/decisions/{tender.Id}/bid|✅ Подати заявку>   " +
                            $"<{publicBaseUrl}/decisions/{tender.Id}/nobid|❌ Відмовитися>",
                },
            });
        }
        else
        {
            blocks.Add(new
            {
                type = "actions",
                elements = new object[]
                {
                    new
                    {
                        type = "button",
                        text = new { type = "plain_text", text = "✅ Подати заявку", emoji = true },
                        value = $"bid_{tender.Id}",
                        action_id = "tender_bid_action",
                        style = "primary",
                    },
                    new
                    {
                        type = "button",
                        text = new { type = "plain_text", text = "❌ Відмовитися", emoji = true },
                        value = $"nobid_{tender.Id}",
                        action_id = "tender_nobid_action",
                        style = "danger",
                    },
                },
            });
        }

        return blocks;
    }

    /// <summary>internal so HandoffBlocksTests.cs can exercise it directly. True for
    /// http(s)://localhost... and loopback IPs (127.0.0.1, ::1); false — never throws — for
    /// null, empty, or unparseable input, which keeps the (safer, always-functional-if-Slack-
    /// interactivity-is-configured) buttons as the default when we can't tell either way.</summary>
    internal static bool IsLocalhost(string? publicBaseUrl) =>
        publicBaseUrl is not null
        && Uri.TryCreate(publicBaseUrl, UriKind.Absolute, out var uri)
        && (uri.IsLoopback || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase));

    private static string FormatValue(MoneyAmount? value)
    {
        if (value is null)
        {
            return "не вказано";
        }

        var currency = value.Currency == "UAH" ? "грн" : value.Currency;
        return $"{value.Amount.ToString("N0", CultureInfo.InvariantCulture)} {currency}";
    }

    private static string FormatDeadline(DateTimeOffset? deadline) =>
        deadline?.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture) ?? "не вказано";

    /// <summary>Prefers the first item's actual delivery location (what a manager cares about —
    /// where the work/goods are, not where the buyer's office is registered); falls back to the
    /// procuring entity's own region when no item carries one.</summary>
    private static string ResolveLocation(TenderDetail tender)
    {
        var itemAddress = tender.Items.Select(i => i.DeliveryAddress).FirstOrDefault(a => a is not null);
        var region = itemAddress?.Region ?? tender.ProcuringEntity.Region;
        return string.IsNullOrWhiteSpace(region) ? "не вказано" : region;
    }

    private static string FormatItems(IReadOnlyList<TenderItemInfo> items) =>
        items.Count == 0
            ? "(none listed)"
            : string.Join('\n', items.Select(FormatItem));

    private static string FormatItem(TenderItemInfo item)
    {
        var unit = item.Unit?.Name;
        var quantityAndUnit = item.Quantity is { } quantity
            ? unit is null ? $"{quantity}" : $"{quantity} {unit}"
            : unit;
        var delivery = item.DeliveryAddress is { } address
            ? string.Join(", ", new[] { address.Region, address.Locality }.Where(s => !string.IsNullOrWhiteSpace(s)))
            : null;

        var parts = new List<string> { $"- {item.Description ?? item.Id}" };
        if (!string.IsNullOrWhiteSpace(quantityAndUnit))
        {
            parts.Add(quantityAndUnit!);
        }
        if (!string.IsNullOrWhiteSpace(delivery))
        {
            parts.Add($"delivery: {delivery}");
        }

        return string.Join(" — ", parts);
    }
}

/// <summary>internal, not private-nested, so HandoffMessageContentTests.cs can construct one
/// directly to exercise BuildBlocks without a live LLM call.</summary>
internal sealed record HandoffBriefJsonResult(
    [property: JsonPropertyName("categoryEmoji")] string CategoryEmoji,
    [property: JsonPropertyName("shortTitle")] string ShortTitle,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("rationale")] string Rationale,
    [property: JsonPropertyName("keyQuestions")] IReadOnlyList<string> KeyQuestions);
