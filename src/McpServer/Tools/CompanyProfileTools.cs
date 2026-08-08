using System.ComponentModel;
using McpServer.CompanyProfile;
using McpServer.Telemetry;
using ModelContextProtocol.Server;

namespace McpServer.Tools;

[McpServerToolType]
public sealed class CompanyProfileTools(ICompanyProfileService profileService)
{
    [McpServerTool(Name = "get_company_profile", ReadOnly = true)]
    [Description("Returns the static supplier profile used for relevance scoring.")]
    public Task<CompanyProfileData> GetCompanyProfile(CancellationToken cancellationToken = default) =>
        ToolTelemetry.TraceAsync(
            "get_company_profile",
            new Dictionary<string, object?>(),
            () => profileService.GetAsync(cancellationToken));
}
