using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using FmcgDms.Api.Persistence;
using Microsoft.Extensions.Logging;
using Microsoft.Data.SqlClient;
using Xunit;

namespace FmcgDms.Api.Tests;

[Collection("SqlScratch")]
public sealed class App02DevF01Tests(SqlScratchFixture fixture)
{
    private const string Route = "/api/synthetic/app02c1";
    private readonly CancellationToken ct = TestContext.Current.CancellationToken;

    [Fact]
    public async Task F01_02_complete_and_recover_one_visit_without_ledger_changes()
    {
        ScratchMigrator.MigrateDevF01(fixture.TenantAConnectionString, "tenant-a", fixture.Nonce, LogLevel.Warning);
        ScratchMigrator.MigrateDevF01(fixture.TenantBConnectionString, "tenant-b", fixture.Nonce, LogLevel.Warning);
        using var factory = fixture.CreateFactory(visits: true);
        using var a = SqlScratchFixture.WithTenant(factory.CreateClient(), "tenant-a", "f01-a", ["sales"]);
        using var b = SqlScratchFixture.WithTenant(factory.CreateClient(), "tenant-b", "f01-b", ["sales"]);
        using var peer = SqlScratchFixture.WithTenant(factory.CreateClient(), "tenant-a", "f01-peer", ["sales"]);
        using var warehouse = SqlScratchFixture.WithTenant(factory.CreateClient(), "tenant-a", "f01-wh", ["warehouse"]);
        await using var dbA = new SqlConnection(fixture.TenantAConnectionString);
        await using var dbB = new SqlConnection(fixture.TenantBConnectionString);
        const string grant = """
            INSERT dbo.App02ActorRoleGrant(TenantId,ActorId,RoleCode,IsActive,ChangedBy)
            VALUES(@tenant,@actor,N'sales',1,N'f01-test')
            """;
        await dbA.ExecuteAsync(grant, new { tenant = "tenant-a", actor = "f01-a" });
        await dbA.ExecuteAsync(grant, new { tenant = "tenant-a", actor = "f01-peer" });
        await dbB.ExecuteAsync(grant, new { tenant = "tenant-b", actor = "f01-b" });
        using var setup = await a.PostAsJsonAsync(Route + "/customers/save",
            new { commandId = Guid.NewGuid(), code = "F01-" + Guid.NewGuid().ToString("N")[..16], name = "Visit DEV" }, ct);
        setup.EnsureSuccessStatusCode();
        Guid customerId = (await setup.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("id").GetGuid();
        await dbA.ExecuteAsync("""
            INSERT dbo.F01CustomerScope(TenantId,CustomerId,ActorId) VALUES(N'tenant-a',@customerId,N'f01-a')
            """, new { customerId });
        Guid commandId = Guid.NewGuid();
        string query = $"/visits?customerId={customerId}";
        var before = await Snapshot(dbA);
        var beforeB = await Snapshot(dbB);
        var body = new { commandId, customerId, outcome = "NOT_MET", reason = "Closed" };

        var responses = await Task.WhenAll(Post(a, body, HttpStatusCode.OK), Post(a, body, HttpStatusCode.OK));
        Guid id = responses[0].GetProperty("id").GetGuid();
        Assert.Equal(id, responses[1].GetProperty("id").GetGuid());
        Assert.Single(responses, r => r.GetProperty("replayed").GetBoolean());
        var recovered = await a.GetFromJsonAsync<JsonElement>(Route + $"/visits/commands/{commandId}", ct);
        Assert.Equal(id, recovered.GetProperty("id").GetGuid());
        var visits = await a.GetFromJsonAsync<JsonElement[]>(Route + query, ct);
        Assert.Equal(id, Assert.Single(visits!).GetProperty("id").GetGuid());
        await Post(a, body with { reason = "Changed" }, HttpStatusCode.Conflict);
        await Post(a, body with { commandId = Guid.NewGuid(), reason = " " }, HttpStatusCode.BadRequest);
        await Post(a, body with { commandId = Guid.NewGuid(), outcome = "ORDER" }, HttpStatusCode.BadRequest);
        await Post(a, new { commandId = Guid.NewGuid(), customerId, outletId = Guid.NewGuid(),
            body.outcome, body.reason }, HttpStatusCode.NotFound);

        Assert.Empty((await b.GetFromJsonAsync<JsonElement[]>(Route + query, ct))!);
        await Post(b, body, HttpStatusCode.NotFound);
        Assert.Empty((await peer.GetFromJsonAsync<JsonElement[]>(Route + "/visits/customers", ct))!);
        Assert.Empty((await peer.GetFromJsonAsync<JsonElement[]>(Route + query, ct))!);
        await Post(peer, body, HttpStatusCode.NotFound);
        using var genericOutcome = await a.GetAsync(Route + $"/commands/{commandId}", ct);
        Assert.Equal(HttpStatusCode.NotFound, genericOutcome.StatusCode);
        await Post(warehouse, body, HttpStatusCode.Forbidden);
        using var deniedRead = await warehouse.GetAsync(Route + query, ct);
        Assert.Equal(HttpStatusCode.Forbidden, deniedRead.StatusCode);
        await dbA.ExecuteAsync("DELETE dbo.F01CustomerScope WHERE TenantId=N'tenant-a' AND ActorId=N'f01-a'");
        Assert.Empty((await a.GetFromJsonAsync<JsonElement[]>(Route + query, ct))!);
        using var unassignedRecovery = await a.GetAsync(Route + $"/visits/commands/{commandId}", ct);
        Assert.Equal(HttpStatusCode.Forbidden, unassignedRecovery.StatusCode);
        await Post(a, body, HttpStatusCode.Forbidden);
        await dbA.ExecuteAsync("UPDATE dbo.App02ActorRoleGrant SET IsActive=0 WHERE ActorId=N'f01-a'");
        await Post(a, body, HttpStatusCode.Forbidden);
        using var revokedRead = await a.GetAsync(Route + query, ct);
        Assert.Equal(HttpStatusCode.Forbidden, revokedRead.StatusCode);

        var row = await dbA.QuerySingleAsync<(string Tenant, string Actor, Guid Customer, Guid? Outlet,
            Guid Source, string Outcome, string Reason, string? Observation)>("""
            SELECT TenantId,ActorId,CustomerId,OutletId,CommandId,Outcome,Reason,Observation
            FROM dbo.F01Visit WHERE Id=@id
            """, new { id });
        Assert.Equal(("tenant-a", "f01-a", customerId, (Guid?)null, commandId, "NOT_MET", "Closed", (string?)null), row);
        Assert.Equal(1, await dbA.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.F01Visit WHERE CustomerId=@customerId", new { customerId }));
        await Assert.ThrowsAsync<SqlException>(() => dbA.ExecuteAsync(
            "UPDATE dbo.F01Visit SET Reason=N'Mutated' WHERE Id=@id", new { id }));
        Assert.Equal(0, await dbB.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.F01Visit WHERE CustomerId=@customerId", new { customerId }));
        Assert.Equal(before, await Snapshot(dbA));
        Assert.Equal(beforeB, await Snapshot(dbB));
    }

    private async Task<JsonElement> Post(HttpClient client, object body, HttpStatusCode status)
    {
        using var response = await client.PostAsJsonAsync(Route + "/visits", body, ct);
        Assert.Equal(status, response.StatusCode);
        return response.Content.Headers.ContentType?.MediaType == "application/json"
            ? await response.Content.ReadFromJsonAsync<JsonElement>(ct) : default;
    }

    private static async Task<string[]> Snapshot(SqlConnection db)
    {
        string[] tables = ["AOrder", "SaleDelivery", "ReceivableOpenItem", "PhysicalStockMovement",
            "PhysicalStockBalance", "LedgerEffect"];
        var rows = new List<string>();
        foreach (string table in tables)
            rows.Add(await db.ExecuteScalarAsync<string>(
                $"SELECT (SELECT * FROM dbo.[{table}] FOR JSON PATH, INCLUDE_NULL_VALUES)") ?? "[]");
        return rows.ToArray();
    }
}
