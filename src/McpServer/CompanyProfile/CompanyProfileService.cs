using System.Text.Json;
using ModelContextProtocol;

namespace McpServer.CompanyProfile;

public interface ICompanyProfileService
{
    /// <summary>Returns the static supplier profile, loading and caching it on first call.</summary>
    Task<CompanyProfileData> GetAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Loads data/company-profile.json and caches the parsed result in memory — it's static PoC
/// data, no need to re-read per call.
/// </summary>
public sealed class CompanyProfileService : ICompanyProfileService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly string _profilePath;
    private readonly ILogger<CompanyProfileService> _logger;
    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private CompanyProfileData? _cached;

    public CompanyProfileService(IConfiguration configuration, ILogger<CompanyProfileService> logger)
    {
        _logger = logger;

        // COMPANY_PROFILE_PATH is an optional escape hatch, not part of the documented config
        // table — the documented default resolves relative to AppContext.BaseDirectory (the
        // running assembly's own directory), NOT IHostEnvironment.ContentRootPath: the latter
        // is the process's working directory at startup, which is the *source* project
        // directory locally (dotnet run) but the container's WORKDIR when deployed — the two
        // environments disagree on where "../../data" even points, and the repo's data/ folder
        // is never copied into a container image anyway. McpServer.csproj instead bundles
        // data/company-profile.json into this project's own build/publish output (see its
        // <Content Include> item), which AppContext.BaseDirectory finds consistently everywhere.
        var overridePath = configuration["COMPANY_PROFILE_PATH"];
        _profilePath = !string.IsNullOrWhiteSpace(overridePath)
            ? Path.GetFullPath(overridePath)
            : Path.Combine(AppContext.BaseDirectory, "data", "company-profile.json");
    }

    public async Task<CompanyProfileData> GetAsync(CancellationToken cancellationToken)
    {
        if (_cached is { } cached)
            return cached;

        await _loadLock.WaitAsync(cancellationToken);
        try
        {
            if (_cached is { } cachedAfterWait)
                return cachedAfterWait;

            if (!File.Exists(_profilePath))
            {
                _logger.LogError("Company profile file not found at {Path}", _profilePath);
                throw new McpException($"Company profile file not found at '{_profilePath}'.");
            }

            await using var stream = File.OpenRead(_profilePath);
            var data = await JsonSerializer.DeserializeAsync<CompanyProfileData>(stream, JsonOptions, cancellationToken);
            if (data is null)
            {
                _logger.LogError("Company profile file at {Path} parsed to null", _profilePath);
                throw new McpException($"Company profile file at '{_profilePath}' could not be parsed.");
            }

            _cached = data;
            return data;
        }
        finally
        {
            _loadLock.Release();
        }
    }
}
