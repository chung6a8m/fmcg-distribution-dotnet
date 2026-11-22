using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace FmcgDms.Api.Tests;

[Collection("SqlScratch")]
public sealed class App02d1AdmissionTests(SqlScratchFixture fixture)
{
    [Fact]
    public void Explicit_D1_enablement_requires_the_guarded_clock()
    {
        using var baseFactory = fixture.CreateFactory(c2Enabled: true, dedicatedC2: true, c3Enabled: true, c4Enabled: true);
        using var factory = baseFactory.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["App02C5:Enabled"] = "true",
                    ["App02D1:Enabled"] = "true"
                })));

        var failure = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());
        Assert.Contains("D.1 requires", failure.Message);
    }
}
