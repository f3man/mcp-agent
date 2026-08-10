using System.Net.Http.Json;

namespace LoopOrchestrator.Notifications;

/// <summary>
/// Posts to a Slack incoming webhook — no package needed. The webhook URL itself is the
/// credential (Slack incoming webhooks have no separate auth header), so treat
/// SLACK_WEBHOOK_URL with the same care as an API key: never logged, never put in a span tag.
/// HttpClient.BaseAddress is set to the webhook URL in Program.cs; this class never sees or logs
/// it directly.
///
/// Interactive elements (buttons with action_id, see HandoffStage.BuildBlocks) work over a plain
/// incoming webhook exactly as well as over chat.postMessage — Slack routes block_actions
/// callbacks based on which Slack App owns the message's origin (every incoming webhook belongs
/// to one), not on which API posted it. What's actually required for the buttons to DO anything
/// is that Slack App having "Interactivity &amp; Shortcuts" turned on with a Request URL pointing
/// at POST /slack/interactions — a one-time setup step in the Slack App's own admin console,
/// outside this codebase's control, plus the resulting Signing Secret supplied as
/// SLACK_SIGNING_SECRET (see Program.cs).
/// </summary>
public sealed class SlackNotifier(HttpClient httpClient, ILogger<SlackNotifier> logger)
{
    public Task SendAsync(string message, CancellationToken cancellationToken) =>
        // unfurl_links: false — HandoffStage's brief embeds decision links elsewhere; even here,
        // where it no longer does, disabling link previews is harmless and keeps the behavior
        // uniform between this and SendBlocksAsync below.
        PostAsync(new { text = message, unfurl_links = false }, cancellationToken);

    /// <summary>Posts a real Slack Block Kit message. `fallbackText` is Slack's own required
    /// accessibility/notification-preview field — shown in push notifications and by clients that
    /// don't render blocks; `blocks` carries the actual rich content.</summary>
    public Task SendBlocksAsync(string fallbackText, IReadOnlyList<object> blocks, CancellationToken cancellationToken) =>
        PostAsync(new { text = fallbackText, blocks, unfurl_links = false }, cancellationToken);

    private async Task PostAsync(object payload, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsJsonAsync(string.Empty, payload, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            logger.LogError("Slack webhook returned {StatusCode}: {Body}", (int)response.StatusCode, body);
            throw new InvalidOperationException($"Slack webhook returned {(int)response.StatusCode}.");
        }
    }
}
