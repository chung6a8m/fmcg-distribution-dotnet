using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace FmcgDms.Api.Tests;

[Collection("SqlScratch")]
public sealed class App02d4AdmissionTests(SqlScratchFixture fixture)
{
    [Fact]
    public void Explicit_D4_enablement_requires_a_guarded_clock_profile_and_D3_runtime()
    {
        using var baseline = fixture.CreateFactory();
        using var factory = baseline.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?> { ["App02D4:Enabled"] = "true" })));
        var failure = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());
        Assert.Contains("D.4 requires the guarded two-tenant LocalDB rehearsal profile", failure.Message);
    }

    [Fact]
    public async Task Default_synthetic_runtime_does_not_expose_D4_api_or_stub()
    {
        using var factory = fixture.CreateFactory();
        using var client = factory.CreateClient();
        var ct = TestContext.Current.CancellationToken;
        using var session = await client.GetAsync("/api/synthetic/app02d4/session", ct);
        using var partner = await client.GetAsync("/stub/invoice/lookup", ct);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, session.StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, partner.StatusCode);
    }
}
