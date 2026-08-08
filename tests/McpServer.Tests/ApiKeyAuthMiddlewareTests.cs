using McpServer.Auth;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace McpServer.Tests;

public class ApiKeyAuthMiddlewareTests
{
    private const string ExpectedKey = "correct-key";

    [Fact]
    public async Task InvokeAsync_MissingHeader_Returns401WithExpectedBodyAndSkipsNext()
    {
        var (middleware, context, nextCalled) = CreateMiddleware();

        await middleware.InvokeAsync(context);

        Assert.False(nextCalled());
        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        Assert.Contains("invalid or missing api key", await ReadBodyAsync(context));
    }

    [Fact]
    public async Task InvokeAsync_WrongKey_Returns401AndSkipsNext()
    {
        var (middleware, context, nextCalled) = CreateMiddleware();
        context.Request.Headers[ApiKeyAuthMiddleware.HeaderName] = "wrong-key";

        await middleware.InvokeAsync(context);

        Assert.False(nextCalled());
        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    [Fact]
    public async Task InvokeAsync_CorrectKey_CallsNextAndLeavesStatusUntouched()
    {
        var (middleware, context, nextCalled) = CreateMiddleware();
        context.Request.Headers[ApiKeyAuthMiddleware.HeaderName] = ExpectedKey;

        await middleware.InvokeAsync(context);

        Assert.True(nextCalled());
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    private static (ApiKeyAuthMiddleware Middleware, DefaultHttpContext Context, Func<bool> NextCalled) CreateMiddleware()
    {
        var nextCalled = false;
        RequestDelegate next = _ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        };

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["MCP_API_KEY"] = ExpectedKey })
            .Build();

        var middleware = new ApiKeyAuthMiddleware(next, configuration, NullLogger<ApiKeyAuthMiddleware>.Instance);
        var context = new DefaultHttpContext { Response = { Body = new MemoryStream() } };

        return (middleware, context, () => nextCalled);
    }

    private static async Task<string> ReadBodyAsync(DefaultHttpContext context)
    {
        context.Response.Body.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(context.Response.Body);
        return await reader.ReadToEndAsync();
    }
}
