using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace FmcgDms.Api.Tests;

[Collection("SqlScratch")]
public sealed class App02d2AdmissionTests(SqlScratchFixture fixture)
{
    [Fact]
    public void Explicit_D2_enablement_requires_the_guarded_D1_runtime()
    {
        using var baseFactory = fixture.CreateFactory(c2Enabled: true, dedicatedC2: true, c3Enabled: true, c4Enabled: true);
        using var factory = baseFactory.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?> { ["App02D2:Enabled"] = "true" })));

        var failure = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());
        Assert.Contains("D.2 requires the guarded D.1 runtime", failure.Message);
    }
}
