using McpServer.Telemetry;

namespace McpServer.Tests;

public class ToolTelemetryTests
{
    [Fact]
    public async Task TraceAsync_ReturnsInnerResult_OnSuccess()
    {
        var result = await ToolTelemetry.TraceAsync(
            "test_tool",
            new Dictionary<string, object?> { ["a"] = 1 },
            () => Task.FromResult(42));

        Assert.Equal(42, result);
    }

    [Fact]
    public async Task TraceAsync_PropagatesException_DoesNotSwallow()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ToolTelemetry.TraceAsync<int>(
                "test_tool",
                new Dictionary<string, object?>(),
                () => throw new InvalidOperationException("boom")));
    }
}
