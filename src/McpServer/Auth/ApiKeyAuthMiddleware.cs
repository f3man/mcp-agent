using System.Security.Cryptography;
using System.Text;

namespace McpServer.Auth;

/// <summary>
/// Checks the X-Api-Key header against MCP_API_KEY before any MCP request reaches the MCP
/// endpoint (registered before app.MapMcp in Program.cs). MCP_API_KEY is guaranteed non-empty
/// at this point — Program.cs fails fast at startup if it isn't configured.
/// </summary>
public sealed class ApiKeyAuthMiddleware(
    RequestDelegate next,
    IConfiguration configuration,
    ILogger<ApiKeyAuthMiddleware> logger)
{
    public const string HeaderName = "X-Api-Key";

    public async Task InvokeAsync(HttpContext context)
    {
        var expectedKey = configuration["MCP_API_KEY"]!;
        var providedKey = context.Request.Headers.TryGetValue(HeaderName, out var values) ? values.ToString() : null;

        if (string.IsNullOrEmpty(providedKey) || !FixedTimeEquals(providedKey, expectedKey))
        {
            logger.LogWarning("Rejected request to {Path}: missing or invalid API key", context.Request.Path);
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "invalid or missing api key" });
            return;
        }

        await next(context);
    }

    /// <summary>Constant-time comparison so response timing doesn't leak how much of the key matched.</summary>
    private static bool FixedTimeEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
}
