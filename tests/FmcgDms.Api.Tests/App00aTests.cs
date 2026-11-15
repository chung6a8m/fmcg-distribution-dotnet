using System.Net;
using System.Net.Http.Json;
using Dapper;
using FmcgDms.Api.Persistence;
using Microsoft.Data.SqlClient;
using Xunit;

namespace FmcgDms.Api.Tests;

[Collection("SqlScratch")]
public sealed class App00aTests(SqlScratchFixture fixture)
{
    [Fact]
    public void Migrations_repeat_without_mutating_tenant_binding()
    {
        ScratchMigrator.Migrate(fixture.TenantAConnectionString, "tenant-a", fixture.Nonce);
        ScratchMigrator.Migrate(fixture.TenantAConnectionString, "tenant-a", fixture.Nonce);
        using var db = new SqlConnection(fixture.TenantAConnectionString);
        Assert.Equal(1, db.ExecuteScalar<int>("SELECT COUNT(*) FROM dbo.AppTenantBinding"));
        Assert.Equal("tenant-a", db.ExecuteScalar<string>("SELECT TenantId FROM dbo.AppTenantBinding"));
        Assert.Throws<InvalidOperationException>(() =>
            ScratchMigrator.Migrate(fixture.TenantAConnectionString, "tenant-b", fixture.Nonce));
    }

    [Fact]
    public void Wrong_nonce_or_system_db_is_rejected_before_mutation()
    {
        Assert.Throws<InvalidOperationException>(() =>
            ScratchMigrator.Migrate(fixture.TenantAConnectionString, "tenant-a", Guid.NewGuid().ToString("N")));
        Assert.Throws<InvalidOperationException>(() =>
            ScratchMigrator.Migrate(fixture.ConnectionString("master"), "tenant-a", fixture.Nonce));
    }

    [Fact]
    public async Task Liveness_is_public_but_diagnostics_require_authentication()
    {
        using var factory = fixture.CreateFactory();
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/diagnostics/tenant")).StatusCode);
    }

    [Fact]
    public async Task Validated_tenant_claim_routes_to_its_DB_not_an_untrusted_header()
    {
        using var factory = fixture.CreateFactory();
        using var a = SqlScratchFixture.WithTenant(factory.CreateClient(), "tenant-a");
        using var b = SqlScratchFixture.WithTenant(factory.CreateClient(), "tenant-b");
        a.DefaultRequestHeaders.Add("X-Tenant-Id", "tenant-b");
        var aInfo = await a.GetFromJsonAsync<TenantDto>("/api/diagnostics/tenant");
        var bInfo = await b.GetFromJsonAsync<TenantDto>("/api/diagnostics/tenant");
        Assert.NotNull(aInfo);
        Assert.NotNull(bInfo);
        Assert.Equal("tenant-a", aInfo.tenantId);
        Assert.Equal(fixture.TenantADb, aInfo.databaseName);
        Assert.Equal("tenant-b", bInfo.tenantId);
        Assert.Equal(fixture.TenantBDb, bInfo.databaseName);
    }

    [Fact]
    public async Task Data_written_in_Tenant_A_is_not_visible_in_Tenant_B()
    {
        using var factory = fixture.CreateFactory();
        using var a = SqlScratchFixture.WithTenant(factory.CreateClient(), "tenant-a");
        using var b = SqlScratchFixture.WithTenant(factory.CreateClient(), "tenant-b");
        string marker = "isolation-" + Guid.NewGuid().ToString("N");
        var post = await a.PostAsJsonAsync("/api/diagnostics/items", new { description = marker });
        Assert.Equal(HttpStatusCode.Created, post.StatusCode);
        var aa = await a.GetFromJsonAsync<ItemDto[]>("/api/diagnostics/items");
        var bb = await b.GetFromJsonAsync<ItemDto[]>("/api/diagnostics/items");
        Assert.Contains(aa!, x => x.description == marker);
        Assert.DoesNotContain(bb!, x => x.description == marker);
    }

    [Fact]
    public async Task Misconfigured_tenant_mapping_fails_closed()
    {
        using var factory = fixture.CreateFactory(tenantAOverride: fixture.TenantBConnectionString);
        using var a = SqlScratchFixture.WithTenant(factory.CreateClient(), "tenant-a");
        Assert.Equal(HttpStatusCode.Forbidden, (await a.GetAsync("/api/diagnostics/tenant")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await a.PostAsJsonAsync("/api/diagnostics/items", new { description = "must-not-write" })).StatusCode);
        using var dbB = new SqlConnection(fixture.TenantBConnectionString);
        Assert.Equal(0, dbB.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM dbo.DiagnosticItem WHERE Description='must-not-write'"));
    }

    [Fact]
    public async Task Synthetic_diagnostics_reject_non_scratch_and_non_LocalDB_targets()
    {
        // In both cases the signed tenant identity is valid, but the configured target is not.
        string otherInstance = new SqlConnectionStringBuilder(fixture.TenantAConnectionString)
        {
            DataSource = "not-a-localdb-server"
        }.ConnectionString;
        foreach (string unsafeTarget in new[] { fixture.ConnectionString("master"), otherInstance })
        {
            using var factory = fixture.CreateFactory(tenantAOverride: unsafeTarget);
            using var a = SqlScratchFixture.WithTenant(factory.CreateClient(), "tenant-a");
            Assert.Equal(HttpStatusCode.Forbidden,
                (await a.GetAsync("/api/diagnostics/tenant")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden,
                (await a.PostAsJsonAsync("/api/diagnostics/items", new { description = "never-write" })).StatusCode);
        }

        using var scratch = new SqlConnection(fixture.TenantAConnectionString);
        Assert.Equal(0, scratch.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM dbo.DiagnosticItem WHERE Description='never-write'"));
    }

    [Fact]
    public async Task Synthetic_diagnostics_reject_invalid_scratch_ownership_marker()
    {
        await using var scratch = new SqlConnection(fixture.TenantAConnectionString);
        await scratch.OpenAsync();
        await scratch.ExecuteAsync(
            "UPDATE dbo.App00ScratchMarker SET Nonce=@Nonce", new { Nonce = Guid.NewGuid().ToString("N") });
        try
        {
            using var factory = fixture.CreateFactory();
            using var a = SqlScratchFixture.WithTenant(factory.CreateClient(), "tenant-a");
            Assert.Equal(HttpStatusCode.Forbidden,
                (await a.GetAsync("/api/diagnostics/tenant")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden,
                (await a.PostAsJsonAsync("/api/diagnostics/items", new { description = "invalid-marker" })).StatusCode);
            Assert.Equal(0, await scratch.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM dbo.DiagnosticItem WHERE Description='invalid-marker'"));
        }
        finally
        {
            // Restore the fixture's ownership marker even if an assertion fails.
            await scratch.ExecuteAsync(
                "UPDATE dbo.App00ScratchMarker SET Nonce=@Nonce", new { Nonce = fixture.Nonce });
        }
    }

    [Fact]
    public async Task Unknown_tenant_and_invalid_signature_cannot_read_diagnostics()
    {
        using var factory = fixture.CreateFactory();
        using var unknown = SqlScratchFixture.WithTenant(factory.CreateClient(), "tenant-unknown");
        Assert.Equal(HttpStatusCode.Forbidden,
            (await unknown.GetAsync("/api/diagnostics/items")).StatusCode);

        using var forged = factory.CreateClient();
        string jwt = SqlScratchFixture.IssueSyntheticToken("tenant-a");
        int signatureStart = jwt.LastIndexOf('.') + 1;
        string invalidJwt = jwt[..signatureStart] +
            (jwt[signatureStart] == 'A' ? 'B' : 'A') +
            jwt[(signatureStart + 1)..];
        forged.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", invalidJwt);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await forged.GetAsync("/api/diagnostics/items")).StatusCode);
    }

    private sealed record TenantDto(string tenantId, string databaseName);
    private sealed record ItemDto(Guid id, string description);
}
