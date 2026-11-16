using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using FmcgDms.Api.Persistence;
using Microsoft.Data.SqlClient;
using Xunit;

namespace FmcgDms.Api.Tests;

/// <summary>
/// APP-01E.3: reuses the APP-01E.2 physical scenario builder and SQL readback
/// without mocking the application or changing its production transaction code.
/// Each test starts from a fresh scene on one of two owned tenant LocalDBs.
/// </summary>
public sealed partial class App01e2CrossCommandRaceTests
{
    private const string E3Producer = "app01e3-rollback";
    private sealed record FaultPoint(string Scenario, string Table, string Operation)
    {
        public string Id => $"{Scenario}-{Table.ToLowerInvariant()}-{Operation.ToLowerInvariant()}";
    }

    // In SQL statement order, cover command identity, source, rights, physical
    // stock, debt/cash posting, paired ledger, and final state transitions.
    // The BusinessCommand UPDATE cell faults AFTER every reserve side effect.
    private static readonly FaultPoint[] FaultPoints =
    [
        new("reserve", "BusinessCommand", "INSERT"),
        new("reserve", "StockPool", "UPDATE"),
        new("reserve", "BusinessSource", "INSERT"),
        new("reserve", "StockRight", "INSERT"),
        new("reserve", "LedgerEffect", "INSERT"),
        new("reserve", "BusinessCommand", "UPDATE"),
        new("quota", "StockPool", "UPDATE"),
        new("quota", "BQuota", "INSERT"),
        new("quota", "LedgerEffect", "INSERT"),
        new("confirm", "AOrder", "UPDATE"),
        new("confirm", "AConfirmedLineSnapshot", "INSERT"),
        new("credit", "BCreditPool", "UPDATE"),
        new("credit", "BCreditGrant", "INSERT"),
        new("credit", "LedgerEffect", "INSERT"),
        new("manifest", "TripManifest", "UPDATE"),
        new("manifest", "PhysicalStockBalance", "UPDATE"),
        new("manifest", "TripManifestLine", "UPDATE"),
        new("manifest", "PhysicalStockMovement", "INSERT"),
        new("manifest", "LedgerEffect", "INSERT"),
        new("b20", "StockRight", "UPDATE"),
        new("b20", "PhysicalStockBalance", "UPDATE"),
        new("b20", "SaleDelivery", "INSERT"),
        new("b20", "ReceivableOpenItem", "INSERT"),
        new("b20", "CashReceipt", "INSERT"),
        new("b20", "ReceiptAllocation", "INSERT"),
        new("b20", "BGrantConsumption", "INSERT"),
        new("b20", "LedgerEffect", "INSERT"),
        new("a25", "StockRight", "UPDATE"),
        new("a25", "PhysicalStockBalance", "UPDATE"),
        new("a25", "ReceivableOpenItem", "INSERT"),
        new("a25", "AOrder", "UPDATE"),
        new("a25", "LedgerEffect", "INSERT"),
        new("offer", "CashHandoverOffer", "INSERT"),
        new("accept", "CashHandoverAcceptance", "INSERT"),
        new("accept", "LedgerEffect", "INSERT"),
        new("unload", "TripUnload", "INSERT"),
        new("unload", "TripUnloadLine", "INSERT"),
        new("unload", "StockRight", "UPDATE"),
        new("unload", "StockPool", "UPDATE"),
        new("unload", "PhysicalStockBalance", "UPDATE"),
        new("unload", "Trip", "UPDATE"),
        new("unload", "LedgerEffect", "INSERT")
    ];

    public static IEnumerable<object[]> RollbackCases()
    {
        foreach (var point in FaultPoints)
        {
            yield return ["tenant-a", point.Scenario, point.Table, point.Operation];
            yield return ["tenant-b", point.Scenario, point.Table, point.Operation];
        }
    }

    private sealed class SqlJsonTable
    {
        public string TableName { get; set; } = "";
        public string RowsJson { get; set; } = "";
    }

    // Including row values (not just counts) is essential: every table's all
    // columns are serialized, rows sorted canonically and hashed. This catches
    // an orphan, a changed amount, a torn stock movement or a frozen price.
    private sealed record FullSqlState(string Database, int Tables, int Rows, string Sha256);

    private async Task<FullSqlState> FullSql(string tenant)
    {
        await using var db = await App01e3SqlFault.OpenOwnedScratchAsync(Connection(tenant), tenant);
        string database = await db.QuerySingleAsync<string>("SELECT DB_NAME()");
        string[] names = (await db.QueryAsync<string>("""
            SELECT name FROM sys.tables
            WHERE schema_id=SCHEMA_ID(N'dbo') AND is_ms_shipped=0
            ORDER BY name
            """)).ToArray();
        Assert.True(names.Length >= 25, "Full SQL fingerprint unexpectedly skipped business tables.");
        foreach (string required in new[] { "BusinessCommand", "BusinessSource",
            "BusinessSourceLine", "LedgerEffect", "StockRight", "StockPool",
            "PhysicalStockBalance", "SaleDelivery", "ReceivableOpenItem",
            "CashReceipt", "ReceiptAllocation", "CashHandoverAcceptance", "TripUnload" })
            Assert.Contains(required, names);

        // One SQL round trip for all schema-discovered business tables. Object
        // names originate from sys.tables, not an HTTP request or test input.
        string batch = string.Join("\nUNION ALL\n", names.Select(name =>
        {
            string identifier = name.Replace("]", "]]", StringComparison.Ordinal);
            string literal = name.Replace("'", "''", StringComparison.Ordinal);
            return $"SELECT N'{literal}' AS TableName, " +
                $"(SELECT * FROM dbo.[{identifier}] FOR JSON PATH, INCLUDE_NULL_VALUES) AS RowsJson";
        }));
        var data = (await db.QueryAsync<SqlJsonTable>(batch)).ToArray();
        Assert.Equal(names.Length, data.Length);
        var sb = new StringBuilder();
        int rowCount = 0;
        foreach (var table in data.OrderBy(x => x.TableName, StringComparer.Ordinal))
        {
            // SQL Server FOR JSON PATH returns empty text for an empty result,
            // not a JSON array. Represent that table as a canonical empty set.
            using var json = JsonDocument.Parse(
                string.IsNullOrWhiteSpace(table.RowsJson) ? "[]" : table.RowsJson);
            string[] rows = json.RootElement.EnumerateArray()
                .Select(x => x.GetRawText()).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            rowCount += rows.Length;
            sb.Append(table.TableName).Append('\0').Append(rows.Length).Append('\n');
            foreach (string row in rows) sb.Append(row).Append('\n');
        }
        string digest = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
        return new FullSqlState(database, names.Length, rowCount, digest);
    }

    private static IApp01aCommand DifferentSource(IApp01aCommand command) => command switch
    {
        ReserveAStockCommand c => c with { SourceNumber = Id() },
        GrantBQuotaCommand c => c with { SourceNumber = Id() },
        ConfirmAOrderCommand c => c with { SourceNumber = Id() },
        GrantBCreditCommand c => c with { SourceNumber = Id() },
        LoadManifestCommand c => c with { SourceNumber = Id() },
        PostBSaleDeliveryReceiptCommand c => c with { SourceNumber = Id() },
        PostAPartialDeliveryCommand c => c with { SourceNumber = Id() },
        OfferCashHandoverCommand c => c with { SourceNumber = Id() },
        AcceptCashHandoverCommand c => c with { SourceNumber = Id() },
        UnloadTripRemainderCommand c => c with { SourceNumber = Id() },
        _ => throw new InvalidOperationException("APP01E3 unregistered command kind.")
    };

    private async Task<Request> E3Request(TestApiFactory factory,
        HttpClient setup, string tenant, string scenario, Scene scene)
    {
        switch (scenario)
        {
            case "reserve":
                return Req(A + "reserve-a-stock",
                    new ReserveAStockCommand(E3Producer, Id(), Id(),
                        scene.Order, scene.OrderLine, scene.Warehouse, 40m),
                    "warehouse", WarehouseActor, "STOCK_ALLOC", 1, 1);
            case "quota":
                return Req(A + "grant-b-quota",
                    new GrantBQuotaCommand(E3Producer, Id(), Id(),
                        scene.Trip, scene.Warehouse, scene.Sku, 30m),
                    "warehouse", WarehouseActor, "STOCK_ALLOC", 1, 1);
            case "confirm":
                await Apply(setup, A + "reserve-a-stock",
                    new ReserveAStockCommand(Producer, Id(), Id(), scene.Order,
                        scene.OrderLine, scene.Warehouse, 40m), "A_STOCK_RESERVED");
                return Req(A + "confirm-a-order",
                    new ConfirmAOrderCommand(E3Producer, Id(), Id(), scene.Order),
                    "sales", SalesActor, "A_CONFIRM", 1, 0);
            case "credit":
                return Req(A + "grant-b-credit",
                    new GrantBCreditCommand(E3Producer, Id(), Id(),
                        scene.Trip, scene.Customer, scene.ShipTo,
                        "B", "SYNTHETIC_UNITLESS", 100m),
                    "warehouse", WarehouseActor, "B_CREDIT_GRANT", 1, 1);
            case "manifest":
                await Prepare(setup, tenant, scene, load: false);
                return Req(B + "load-manifest",
                    new LoadManifestCommand(E3Producer, Id(), Id(), scene.Manifest),
                    "warehouse", WarehouseActor, "MANIFEST_LOAD", 2, 4);
            case "b20":
                await Prepare(setup, tenant, scene, load: true);
                return Req(C + "post-b-sale-delivery-receipt",
                    B20(scene, CashWitness(tenant, scene)) with
                    { ProducerId = E3Producer, CommandId = Id(), SourceNumber = Id() },
                    "sales", SalesActor, "B_SALE_DELIVERY", 1, 7);
            case "a25":
                await Prepare(setup, tenant, scene, load: true);
                return Req(C + "post-a-partial-delivery",
                    A25(scene) with
                    { ProducerId = E3Producer, CommandId = Id(), SourceNumber = Id() },
                    "sales", SalesActor, "A_DELIVERY", 1, 4);
            case "offer":
                await Prepare(setup, tenant, scene, load: true);
                await Apply(setup, C + "post-b-sale-delivery-receipt",
                    B20(scene, CashWitness(tenant, scene)), "B_SALE_DELIVERY_POSTED");
                return Req(D + "offer-cash-handover",
                    new OfferCashHandoverCommand(E3Producer, Id(), Id(), scene.Trip, 200m),
                    "sales", SalesActor, "CASH_HANDOVER_OFFER", 1, 0);
            case "accept":
            {
                await Prepare(setup, tenant, scene, load: true);
                var (offer, count) = await SeedCashier(setup, tenant, scene);
                return Req(D + "accept-cash-handover",
                    new AcceptCashHandoverCommand(E3Producer, Id(), Id(),
                        scene.Trip, offer, count),
                    "cashier", CashierActor, "CASH_HANDOVER_ACCEPT", 1, 2);
            }
            case "unload":
            {
                await Prepare(setup, tenant, scene, load: true);
                var (offer, count) = await SeedCashier(setup, tenant, scene);
                using var cashier = Client(factory, tenant, CashierActor, "cashier");
                await Apply(cashier, D + "accept-cash-handover",
                    new AcceptCashHandoverCommand(Producer, Id(), Id(),
                        scene.Trip, offer, count), "CASH_HANDOVER_ACCEPTED");
                return Req(D + "unload-trip-remainder",
                    Unload(scene) with
                    { ProducerId = E3Producer, CommandId = Id(), SourceNumber = Id() },
                    "warehouse", WarehouseActor, "TRIP_UNLOAD", 2, 5);
            }
            default:
                throw new InvalidOperationException("Unknown APP01E3 matrix scenario.");
        }
    }

    private static bool IncludesSqlFault(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
            if (current is SqlException sql && sql.Number == App01e3SqlFault.ErrorNumber
                || current.Message.Contains("APP01E3_INJECT_PRECOMMIT", StringComparison.Ordinal))
                return true;
        return false;
    }

    private static async Task<string> InjectedFailure(HttpClient client, Request request)
    {
        try
        {
            using var body = JsonContent.Create(request.Payload, request.Payload.GetType());
            using var response = await client.PostAsync(
                request.Path, body, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
            return "HTTP_500";
        }
        catch (Exception ex) when (IncludesSqlFault(ex))
        {
            return "SQL_51063";
        }
    }

    private static void SqlUnchanged(SqlSnapshot before, SqlSnapshot after,
        FullSqlState fullBefore, FullSqlState fullAfter)
    {
        Assert.Equal(fullBefore.Database, fullAfter.Database);
        Assert.Equal(fullBefore.Tables, fullAfter.Tables);
        Assert.Equal(fullBefore.Rows, fullAfter.Rows);
        Assert.Equal(fullBefore.Sha256, fullAfter.Sha256);
        Assert.Equal(before.Numbers.Cells().ToArray(), after.Numbers.Cells().ToArray());
        Assert.Equal(before.TripStatus, after.TripStatus);
        Assert.Equal(before.ManifestStatus, after.ManifestStatus);
        Assert.Equal(before.LedgerSha256, after.LedgerSha256);
    }

    private static Cell[] NumberDiffs(string stage, SqlSnapshot expected, SqlSnapshot actual)
    {
        var x = expected.Numbers.Cells();
        var y = actual.Numbers.Cells();
        Assert.Equal(23, x.Count);
        Assert.Equal(x.Keys, y.Keys);
        return x.Select(k => new Cell(stage + "/" + k.Key, k.Value, y[k.Key])).ToArray();
    }

    private static void E3Evidence(string tenant, FaultPoint point, Request req,
        string faultResult, SqlAudit audit, Reply applied, Reply replay, Reply conflict,
        FullSqlState before, FullSqlState rollback, FullSqlState committed,
        FullSqlState replayed, FullSqlState conflicted,
        SqlSnapshot initial, SqlSnapshot failed, SqlSnapshot posted,
        SqlSnapshot replaySnapshot, SqlSnapshot conflictSnapshot)
    {
        Cell[] cells =
        [
            ..NumberDiffs("ROLLBACK", initial, failed),
            ..NumberDiffs("REPLAY", posted, replaySnapshot),
            ..NumberDiffs("CONFLICT", posted, conflictSnapshot)
        ];
        bool passed = cells.All(c => c.Match)
            && before.Sha256 == rollback.Sha256
            && committed.Sha256 == replayed.Sha256
            && committed.Sha256 == conflicted.Sha256
            && committed.Sha256 != before.Sha256
            && faultResult is "HTTP_500" or "SQL_51063"
            && audit.Status == "APPLIED"
            && audit.SourceId == applied.Outcome.SourceId
            && !applied.Outcome.Replayed && replay.Outcome.Replayed
            && conflict.Outcome.Status == "CONFLICT"
            && conflict.Outcome.ReasonCode == "IDEMPOTENCY_PAYLOAD_MISMATCH";
        var report = new
        {
            Schema = "APP01E3_ROLLBACK_V1",
            TestRunId = Environment.GetEnvironmentVariable("APP01E3_TEST_RUN_ID") ?? "LOCAL_UNTRACKED",
            SourceHeadSha = Environment.GetEnvironmentVariable("APP01E3_SOURCE_HEAD_SHA"),
            WorkflowSha = Environment.GetEnvironmentVariable("GITHUB_SHA"),
            TenantId = tenant, Scenario = point.Scenario, FaultPoint = point.Id,
            FaultTable = point.Table, FaultOperation = point.Operation,
            FaultOutcome = faultResult,
            Command = new { req.Payload.ProducerId, req.Payload.CommandId,
                req.Payload.SourceNumber, req.Actor, req.SourceKind,
                ExpectedLines = req.Lines, ExpectedEffects = req.Effects },
            Database = before.Database,
            Before = before, AfterFault = rollback, AfterCommit = committed,
            AfterReplay = replayed, AfterConflict = conflicted,
            SqlLineage = new
            {
                PreCommit = initial.LedgerSha256,
                RolledBack = failed.LedgerSha256,
                Applied = posted.LedgerSha256,
                Replayed = replaySnapshot.LedgerSha256,
                Conflicted = conflictSnapshot.LedgerSha256
            },
            Retry = new { Http = (int)applied.HttpStatus, applied.Outcome, Audit = audit },
            Replay = new { Http = (int)replay.HttpStatus, replay.Outcome },
            Conflict = new { Http = (int)conflict.HttpStatus, conflict.Outcome },
            ExpectedCells = cells.Length, MatchedCells = cells.Count(c => c.Match),
            Cells = cells,
            Status = passed ? "PASS" : "FAIL"
        };
        string folder = Path.Combine(AppContext.BaseDirectory, "TestResults", "APP-01E3");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, $"{tenant}-{point.Id}-rollback.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false));
        Assert.True(passed, "APP01E3 persisted SQL retry matrix invariant failed.");
    }

    [Fact]
    public async Task App01e3_fault_DDL_refuses_unowned_cross_tenant_and_nonlocaldb_targets()
    {
        var master = new SqlConnectionStringBuilder(Connection("tenant-a"))
        {
            InitialCatalog = "master"
        };
        var external = new SqlConnectionStringBuilder(Connection("tenant-a"))
        {
            DataSource = "localhost"
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            App01e3SqlFault.ArmAsync(master.ConnectionString, "tenant-a",
                "LedgerEffect", "INSERT"));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            App01e3SqlFault.ArmAsync(external.ConnectionString, "tenant-a",
                "LedgerEffect", "INSERT"));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            App01e3SqlFault.ArmAsync(Connection("tenant-a"), "tenant-b",
                "LedgerEffect", "INSERT"));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            App01e3SqlFault.ArmAsync(Connection("tenant-a"), "tenant-a",
                "Customer", "DELETE"));
    }

    [Theory]
    [MemberData(nameof(RollbackCases))]
    public async Task Precommit_failure_restores_all_SQL_then_safe_retry_replay_and_payload_conflict(
        string tenant, string scenario, string table, string operation)
    {
        var point = new FaultPoint(scenario, table, operation);
        using var factory = fixture.CreateFactory();
        using var setup = SqlScratchFixture.WithTenant(factory.CreateClient(), tenant);
        Scene scene = Seed(tenant);
        Request req = await E3Request(factory, setup, tenant, scenario, scene);
        SqlSnapshot initial = ReadSql(tenant, scene);
        FullSqlState before = await FullSql(tenant);
        Assert.Equal(0, await CountCommand(tenant, req.Payload));

        using var client = Client(factory, tenant, req.Actor, req.Role);
        string faultResult;
        SqlSnapshot rolledBack;
        FullSqlState afterFault;
        await using (var fault = await App01e3SqlFault.ArmAsync(
            Connection(tenant), tenant, table, operation))
        {
            faultResult = await InjectedFailure(client, req);
            rolledBack = ReadSql(tenant, scene);
            afterFault = await FullSql(tenant);
            SqlUnchanged(initial, rolledBack, before, afterFault);
            Assert.Equal(0, await CountCommand(tenant, req.Payload));
        }

        // A rollback erases the idempotency key: the same identity must be
        // freshly APPLIED, not treated as a replay or replaced by a new key.
        using var retryClient = Client(factory, tenant, req.Actor, req.Role);
        Reply applied = await Send(retryClient, req.Path, req.Payload);
        Assert.Equal(HttpStatusCode.OK, applied.HttpStatus);
        Assert.Equal("APPLIED", applied.Outcome.Status);
        Assert.False(applied.Outcome.Replayed);
        Assert.NotNull(applied.Outcome.SourceId);
        SqlAudit audit = Audit(tenant, req.Payload);
        AssertCommandFromSql(tenant, scene, req, applied, audit);
        Assert.Equal(1, await CountCommand(tenant, req.Payload));
        SqlSnapshot posted = ReadSql(tenant, scene);
        FullSqlState committed = await FullSql(tenant);
        Assert.NotEqual(before.Sha256, committed.Sha256);
        Assert.True(committed.Rows > before.Rows);

        Reply replay = await Send(retryClient, req.Path, req.Payload);
        Assert.Equal(HttpStatusCode.OK, replay.HttpStatus);
        Assert.True(replay.Outcome.Replayed);
        Assert.Equal(applied.Outcome, replay.Outcome with { Replayed = false });
        SqlSnapshot afterReplay = ReadSql(tenant, scene);
        FullSqlState replayed = await FullSql(tenant);
        SqlUnchanged(posted, afterReplay, committed, replayed);

        // Same (tenant, producer, command id), changed canonical source number.
        // The durable hash must reject it WITHOUT another source or ledger leg.
        Reply conflict = await Send(retryClient, req.Path, DifferentSource(req.Payload));
        Assert.Equal(HttpStatusCode.Conflict, conflict.HttpStatus);
        Assert.Equal("CONFLICT", conflict.Outcome.Status);
        Assert.Equal("IDEMPOTENCY_PAYLOAD_MISMATCH", conflict.Outcome.ReasonCode);
        Assert.False(conflict.Outcome.Replayed);
        Assert.Null(conflict.Outcome.SourceId);
        SqlSnapshot afterConflict = ReadSql(tenant, scene);
        FullSqlState conflicted = await FullSql(tenant);
        SqlUnchanged(posted, afterConflict, committed, conflicted);
        Assert.Equal(1, await CountCommand(tenant, req.Payload));

        E3Evidence(tenant, point, req, faultResult, audit, applied, replay, conflict,
            before, afterFault, committed, replayed, conflicted,
            initial, rolledBack, posted, afterReplay, afterConflict);
    }

    private async Task<int> CountCommand(string tenant, IApp01aCommand command)
    {
        await using var db = await App01e3SqlFault.OpenOwnedScratchAsync(Connection(tenant), tenant);
        return await db.ExecuteScalarAsync<int>("""
            SELECT COUNT(*) FROM dbo.BusinessCommand
            WHERE TenantId=@Tenant AND ProducerId=@Producer AND CommandId=@Command
            """, new { Tenant = tenant, Producer = command.ProducerId, Command = command.CommandId });
    }
}
