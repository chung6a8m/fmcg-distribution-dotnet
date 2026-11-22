using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace FmcgDms.Api.Tests;

[Collection("SqlScratch")]
public sealed class App02c6ClockTests(SqlScratchFixture fixture)
{
    [Fact]
    public async Task Report_uses_a_fixed_clock_only_for_the_guarded_synthetic_runtime()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string directory = Path.Combine(Path.GetTempPath(), fixture.Nonce);
        Directory.CreateDirectory(directory);
        string clock = Path.Combine(directory, "clock.utc");
        try
        {
            await File.WriteAllTextAsync(clock, "2026-11-07T17:05:00.0000000Z", ct);
            using var baseFactory = fixture.CreateFactory(c2Enabled: true, dedicatedC2: true, c3Enabled: true, c4Enabled: true);
            using var factory = baseFactory.WithWebHostBuilder(builder =>
                builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                    new Dictionary<string, string?> { ["App02C5:Enabled"] = "true", ["App02C6:ClockFile"] = clock })));
            await using var db = new SqlConnection(fixture.TenantAConnectionString);
            await db.ExecuteAsync("""
                INSERT dbo.App02ActorRoleGrant(TenantId,ActorId,RoleCode,IsActive,ChangedBy)
                VALUES(N'tenant-a',N'c6-clock-test',N'sales',1,N'test')
                """);
            using var client = SqlScratchFixture.WithTenant(factory.CreateClient(), "tenant-a", actor: "c6-clock-test", roles: ["sales"]);
            const string report = "/api/synthetic/app02c5/ar";
            var response = await client.GetFromJsonAsync<JsonElement>(report, ct);
            Assert.Equal(DateTimeOffset.Parse("2026-11-07T17:05:00Z"), response.GetProperty("asOf").GetDateTimeOffset());
            using var future = await client.GetAsync(report + "?asOf=2026-11-07T17:06:00Z", ct);
            Assert.Equal(HttpStatusCode.BadRequest, future.StatusCode);
            await File.WriteAllTextAsync(clock, "2026-11-07T17:07:00.0000000Z", ct);
            response = await client.GetFromJsonAsync<JsonElement>(report, ct);
            Assert.Equal(DateTimeOffset.Parse("2026-11-07T17:07:00Z"), response.GetProperty("asOf").GetDateTimeOffset());

            using var unsafeFactory = factory.WithWebHostBuilder(builder => builder.UseEnvironment("Development"));
            var rejected = Assert.Throws<InvalidOperationException>(() => unsafeFactory.CreateClient());
            Assert.Contains("C.6", rejected.Message);
        }
        finally
        {
            File.Delete(clock);
            Directory.Delete(directory);
        }
    }
}
