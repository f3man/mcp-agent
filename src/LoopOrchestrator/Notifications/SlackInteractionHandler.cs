using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LoopOrchestrator.State;

namespace LoopOrchestrator.Notifications;

/// <summary>
/// Handles Slack's block_actions interactivity callback (POST /slack/interactions, see
/// Program.cs) — the receiving side of HandoffStage's Bid/No-Bid buttons. Requires the owning
/// Slack App to have "Interactivity &amp; Shortcuts" enabled with its Request URL pointed at this
/// endpoint's public address (PUBLIC_BASE_URL + "/slack/interactions"), plus
/// SLACK_SIGNING_SECRET — a separate secret from SLACK_WEBHOOK_URL (Slack issues one signing
/// secret per App, used to verify a request actually came from Slack, not to send anything). Both
/// are one-time setup steps in Slack's own admin console, outside this codebase's control.
///
/// HandleAsync only does fast, no-I/O work (signature verification, payload parsing) before
/// acking — Slack requires that ack within 3 seconds. The actual decision recording (a Table
/// Storage read + write) and the Slack message update run afterward, detached, in
/// ProcessDecisionAsync — which updates the message in Slack twice: immediately to an "in
/// progress" state (stripping the buttons right away), then to the real result once the decision
/// is actually recorded.
/// </summary>
public sealed class SlackInteractionHandler(HttpClient httpClient, ITenderStateStore stateStore, IConfiguration configuration, ILogger<SlackInteractionHandler> logger)
{
    // Slack's own replay-attack guidance: reject requests whose timestamp is older than this.
    private static readonly TimeSpan MaxRequestAge = TimeSpan.FromMinutes(5);

    public async Task<IResult> HandleAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        var signingSecret = configuration["SLACK_SIGNING_SECRET"];
        if (string.IsNullOrWhiteSpace(signingSecret))
        {
            // Fail CLOSED, not open — an unset signing secret must never mean "accept anything
            // unverified"; that would let anyone who finds this URL spoof a Bid/No-Bid decision.
            logger.LogWarning("SLACK_SIGNING_SECRET not configured — refusing all Slack interaction callbacks.");
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        // Read the raw body BEFORE any form-parsing — the signature is computed over the exact
        // raw bytes Slack sent; Request.ReadFormAsync() would consume the stream first and leave
        // nothing for a byte-accurate check.
        using var reader = new StreamReader(request.Body);
        var rawBody = await reader.ReadToEndAsync(cancellationToken);

        var signatureHeader = request.Headers.TryGetValue("X-Slack-Signature", out var sig) ? sig.ToString() : null;
        var timestampHeader = request.Headers.TryGetValue("X-Slack-Request-Timestamp", out var ts) ? ts.ToString() : null;
        if (!IsValidSignature(signatureHeader, timestampHeader, rawBody, signingSecret, DateTimeOffset.UtcNow))
        {
            logger.LogWarning("Rejected a /slack/interactions request with an invalid, missing, or stale signature.");
            return Results.Unauthorized();
        }

        var payloadJson = ExtractPayloadField(rawBody);
        if (payloadJson is null)
        {
            return Results.BadRequest("Missing 'payload' field.");
        }

        var payload = JsonSerializer.Deserialize<SlackBlockActionsPayload>(payloadJson);
        var action = payload?.Actions?.FirstOrDefault();
        if (action?.Value is null)
        {
            return Results.BadRequest("No action value present.");
        }

        var (canonicalDecision, tenderId) = ParseActionValue(action.Value);
        if (canonicalDecision is null || tenderId is null)
        {
            logger.LogWarning("Unrecognized Slack action value: {Value}", action.Value);
            return Results.BadRequest("Unrecognized action value.");
        }

        // Everything from here is slow I/O (a Table Storage read + write, then one or two
        // response_url POSTs) — Slack requires the ack below within 3 seconds, so none of it can
        // be awaited first. Fired detached (discarded, not awaited) with its own
        // CancellationToken.None: `cancellationToken` (HttpContext.RequestAborted) gets cancelled
        // once this method returns and the response is sent, but this continuation needs to keep
        // running after that.
        _ = ProcessDecisionAsync(tenderId, canonicalDecision, payload?.ResponseUrl, payload?.Message?.Blocks);

        // Empty 200 within Slack's 3s window acknowledges receipt — separate from (and not
        // dependent on) the response_url updates ProcessDecisionAsync sends afterward.
        return Results.Ok();
    }

    /// <summary>Does the actual decision recording + the two-stage Slack message update, after
    /// HandleAsync has already acked the original request. Update Slack immediately with an
    /// "in progress" message (also strips the buttons right away, preventing a double-click),
    /// then swap it for the real result once the decision is actually recorded.</summary>
    private async Task ProcessDecisionAsync(
        string tenderId, string canonicalDecision, string? responseUrl, List<JsonElement>? originalBlocks)
    {
        if (responseUrl is not null)
        {
            await TryPostMessageUpdateAsync(tenderId, responseUrl, originalBlocks, "⏳ Обробка вашого запиту...");
        }

        try
        {
            var existing = await stateStore.GetAsync(tenderId, CancellationToken.None);
            if (existing is null)
            {
                logger.LogWarning("Slack interaction for unknown tender {TenderId} — ignoring.", tenderId);
                if (responseUrl is not null)
                {
                    await TryPostMessageUpdateAsync(
                        tenderId, responseUrl, originalBlocks, "⚠️ Не вдалося обробити запит — тендер не знайдено.");
                }
                return;
            }

            var updated = DecisionUpdater.ApplyDecision(existing, canonicalDecision, note: null, DateTimeOffset.UtcNow);
            await stateStore.UpsertAsync(updated, CancellationToken.None);

            logger.LogInformation("Tender {TenderId}: decision {Decision} recorded via Slack button click.", tenderId, canonicalDecision);

            if (responseUrl is not null)
            {
                var confirmationText = canonicalDecision == HumanDecisionStatus.Bid
                    ? "✅ Ваша відповідь: Буду брати участь"
                    : "❌ Ваша відповідь: Відхилено";
                await TryPostMessageUpdateAsync(tenderId, responseUrl, originalBlocks, confirmationText);
            }
        }
        catch (Exception ex)
        {
            // Covers e.g. a Table Storage error — logged at Error (not Warning, unlike the
            // response_url-specific failures below) since this means the decision itself may not
            // have been recorded, not just that the cosmetic Slack update failed.
            logger.LogError(ex, "Tender {TenderId}: failed to record decision {Decision} from Slack.", tenderId, canonicalDecision);
            if (responseUrl is not null)
            {
                await TryPostMessageUpdateAsync(tenderId, responseUrl, originalBlocks, "⚠️ Не вдалося обробити запит. Спробуйте ще раз.");
            }
        }
    }

    /// <summary>Best-effort — logged and swallowed, never throws. The decision (once recorded)
    /// is already durable in the state store regardless of whether any of these cosmetic Slack
    /// updates succeed; response_url is short-lived (~30 minutes, a handful of uses), so a late
    /// or repeated click could legitimately fail here without that being a real problem.</summary>
    private async Task TryPostMessageUpdateAsync(
        string tenderId, string responseUrl, List<JsonElement>? originalBlocks, string sectionText)
    {
        try
        {
            await PostMessageUpdateAsync(responseUrl, originalBlocks, sectionText, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Tender {TenderId}: updating the Slack message via response_url failed.", tenderId);
        }
    }

    /// <summary>Removes the "actions" block (the two buttons, if still present) from the
    /// original message and appends one mrkdwn section, then asks Slack to replace the original
    /// message with this edited version in place. Shared by the interim "processing" update and
    /// every final (success/not-found/error) update — same shape, different text.</summary>
    private async Task PostMessageUpdateAsync(
        string responseUrl, List<JsonElement>? originalBlocks, string sectionText, CancellationToken cancellationToken)
    {
        var blocks = new List<object>();
        if (originalBlocks is not null)
        {
            blocks.AddRange(originalBlocks.Where(b => !IsActionsBlock(b)).Select(b => (object)b));
        }
        blocks.Add(new { type = "section", text = new { type = "mrkdwn", text = sectionText } });

        using var response = await httpClient.PostAsJsonAsync(
            responseUrl, new { replace_original = true, text = sectionText, blocks }, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private static bool IsActionsBlock(JsonElement block) =>
        block.TryGetProperty("type", out var type) && type.GetString() == "actions";

    /// <summary>internal, not private, so SlackSignatureVerificationTests.cs can exercise the
    /// real algorithm (including a genuinely-computed valid signature) without any ASP.NET Core
    /// dependency — pure given its inputs, "now" included, so staleness is deterministically
    /// testable rather than racing the real clock.</summary>
    internal static bool IsValidSignature(
        string? signatureHeader, string? timestampHeader, string rawBody, string signingSecret, DateTimeOffset now)
    {
        if (signatureHeader is null || timestampHeader is null || !long.TryParse(timestampHeader, out var timestamp))
        {
            return false;
        }

        var requestAge = now - DateTimeOffset.FromUnixTimeSeconds(timestamp);
        if (requestAge.Duration() > MaxRequestAge)
        {
            return false; // stale — reject rather than risk a replayed request.
        }

        var baseString = $"v0:{timestamp}:{rawBody}";
        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(signingSecret), Encoding.UTF8.GetBytes(baseString));
        var expectedSignature = "v0=" + Convert.ToHexString(hash).ToLowerInvariant();

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expectedSignature), Encoding.UTF8.GetBytes(signatureHeader));
    }

    /// <summary>Slack posts application/x-www-form-urlencoded with one field, "payload", whose
    /// value is URL-encoded JSON — parsed by hand rather than via Request.ReadFormAsync(), which
    /// would consume the body before the raw-bytes signature check above can run.</summary>
    internal static string? ExtractPayloadField(string rawBody)
    {
        foreach (var pair in rawBody.Split('&'))
        {
            var parts = pair.Split('=', 2);
            if (parts.Length == 2 && parts[0] == "payload")
            {
                return Uri.UnescapeDataString(parts[1].Replace('+', ' '));
            }
        }
        return null;
    }

    /// <summary>Mirrors the "bid_{tenderId}"/"nobid_{tenderId}" value shape
    /// HandoffStage.BuildBlocks encodes into each button.</summary>
    internal static (string? Decision, string? TenderId) ParseActionValue(string value)
    {
        if (value.StartsWith("bid_", StringComparison.Ordinal))
        {
            return (HumanDecisionStatus.Bid, value["bid_".Length..]);
        }
        if (value.StartsWith("nobid_", StringComparison.Ordinal))
        {
            return (HumanDecisionStatus.NoBid, value["nobid_".Length..]);
        }
        return (null, null);
    }

    private sealed record SlackBlockActionsPayload(
        [property: JsonPropertyName("actions")] List<SlackAction>? Actions,
        [property: JsonPropertyName("response_url")] string? ResponseUrl,
        [property: JsonPropertyName("message")] SlackMessage? Message);

    private sealed record SlackAction(
        [property: JsonPropertyName("action_id")] string? ActionId,
        [property: JsonPropertyName("value")] string? Value);

    private sealed record SlackMessage(
        [property: JsonPropertyName("blocks")] List<JsonElement>? Blocks);
}
