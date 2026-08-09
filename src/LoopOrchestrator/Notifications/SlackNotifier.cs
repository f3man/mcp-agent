using System.Net.Http.Json;

namespace LoopOrchestrator.Notifications;

/// <summary>
/// Posts to a Slack incoming webhook — no package needed, just a plain POST of {"text": "..."}.
/// The webhook URL itself is the credential (Slack incoming webhooks have no separate auth
/// header), so treat SLACK_WEBHOOK_URL with the same care as an API key: never logged, never put
/// in a span tag. HttpClient.BaseAddress is set to the webhook URL in Program.cs; this class never
/// sees or logs it directly.
/// </summary>
public sealed class SlackNotifier(HttpClient httpClient, ILogger<SlackNotifier> logger)
{
    public async Task SendAsync(string message, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsJsonAsync(string.Empty, new { text = message }, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            logger.LogError("Slack webhook returned {StatusCode}: {Body}", (int)response.StatusCode, body);
            throw new InvalidOperationException($"Slack webhook returned {(int)response.StatusCode}.");
        }
    }
}
