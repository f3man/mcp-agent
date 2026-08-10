using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace LoopOrchestrator.Mcp;

/// <summary>Runtime configuration for <see cref="McpTenderClient"/>.</summary>
public sealed record McpTenderClientOptions(string ApiKey);

/// <summary>
/// Talks to McpServer purely as an external MCP client over Streamable HTTP — exactly the same
/// way Claude Desktop or MCP Inspector would, never in-process. The endpoint URI uses "mcp-server"
/// as its host — the logical Aspire resource name from AppHost.cs's `AddProject&lt;Projects.McpServer&gt;
/// ("mcp-server")` — so Aspire's service-discovery HttpClient handler (wired in
/// ServiceDefaults.AddServiceDefaults via ConfigureHttpClientDefaults) resolves it to the real
/// endpoint; no hardcoded URL, same code locally and in the cloud.
///
/// One McpClient session is created lazily on first use and reused for this instance's lifetime.
/// LoopRunner creates a fresh McpTenderClient per loop run (see Loop/LoopRunner.cs) rather than
/// holding one for the process lifetime, so a rarely-used, long-idle session (default loop
/// interval: 6 hours) never has to deal with reconnect/expiry logic.
/// </summary>
public sealed class McpTenderClient(HttpClient httpClient, McpTenderClientOptions options, ILogger<McpTenderClient> logger)
    : IMcpTenderClient, IAsyncDisposable
{
    private static readonly Uri Endpoint = new("http://mcp-server/mcp");
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private McpClient? _client;

    public async Task<IReadOnlyList<TenderSummary>> ListTendersAsync(
        string? category = null, string? region = null, string status = "active", int limit = 20,
        CancellationToken cancellationToken = default)
    {
        var arguments = new Dictionary<string, object?> { ["status"] = status, ["limit"] = limit };
        if (category is not null) arguments["category"] = category;
        if (region is not null) arguments["region"] = region;

        return await CallToolAsync<List<TenderSummary>>("list_tenders", arguments, cancellationToken) ?? [];
    }

    public async Task<IReadOnlyList<TenderSummary>> SearchTendersAsync(
        string keywords, int limit = 20, CancellationToken cancellationToken = default)
    {
        var arguments = new Dictionary<string, object?> { ["keywords"] = keywords, ["limit"] = limit };
        return await CallToolAsync<List<TenderSummary>>("search_tenders", arguments, cancellationToken) ?? [];
    }

    public Task<TenderDetail> GetTenderAsync(string tenderId, CancellationToken cancellationToken = default) =>
        CallToolAsync<TenderDetail>("get_tender", new Dictionary<string, object?> { ["tenderId"] = tenderId }, cancellationToken)!;

    public Task<CompanyProfileData> GetCompanyProfileAsync(CancellationToken cancellationToken = default) =>
        CallToolAsync<CompanyProfileData>("get_company_profile", new Dictionary<string, object?>(), cancellationToken)!;

    public async Task<IReadOnlyList<McpToolDescriptor>> ListAvailableToolsAsync(CancellationToken cancellationToken = default)
    {
        var client = await EnsureConnectedAsync(cancellationToken);
        var tools = await client.ListToolsAsync(cancellationToken: cancellationToken);
        return tools.Select(t => new McpToolDescriptor(t.Name, t.Description ?? string.Empty, t.JsonSchema)).ToList();
    }

    public async Task<string> CallToolRawAsync(
        string toolName, IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken = default)
    {
        var client = await EnsureConnectedAsync(cancellationToken);
        var result = await client.CallToolAsync(toolName, arguments, cancellationToken: cancellationToken);

        if (result.IsError == true)
        {
            var errorText = result.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text ?? $"'{toolName}' failed with no error detail.";
            throw new McpToolCallException(errorText);
        }

        return result.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text
            ?? throw new InvalidOperationException($"Tool '{toolName}' returned no text content.");
    }

    private async Task<T?> CallToolAsync<T>(string toolName, Dictionary<string, object?> arguments, CancellationToken cancellationToken)
    {
        var client = await EnsureConnectedAsync(cancellationToken);
        var result = await client.CallToolAsync(toolName, arguments, cancellationToken: cancellationToken);

        if (result.IsError == true)
        {
            var errorText = result.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text ?? $"'{toolName}' failed with no error detail.";
            throw new McpToolCallException(errorText);
        }

        var json = result.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text;
        if (string.IsNullOrEmpty(json))
        {
            throw new InvalidOperationException($"Tool '{toolName}' returned no text content.");
        }

        return JsonSerializer.Deserialize<T>(json, JsonOptions);
    }

    private async Task<McpClient> EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (_client is not null) return _client;

        await _connectLock.WaitAsync(cancellationToken);
        try
        {
            if (_client is not null) return _client;

            logger.LogInformation("Connecting MCP client to {Endpoint}", Endpoint);
            var transport = new HttpClientTransport(
                new HttpClientTransportOptions
                {
                    Endpoint = Endpoint,
                    TransportMode = HttpTransportMode.StreamableHttp,
                    AdditionalHeaders = new Dictionary<string, string> { ["X-Api-Key"] = options.ApiKey },
                },
                httpClient,
                ownsHttpClient: false);

            _client = await McpClient.CreateAsync(transport, cancellationToken: cancellationToken);
            return _client;
        }
        finally
        {
            _connectLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is not null)
        {
            await _client.DisposeAsync();
        }
    }
}
