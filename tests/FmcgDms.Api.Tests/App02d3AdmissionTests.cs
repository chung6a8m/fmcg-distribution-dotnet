using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace FmcgDms.Api.Tests;

[Collection("SqlScratch")]
public sealed class App02d3AdmissionTests(SqlScratchFixture fixture)
{
    [Fact]
    public void Explicit_D3_enablement_requires_the_guarded_D2_runtime()
    {
        using var baseline = fixture.CreateFactory(c2Enabled: true, dedicatedC2: true, c3Enabled: true, c4Enabled: true);
        using var factory = baseline.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?> { ["App02D3:Enabled"] = "true" })));

        var failure = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());
        Assert.Contains("D.3 requires the guarded D.2 runtime", failure.Message);
    }
}
