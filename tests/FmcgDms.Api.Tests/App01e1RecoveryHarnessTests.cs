using System.Net;
using System.Net.Http.Json;
using Dapper;
using FmcgDms.Api.Persistence;
using Microsoft.Data.SqlClient;
using Xunit;

namespace FmcgDms.Api.Tests;

/// <summary>
/// APP-01E.1 harness smoke tests: each scenario runs against BOTH real,
/// nonce-owned LocalDB tenant databases, through separate HTTP clients.
/// E.2-E.4 own cross-command races, comprehensive fault matrix and restart.
/// </summary>
[Collection("SqlScratch")]
public sealed class App01e1RecoveryHarnessTests(SqlScratchFixture fixture)
{
    private const string Producer = "app01e1-harness";
    private const string Actor = "e1-warehouse-actor";
    private const string QuotaRoute = "/api/synthetic/app01a/commands/grant-b-quota";

    private sealed record Scene(Guid Trip, Guid Location, Guid Sku);

    private static string Key() => Guid.NewGuid().ToString("N");

    private string Connection(string tenant) => tenant switch
    {
        "tenant-a" => fixture.TenantAConnectionString,
        "tenant-b" => fixture.TenantBConnectionString,
        _ => throw new ArgumentException("Unknown tenant.", nameof(tenant))
    };

    private Scene Seed(string tenant)
    {
        var s = new Scene(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        Guid vehicle = Guid.NewGuid();
        using var db = new SqlConnection(Connection(tenant));
        db.Execute("""
            INSERT dbo.Sku(Id,TenantId,Code,Name,BaseUom)
            VALUES(@Sku,@Tenant,@SkuCode,N'E1 SKU',N'EA');
            INSERT dbo.StockLocation(Id,TenantId,Code,Kind)
            VALUES(@Location,@Tenant,@WarehouseCode,N'WAREHOUSE');
            INSERT dbo.StockLocation(Id,TenantId,Code,Kind)
            VALUES(@Vehicle,@Tenant,@VehicleCode,N'VEHICLE');
            INSERT dbo.Trip(Id,TenantId,Code,BusinessDate,VehicleLocationId)
            VALUES(@Trip,@Tenant,@TripCode,CONVERT(date,SYSUTCDATETIME()),@Vehicle);
            INSERT dbo.StockPool(TenantId,StockLocationId,SkuId,OnHandBaseQuantity)
            VALUES(@Tenant,@Location,@Sku,100);
            """, new
        {
            Tenant = tenant, s.Trip, s.Location, s.Sku, Vehicle = vehicle,
            SkuCode = Key(), WarehouseCode = Key(), VehicleCode = Key(), TripCode = Key()
        });
        return s;
    }

    private static GrantBQuotaCommand Quota(Scene s, string command, string source) =>
        new(Producer, command, source, s.Trip, s.Location, s.Sku, 30m);

    private static HttpClient Client(TestApiFactory factory, string tenant,
        DelegatingHandler? handler = null)
    {
        var client = handler is null
            ? factory.CreateClient()
            : factory.CreateDefaultClient(handler);
        return SqlScratchFixture.WithTenant(client, tenant, Actor, ["warehouse"]);
    }

    private static async Task<(HttpStatusCode Code, BusinessCommandOutcome Outcome)>
        SendAsync(HttpClient client, GrantBQuotaCommand command)
    {
        using var response = await client.PostAsJsonAsync(
            QuotaRoute, command, TestContext.Current.CancellationToken);
        var result = await response.Content.ReadFromJsonAsync<BusinessCommandOutcome>(
            TestContext.Current.CancellationToken);
        return (response.StatusCode, Assert.IsType<BusinessCommandOutcome>(result));
    }

    private Task<CommandSqlReadback> Probe(string tenant, Scene s, string key) =>
        RecoverySqlProbe.ReadAsync(
            Connection(tenant), tenant, Producer, key, s.Location, s.Sku);

    private static void AssertCommitted(CommandSqlReadback actual)
    {
        Assert.Equal("APPLIED", actual.Status);
        Assert.Equal("B_QUOTA_GRANTED", actual.OutcomeCode);
        Assert.NotNull(actual.SourceId);
        Assert.Equal(1, actual.Commands);
        Assert.Equal(1, actual.Sources);
        Assert.Equal(1, actual.SourceLines);
        Assert.Equal(1, actual.Effects);
        Assert.Equal(1, actual.Rights);
        Assert.Equal(30m, actual.Allocated);
    }

    private static HarnessStage ExpectedStage(string label, CommandSqlReadback actual,
        bool committed) => committed
        ? new HarnessStage(label, actual, 1, 1, 1, 1, 1, 30m)
        : new HarnessStage(label, actual, 0, 0, 0, 0, 0, 0m);

    [Fact]
    public async Task Sql_trigger_fault_guard_refuses_master_nonlocaldb_and_cross_tenant_targets()
    {
        string source = Key();
        var master = new SqlConnectionStringBuilder(Connection("tenant-a"))
        {
            InitialCatalog = "master"
        };
        var notLocalDb = new SqlConnectionStringBuilder(Connection("tenant-a"))
        {
            DataSource = "localhost"
        };
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            _ = await PreCommitLedgerFault.ArmAsync(master.ConnectionString, "tenant-a", source);
        });
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            _ = await PreCommitLedgerFault.ArmAsync(notLocalDb.ConnectionString, "tenant-a", source);
        });
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            _ = await PreCommitLedgerFault.ArmAsync(Connection("tenant-a"), "tenant-b", source);
        });
    }

    /// <summary>
    /// The barrier is at the HTTP admission boundary (not a SQL schedule oracle).
    /// Same canonical key, actor and payload must produce one persisted source.
    /// </summary>
    [Theory]
    [InlineData("tenant-a")]
    [InlineData("tenant-b")]
    public async Task Two_distinct_clients_share_explicit_gate_and_replay_one_commit(string tenant)
    {
        Scene s = Seed(tenant);
        var command = Quota(s, Key(), Key());
        using var factory = fixture.CreateFactory();
        var gate = new TwoClientReleaseGate();
        using var first = Client(factory, tenant, new TwoClientGateHandler(gate));
        using var second = Client(factory, tenant, new TwoClientGateHandler(gate));

        Task<(HttpStatusCode Code, BusinessCommandOutcome Outcome)> a = SendAsync(first, command);
        Task<(HttpStatusCode Code, BusinessCommandOutcome Outcome)> b = SendAsync(second, command);
        try
        {
            await gate.BothReady.WaitAsync(
                TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
            Assert.Equal(2, gate.Arrived);
        }
        finally
        {
            gate.Release();
        }

        var replies = await Task.WhenAll(a, b);
        CommandSqlReadback actual = await Probe(tenant, s, command.CommandId);
        RecoveryHarnessEvidence.Write(tenant, "two-client-gate",
            ExpectedStage("AFTER_BOTH_HTTP_CLIENTS", actual, committed: true));

        Assert.All(replies, x =>
        {
            Assert.Equal(HttpStatusCode.OK, x.Code);
            Assert.Equal("APPLIED", x.Outcome.Status);
            Assert.Equal("B_QUOTA_GRANTED", x.Outcome.OutcomeCode);
            Assert.Equal(actual.SourceId, x.Outcome.SourceId);
        });
        Assert.Equal(1, replies.Count(x => x.Outcome.Replayed));
        Assert.Equal(1, replies.Count(x => !x.Outcome.Replayed));
        AssertCommitted(actual);
    }

    private static async Task ExpectSqlFaultAsync(HttpClient client, GrantBQuotaCommand command)
    {
        try
        {
            using var response = await client.PostAsJsonAsync(
                QuotaRoute, command, TestContext.Current.CancellationToken);
            Assert.False(response.IsSuccessStatusCode,
                "A pre-COMMIT SQL trigger must not yield a successful HTTP response.");
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException)
        {
            // TestServer can propagate SQL trigger exceptions instead of an HTTP 500.
        }
    }

    /// <summary>
    /// The fault is thrown after a ledger INSERT but before COMMIT. After the
    /// trigger is removed, retry must reuse exactly the SAME command identity.
    /// </summary>
    [Theory]
    [InlineData("tenant-a")]
    [InlineData("tenant-b")]
    public async Task Precommit_ledger_fault_rolls_back_then_same_key_retries(string tenant)
    {
        Scene s = Seed(tenant);
        var command = Quota(s, Key(), Key());
        using var factory = fixture.CreateFactory();
        using var client = Client(factory, tenant);
        CommandSqlReadback rolledBack;

        await using (var fault =
            await PreCommitLedgerFault.ArmAsync(Connection(tenant), tenant, command.SourceNumber))
        {
            await ExpectSqlFaultAsync(client, command);
            rolledBack = await Probe(tenant, s, command.CommandId);
        }

        var retry = await SendAsync(client, command);
        CommandSqlReadback committed = await Probe(tenant, s, command.CommandId);
        RecoveryHarnessEvidence.Write(tenant, "precommit-rollback",
            ExpectedStage("FAULT_PRECOMMIT_ROLLED_BACK", rolledBack, committed: false),
            ExpectedStage("RETRY_SAME_IDENTITY", committed, committed: true));

        Assert.Null(rolledBack.Status);
        Assert.Null(rolledBack.SourceId);
        Assert.Equal(0, rolledBack.Commands);
        Assert.Equal(0, rolledBack.Sources);
        Assert.Equal(0, rolledBack.SourceLines);
        Assert.Equal(0, rolledBack.Effects);
        Assert.Equal(0, rolledBack.Rights);
        Assert.Equal(0m, rolledBack.Allocated);
        Assert.Equal(HttpStatusCode.OK, retry.Code);
        Assert.False(retry.Outcome.Replayed);
        Assert.Equal(committed.SourceId, retry.Outcome.SourceId);
        AssertCommitted(committed);
    }

    /// <summary>
    /// The handler suppresses the FIRST response after server completion.
    /// This demonstrates a post-commit unknown outcome, not process restart,
    /// disconnected TCP, a recovery endpoint or offline queued writes.
    /// </summary>
    [Theory]
    [InlineData("tenant-a")]
    [InlineData("tenant-b")]
    public async Task Response_suppressed_after_commit_replay_keeps_sql_fingerprint(string tenant)
    {
        Scene s = Seed(tenant);
        var command = Quota(s, Key(), Key());
        using var factory = fixture.CreateFactory();
        var ackLoss = new AfterServerAckLossHandler(QuotaRoute);
        using var first = Client(factory, tenant, ackLoss);
        using var second = Client(factory, tenant);

        var lost = await Assert.ThrowsAsync<HttpRequestException>(async () =>
        {
            using var _ = await first.PostAsJsonAsync(
                QuotaRoute, command, TestContext.Current.CancellationToken);
        });
        Assert.Contains("APP01E1_SYNTHETIC_ACK_SUPPRESSED_AFTER_RESPONSE", lost.Message);
        Assert.Equal(1, ackLoss.Suppressed);

        CommandSqlReadback afterDroppedAck = await Probe(tenant, s, command.CommandId);
        var replay = await SendAsync(second, command);
        CommandSqlReadback afterReplay = await Probe(tenant, s, command.CommandId);
        RecoveryHarnessEvidence.Write(tenant, "lost-ack-smoke",
            ExpectedStage("POSTCOMMIT_ACK_SUPPRESSED", afterDroppedAck, committed: true),
            ExpectedStage("SAME_IDENTITY_REPLAY", afterReplay, committed: true));

        Assert.Equal(HttpStatusCode.OK, replay.Code);
        Assert.True(replay.Outcome.Replayed);
        Assert.Equal(afterDroppedAck.SourceId, replay.Outcome.SourceId);
        Assert.Equal(afterDroppedAck.LedgerSha256, afterReplay.LedgerSha256);
        Assert.Equal(afterDroppedAck.Allocated, afterReplay.Allocated);
        AssertCommitted(afterDroppedAck);
        AssertCommitted(afterReplay);
    }
}
