namespace McpServer.Tenders;

/// <summary>Runtime configuration for <see cref="ProzorroClient"/>, sourced from env vars in Program.cs.</summary>
public sealed record ProzorroClientOptions(string BaseUrl, TimeSpan CacheTtl);
