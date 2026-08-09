namespace LoopOrchestrator.Mcp;

/// <summary>
/// Everything the loop stages need from McpServer, expressed as plain typed methods — the MCP
/// JSON-RPC/session mechanics live entirely inside McpTenderClient. This is the ONLY way the
/// orchestrator talks to tender data; there is deliberately no other code path to Prozorro or any
/// other tender source.
/// </summary>
public interface IMcpTenderClient
{
    Task<IReadOnlyList<TenderSummary>> ListTendersAsync(
        string? category = null, string? region = null, string status = "active", int limit = 20,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TenderSummary>> SearchTendersAsync(
        string keywords, int limit = 20, CancellationToken cancellationToken = default);

    Task<TenderDetail> GetTenderAsync(string tenderId, CancellationToken cancellationToken = default);

    Task<CompanyProfileData> GetCompanyProfileAsync(CancellationToken cancellationToken = default);
}

/// <summary>Thrown when McpServer's tools/call returns isError=true (e.g. a bogus tenderId).</summary>
public sealed class McpToolCallException(string message) : Exception(message);
