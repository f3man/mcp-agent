using System.Text.Json;

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

    /// <summary>Discovers every tool McpServer currently exposes, with its real, live
    /// name/description/input-schema — used to hand a curated subset to Claude as native Anthropic
    /// tools (see Loop/Stages/AssessStage.cs) without hand-maintaining a second copy of each tool's
    /// schema that could drift from what McpServer actually defines.</summary>
    Task<IReadOnlyList<McpToolDescriptor>> ListAvailableToolsAsync(CancellationToken cancellationToken = default);

    /// <summary>Generic, by-name tool invocation — the passthrough AssessStage's agentic tool loop
    /// calls into when Claude requests a tool. Deliberately separate from the four typed methods
    /// above (which stay the fixed, deterministic call sites Discover/LoopRunner use); callers of
    /// this generic path are expected to allow-list which tool names they'll actually invoke.</summary>
    Task<string> CallToolRawAsync(
        string toolName, IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken = default);
}

/// <summary>Thrown when McpServer's tools/call returns isError=true (e.g. a bogus tenderId).</summary>
public sealed class McpToolCallException(string message) : Exception(message);

/// <summary>Local projection of the MCP SDK's own tool descriptor — kept separate from
/// ModelContextProtocol.Client.McpClientTool/Protocol.Tool per this file's own "protocol boundary,
/// not shared-code boundary" convention (see Mcp/TenderDtos.cs's doc comment for the same
/// reasoning applied to tender data).</summary>
public sealed record McpToolDescriptor(string Name, string Description, JsonElement InputSchema);
