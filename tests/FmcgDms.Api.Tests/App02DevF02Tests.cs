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
public sealed class App02DevF02Tests(SqlScratchFixture fixture)
{
    private const string Route = "/api/synthetic/dev-f02";
    private readonly CancellationToken ct = TestContext.Current.CancellationToken;

    [Fact]
    public async Task F02_01_02_03_independent_advance_lifecycle_with_source_and_current_authority()
    {
        ScratchMigrator.MigrateDevF02(fixture.TenantAConnectionString, "tenant-a", fixture.Nonce, LogLevel.Warning);
        ScratchMigrator.MigrateDevF02(fixture.TenantBConnectionString, "tenant-b", fixture.Nonce, LogLevel.Warning);
        using var factory = fixture.CreateFactory(advances: true);
        using var finance = SqlScratchFixture.WithTenant(factory.CreateClient(), "tenant-a", "f02-fin", ["finance"]);
        using var worker = SqlScratchFixture.WithTenant(factory.CreateClient(), "tenant-a", "f02-worker", ["sales"]);
        using var cashier = SqlScratchFixture.WithTenant(factory.CreateClient(), "tenant-a", "f02-cash", ["finance", "cashier"]);
        using var peer = SqlScratchFixture.WithTenant(factory.CreateClient(), "tenant-a", "f02-peer", ["sales"]);
        using var selfChecker = SqlScratchFixture.WithTenant(factory.CreateClient(), "tenant-a", "f02-worker", ["cashier", "finance"]);
        using var other = SqlScratchFixture.WithTenant(factory.CreateClient(), "tenant-b", "f02-cash", ["cashier"]);
        await using var a = new SqlConnection(fixture.TenantAConnectionString);
        await using var b = new SqlConnection(fixture.TenantBConnectionString);
        const string grant = """
            INSERT dbo.App02ActorRoleGrant(TenantId,ActorId,RoleCode,IsActive,ChangedBy)
            VALUES(@Tenant,@Actor,@Role,1,N'f02-test')
            """;
        await a.ExecuteAsync(grant, new[]
        {
            new { Tenant = "tenant-a", Actor = "f02-fin", Role = "finance" },
            new { Tenant = "tenant-a", Actor = "f02-worker", Role = "sales" },
            new { Tenant = "tenant-a", Actor = "f02-worker", Role = "cashier" },
            new { Tenant = "tenant-a", Actor = "f02-worker", Role = "finance" },
            new { Tenant = "tenant-a", Actor = "f02-peer", Role = "sales" },
            new { Tenant = "tenant-a", Actor = "f02-cash", Role = "cashier" }
        });
        await b.ExecuteAsync(grant, new { Tenant = "tenant-b", Actor = "f02-cash", Role = "cashier" });

        var before = await Snapshot(a);
        var beforeB = await Snapshot(b);
        Guid advance = Id(await Post(finance, new Command(Guid.NewGuid(), "ISSUE", 500000, "F02-ISSUE",
            EmployeeId: "f02-worker")));
        await Balance(500000);
        using var peerRead = await peer.GetAsync(Route + $"/advances/{advance}", ct);
        Assert.Equal(HttpStatusCode.NotFound, peerRead.StatusCode);
        await Post(peer, new Command(Guid.NewGuid(), "EXPENSE_REQUEST", 300000, "F02-OTHER", advance),
            HttpStatusCode.NotFound);
        Guid expense = Id(await Post(worker, new Command(Guid.NewGuid(), "EXPENSE_REQUEST", 300000, "F02-EXPENSE", advance)));
        await Balance(500000);
        var approval = new Command(Guid.NewGuid(), "EXPENSE_APPROVE", 300000, "F02-APPROVAL", advance, expense);
        await Post(worker, approval, HttpStatusCode.Forbidden);
        await Post(finance, approval with { CommandId = Guid.NewGuid() }, HttpStatusCode.BadRequest);
        await Post(selfChecker, approval with { DecisionReason = "Self approval" }, HttpStatusCode.Conflict);
        await Post(finance, approval with { DecisionReason = "Expense verified" });
        await Balance(200000);
        Guid offer = Id(await Post(worker, new Command(Guid.NewGuid(), "RETURN_OFFER", 200000, "F02-OFFER", advance)));
        await Balance(200000);
        var accept = new Command(Guid.NewGuid(), "RETURN_ACCEPT", 100000, "F02-COUNT-1", advance, offer, DecisionReason: "Cash counted");
        await Post(worker, accept, HttpStatusCode.Forbidden);
        await Post(other, accept, HttpStatusCode.NotFound);
        await Post(selfChecker, accept, HttpStatusCode.Conflict);
        var receipt = await Post(cashier, accept);
        var replay = await Post(cashier, accept);
        Assert.Equal(Id(receipt), Id(replay));
        Assert.True(replay.GetProperty("replayed").GetBoolean());
        var session = await cashier.GetFromJsonAsync<JsonElement>(Route + "/session", ct);
        Assert.Equal("cashier", session.GetProperty("role").GetString());
        var recovered = await cashier.GetFromJsonAsync<JsonElement>(Route + $"/commands/{accept.CommandId}", ct);
        Assert.Equal(Id(receipt), Id(recovered));
        await Post(cashier, accept with { Amount = 1 }, HttpStatusCode.Conflict);
        await Post(cashier, accept with { CommandId = Guid.NewGuid() }, HttpStatusCode.Conflict);
        await Balance(100000);
        var last = accept with { CommandId = Guid.NewGuid(), Evidence = "F02-COUNT-2" };
        var competing = last with { CommandId = Guid.NewGuid(), Evidence = "F02-COUNT-3" };
        var results = await Task.WhenAll(cashier.PostAsJsonAsync(Route + "/commands", last, ct),
            cashier.PostAsJsonAsync(Route + "/commands", competing, ct));
        try
        {
            Assert.Equal(new[] { HttpStatusCode.OK, HttpStatusCode.Conflict },
                results.Select(r => r.StatusCode).Order().ToArray());
        }
        finally { foreach (var result in results) result.Dispose(); }
        await Balance(0);
        using var foreign = await other.GetAsync(Route + $"/advances/{advance}", ct);
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        await a.ExecuteAsync("UPDATE dbo.App02ActorRoleGrant SET IsActive=0 WHERE ActorId=N'f02-cash'");
        await Post(cashier, accept, HttpStatusCode.Forbidden);
        Assert.Equal(before, await Snapshot(a));
        Assert.Equal(beforeB, await Snapshot(b));
        Assert.Equal(5, await a.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.F02AdvanceEntry WHERE AdvanceId=@advance", new { advance }));

        async Task Balance(long expected)
        {
            var result = await worker.GetFromJsonAsync<JsonElement>(Route + $"/advances/{advance}", ct);
            Assert.Equal(expected, result.GetProperty("outstanding").GetInt64());
        }
    }

    private sealed record Command(Guid CommandId, string Action, long Amount, string Evidence,
        Guid? AdvanceId = null, Guid? ReferenceId = null, string? EmployeeId = null, string? DecisionReason = null);

    private static Guid Id(JsonElement result) => result.GetProperty("id").GetGuid();

    private async Task<JsonElement> Post(HttpClient client, Command command, HttpStatusCode expected = HttpStatusCode.OK)
    {
        using var response = await client.PostAsJsonAsync(Route + "/commands", command, ct);
        Assert.Equal(expected, response.StatusCode);
        return response.Content.Headers.ContentType?.MediaType == "application/json"
            ? await response.Content.ReadFromJsonAsync<JsonElement>(ct) : default;
    }

    private static async Task<string[]> Snapshot(SqlConnection db)
    {
        string[] tables = ["ReceivableOpenItem", "CashReceipt", "ReceiptAllocation", "LedgerEffect"];
        var result = new List<string>();
        foreach (string table in tables)
            result.Add(await db.ExecuteScalarAsync<string>(
                $"SELECT (SELECT * FROM dbo.[{table}] FOR JSON PATH, INCLUDE_NULL_VALUES)") ?? "[]");
        return result.ToArray();
    }
}
