using McpServer.CompanyProfile;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;

namespace McpServer.Tests;

public class CompanyProfileServiceTests
{
    [Fact]
    public async Task GetAsync_FileMissing_ThrowsMcpException()
    {
        var missingPath = Path.Combine(Path.GetTempPath(), $"no-such-profile-{Guid.NewGuid():N}.json");
        var service = CreateService(profilePathOverride: missingPath);

        var ex = await Assert.ThrowsAsync<McpException>(() => service.GetAsync(CancellationToken.None));
        Assert.Contains(missingPath, ex.Message);
    }

    [Fact]
    public async Task GetAsync_ValidFile_ParsesExpectedShape()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"company-profile-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(tempFile, """
            {
              "companyName": "Test Co",
              "categories": ["IT services"],
              "certifications": ["ISO 9001"],
              "regionsServed": ["Kyiv"],
              "minProjectValue": 1000,
              "maxProjectValue": 5000,
              "currency": "UAH",
              "pastContracts": [
                { "category": "IT services", "year": 2024, "outcome": "won" },
                { "category": "medical supplies", "year": 2023, "outcome": "lost", "reason": "price" }
              ]
            }
            """);

        try
        {
            var service = CreateService(profilePathOverride: tempFile);

            var profile = await service.GetAsync(CancellationToken.None);

            Assert.Equal("Test Co", profile.CompanyName);
            Assert.Equal(["IT services"], profile.Categories);
            Assert.Equal(2, profile.PastContracts.Count);
            Assert.Equal("lost", profile.PastContracts[1].Outcome);
            Assert.Equal("price", profile.PastContracts[1].Reason);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task GetAsync_CalledTwice_ReturnsCachedInstance()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"company-profile-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(tempFile, """
            {
              "companyName": "Test Co", "categories": [], "certifications": [], "regionsServed": [],
              "minProjectValue": 0, "maxProjectValue": 0, "currency": "UAH", "pastContracts": []
            }
            """);

        try
        {
            var service = CreateService(profilePathOverride: tempFile);

            var first = await service.GetAsync(CancellationToken.None);
            File.Delete(tempFile); // if it re-read from disk, this would now throw
            var second = await service.GetAsync(CancellationToken.None);

            Assert.Same(first, second);
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    private static CompanyProfileService CreateService(string profilePathOverride)
    {
        var env = new FakeHostEnvironment();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["COMPANY_PROFILE_PATH"] = profilePathOverride })
            .Build();
        return new CompanyProfileService(env, configuration, NullLogger<CompanyProfileService>.Instance);
    }

    private sealed class FakeHostEnvironment : IHostEnvironment
    {
        public string ApplicationName { get; set; } = "McpServer.Tests";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public string EnvironmentName { get; set; } = "Development";
    }
}
