using System.Security.Cryptography;
using System.Text;
using LoopOrchestrator.Notifications;

namespace LoopOrchestrator.Tests;

/// <summary>
/// SlackInteractionHandler.IsValidSignature is the security-critical check protecting POST
/// /slack/interactions — without it, anyone who finds the URL could spoof a Bid/No-Bid decision.
/// Computes a genuinely valid HMAC-SHA256 signature the same way Slack does, per their documented
/// algorithm (https://api.slack.com/authentication/verifying-requests-from-slack), rather than
/// asserting only the trivial "garbage in, rejected" cases.
/// </summary>
public class SlackSignatureVerificationTests
{
    private const string SigningSecret = "test-signing-secret";

    [Fact]
    public void IsValidSignature_AcceptsAGenuinelyValidSignature()
    {
        var now = DateTimeOffset.UtcNow;
        var timestamp = now.ToUnixTimeSeconds();
        const string body = "payload=%7B%22foo%22%3A%22bar%22%7D";

        var signature = ComputeSignature(timestamp, body, SigningSecret);

        Assert.True(SlackInteractionHandler.IsValidSignature(signature, timestamp.ToString(), body, SigningSecret, now));
    }

    [Fact]
    public void IsValidSignature_RejectsWrongSigningSecret()
    {
        var now = DateTimeOffset.UtcNow;
        var timestamp = now.ToUnixTimeSeconds();
        const string body = "payload=abc";

        var signature = ComputeSignature(timestamp, body, "a-different-secret");

        Assert.False(SlackInteractionHandler.IsValidSignature(signature, timestamp.ToString(), body, SigningSecret, now));
    }

    [Fact]
    public void IsValidSignature_RejectsTamperedBody()
    {
        var now = DateTimeOffset.UtcNow;
        var timestamp = now.ToUnixTimeSeconds();
        var signature = ComputeSignature(timestamp, "payload=original", SigningSecret);

        // Same signature, but the body Slack "sent" was altered after signing — must fail.
        Assert.False(SlackInteractionHandler.IsValidSignature(signature, timestamp.ToString(), "payload=tampered", SigningSecret, now));
    }

    [Fact]
    public void IsValidSignature_RejectsStaleTimestamp_EvenIfSignatureIsOtherwiseValid()
    {
        var requestTime = DateTimeOffset.UtcNow.AddMinutes(-10); // older than the 5-minute window
        var timestamp = requestTime.ToUnixTimeSeconds();
        const string body = "payload=abc";
        var signature = ComputeSignature(timestamp, body, SigningSecret);

        Assert.False(SlackInteractionHandler.IsValidSignature(signature, timestamp.ToString(), body, SigningSecret, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void IsValidSignature_RejectsMissingSignatureHeader()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.False(SlackInteractionHandler.IsValidSignature(null, now.ToUnixTimeSeconds().ToString(), "payload=abc", SigningSecret, now));
    }

    [Fact]
    public void IsValidSignature_RejectsUnparseableTimestamp()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.False(SlackInteractionHandler.IsValidSignature("v0=whatever", "not-a-number", "payload=abc", SigningSecret, now));
    }

    [Theory]
    [InlineData("payload=%7B%22actions%22%3A%5B%7B%22value%22%3A%22bid_abc%22%7D%5D%7D",
        """{"actions":[{"value":"bid_abc"}]}""")]
    [InlineData("token=xyz&payload=%7B%22foo%22%3A1%7D&extra=1", """{"foo":1}""")]
    public void ExtractPayloadField_DecodesTheUrlEncodedPayloadValue(string rawBody, string expectedJson)
    {
        Assert.Equal(expectedJson, SlackInteractionHandler.ExtractPayloadField(rawBody));
    }

    [Fact]
    public void ExtractPayloadField_ReturnsNull_WhenNoPayloadFieldPresent()
    {
        Assert.Null(SlackInteractionHandler.ExtractPayloadField("token=xyz&other=1"));
    }

    [Theory]
    [InlineData("bid_abc123", "Bid", "abc123")]
    [InlineData("nobid_abc123", "NoBid", "abc123")]
    [InlineData("bid_", "Bid", "")]
    public void ParseActionValue_SplitsKnownPrefixes(string value, string expectedDecision, string expectedTenderId)
    {
        var (decision, tenderId) = SlackInteractionHandler.ParseActionValue(value);

        Assert.Equal(expectedDecision, decision);
        Assert.Equal(expectedTenderId, tenderId);
    }

    [Fact]
    public void ParseActionValue_ReturnsNulls_ForUnrecognizedValue()
    {
        var (decision, tenderId) = SlackInteractionHandler.ParseActionValue("something_else");

        Assert.Null(decision);
        Assert.Null(tenderId);
    }

    /// <summary>The exact algorithm Slack itself uses: v0=HMAC-SHA256(signingSecret, "v0:{ts}:{body}"), hex.</summary>
    private static string ComputeSignature(long timestamp, string body, string signingSecret)
    {
        var baseString = $"v0:{timestamp}:{body}";
        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(signingSecret), Encoding.UTF8.GetBytes(baseString));
        return "v0=" + Convert.ToHexString(hash).ToLowerInvariant();
    }
}
