namespace McpServer.Tenders;

/// <summary>
/// Thrown by <see cref="IProzorroClient.GetTenderAsync"/> when the upstream API returns a 404
/// (or an unexpectedly empty payload) for a given tender id. Caught at the tool layer
/// (Tools/TenderTools.cs) and translated into a clean McpException, never surfaced as a raw
/// unhandled exception to the MCP client.
/// </summary>
public sealed class TenderNotFoundException(string tenderId)
    : Exception($"Tender '{tenderId}' was not found.")
{
    public string TenderId { get; } = tenderId;
}
