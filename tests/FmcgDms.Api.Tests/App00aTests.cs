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

    private sealed record TenantDto(string tenantId, string databaseName);
    private sealed record ItemDto(Guid id, string description);
}
