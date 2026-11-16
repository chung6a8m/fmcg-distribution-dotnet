using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Dapper;
using FmcgDms.Api.Persistence;
using Microsoft.Data.SqlClient;
using Xunit;

namespace FmcgDms.Api.Tests;

/// <summary>
/// APP-01C.4: independent AC-03 T0..T4 SQL actual/diff oracle, 65 fields per tenant.
/// Reads immutable expected cells from the locked Business Baseline. Both
/// database-per-tenant runs write independently auditable actual/diff evidence.
/// </summary>
[Collection("SqlScratch")]
public sealed class App01c4GoldenOracleTests(SqlScratchFixture fixture)
{
    private const string TenantA = "tenant-a";
    private const string TenantB = "tenant-b";
    private const string Producer = "app01c4-oracle";
    private const string ACommands = "/api/synthetic/app01a/commands/";
    private const string BCommands = "/api/synthetic/app01b/commands/";
    private const string CCommands = "/api/synthetic/app01c/commands/";

    private sealed record Scene(Guid Sku, Guid Warehouse, Guid Vehicle, Guid Trip,
        Guid Order, Guid OrderLine, Guid Manifest, Guid Customer, Guid ShipTo,
        Guid AManifestLine, Guid BManifestLine);
    private sealed record Oracle(string[] Columns, JsonElement[][] Rows, string Sha256);
    private sealed record Cell(int Row, string Field, string Expected, string Actual,
        decimal? Difference, bool Match);
    private sealed record Evidence(string ManifestStatus, decimal OpeningQty,
        decimal OpeningCost, decimal PoolAllocated, int Rights, int ManifestLines,
        int Sources, int SourceLines, int Movements, int PhysicalLegs,
        decimal PhysicalLegQtyNet, decimal PhysicalLegCostNet,
        int NonPhysicalEffects, int UnknownSources, int RejectedCommands,
        int LedgerRows, string LedgerDigest, int RejectedCommandSources);
    // Full source-backed ledger identity; rights effects must not be invisible to T2.
    private sealed class LedgerEntry
    {
        public Guid Id { get; set; }
        public Guid BusinessSourceLineId { get; set; }
        public string EffectKey { get; set; } = "";
        public string LedgerKind { get; set; } = "";
        public Guid? CustomerId { get; set; }
        public Guid? SkuId { get; set; }
        public Guid? StockLocationId { get; set; }
        public decimal QuantityDelta { get; set; }
        public decimal AmountDelta { get; set; }
    }
    private sealed class PostingEvidence
    {
        public int Sales { get; set; }
        public int BSales { get; set; }
        public int ASales { get; set; }
        public int SourceLinked { get; set; }
        public int Issues { get; set; }
        public int ArItems { get; set; }
        public int Receipts { get; set; }
        public int Allocations { get; set; }
        public int GrantConsumptions { get; set; }
        public int WitnessMatches { get; set; }
        public int AReceipts { get; set; }
        public int AGrantConsumptions { get; set; }
        public int BEffectRows { get; set; }
        public int AEffectRows { get; set; }
        public int FrozenSnapshots { get; set; }
        public decimal FrozenUnitPrice { get; set; }
        public int FrozenTermsDays { get; set; }
        public decimal IssuedQty { get; set; }
        public decimal IssuedCost { get; set; }
        public decimal SaleQty { get; set; }
        public decimal OriginalAr { get; set; }
        public decimal AppliedAr { get; set; }
        public decimal OutstandingAr { get; set; }
        public decimal ReceiptReceived { get; set; }
        public decimal ReceiptApplied { get; set; }
        public decimal GrantConsumed { get; set; }
        public decimal SourceCreditConsumed { get; set; }
        public DateTime TripDate { get; set; }
        public DateTime? ADue { get; set; }
        public string OrderStatus { get; set; } = "";
    }
    private sealed class PostingEffectRow
    {
        public string SourceKind { get; set; } = "";
        public string EffectKey { get; set; } = "";
        public string LedgerKind { get; set; } = "";
        public decimal QuantityDelta { get; set; }
        public decimal AmountDelta { get; set; }
    }
    private sealed record Snapshot(string Step, Dictionary<string, decimal> Values,
        Evidence Evidence, PostingEvidence Posting, string PostingDigest);
    private sealed record Observation(int Row, Snapshot Snapshot);

    private static string Id() => Guid.NewGuid().ToString("N");
    private SqlConnection Open(string tenant) =>
        new(tenant == TenantA ? fixture.TenantAConnectionString : fixture.TenantBConnectionString);

    private static Oracle ReadOracle()
    {
        byte[] bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "golden-oracles.json"));
        using var json = JsonDocument.Parse(bytes);
        var ac03 = json.RootElement.GetProperty("AC-03");
        Assert.Equal("MODEL_ONLY", ac03.GetProperty("status").GetString());
        Assert.Equal("SYNTHETIC_UNITLESS", ac03.GetProperty("unit").GetString());
        Assert.False(ac03.GetProperty("invoiceEligible").GetBoolean());
        string[] columns = ac03.GetProperty("cols").EnumerateArray()
            .Select(c => c.GetString()!).ToArray();
        Assert.Equal(new[] { "step", "mainQty", "tripQty", "soldQty", "mainCost",
            "tripCost", "cogs", "aHold", "bQuota", "arA", "arB", "custody", "fund" }, columns);
        var rows = ac03.GetProperty("steps").EnumerateArray()
            .Take(5).Select(row => row.EnumerateArray().Select(cell => cell.Clone()).ToArray())
            .ToArray();
        Assert.Equal(5, rows.Length);
        Assert.All(rows, row => Assert.Equal(13, row.Length));
        Assert.Equal(new[] { "T0", "T1_LOAD", "T2_REJECT", "T3_B20", "T4_A25" },
            rows.Select(row => row[0].GetString()).ToArray());
        return new Oracle(columns, rows, Convert.ToHexString(SHA256.HashData(bytes)));
    }

    private static List<Cell> Compare(Oracle oracle, int row,
        string actualStep, IReadOnlyDictionary<string, decimal> values)
    {
        var cells = new List<Cell>(13);
        for (int i = 0; i < oracle.Columns.Length; i++)
        {
            string field = oracle.Columns[i];
            if (i == 0)
            {
                string expected = oracle.Rows[row][i].GetString()!;
                cells.Add(new Cell(row, field, expected, actualStep, null,
                    StringComparer.Ordinal.Equals(expected, actualStep)));
            }
            else
            {
                decimal expected = oracle.Rows[row][i].GetDecimal();
                if (!values.TryGetValue(field, out decimal actual))
                {
                    cells.Add(new Cell(row, field, expected.ToString(CultureInfo.InvariantCulture),
                        "<missing>", null, false));
                    continue;
                }
                cells.Add(new Cell(row, field, expected.ToString(CultureInfo.InvariantCulture),
                    actual.ToString(CultureInfo.InvariantCulture), actual - expected, actual == expected));
            }
        }
        return cells;
    }

    [Fact]
    public void Comparator_detects_both_persisted_step_and_numeric_drift()
    {
        var oracle = ReadOracle();
        var first = oracle.Rows[0];
        var values = Enumerable.Range(1, 12).ToDictionary(
            i => oracle.Columns[i], i => first[i].GetDecimal());
        values["mainQty"] += 1m;
        var diff = Compare(oracle, 0, "NOT_T0", values).Where(c => !c.Match).ToArray();
        Assert.Equal(new[] { "step", "mainQty" }, diff.Select(c => c.Field));
        Assert.Equal(1m, diff[1].Difference);
    }

    private async Task<Scene> Prepare(HttpClient client, string tenant)
    {
        var s = new Scene(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        using (var db = Open(tenant))
        {
            db.Execute("""
                INSERT dbo.Customer(Id,TenantId,Code,Name)
                    VALUES(@Customer,@Tenant,@CustomerCode,N'APP01C4 Customer');
                INSERT dbo.ShipToSnapshot(Id,TenantId,CustomerId,DisplayName,Address)
                    VALUES(@ShipTo,@Tenant,@Customer,N'ShipTo',N'Synthetic');
                INSERT dbo.Sku(Id,TenantId,Code,Name,BaseUom)
                    VALUES(@Sku,@Tenant,@SkuCode,N'APP01C4 SKU',N'EA');
                INSERT dbo.StockLocation(Id,TenantId,Code,Kind)
                    VALUES(@Warehouse,@Tenant,@WhCode,N'WAREHOUSE');
                INSERT dbo.StockLocation(Id,TenantId,Code,Kind)
                    VALUES(@Vehicle,@Tenant,@VehicleCode,N'VEHICLE');
                INSERT dbo.Trip(Id,TenantId,Code,BusinessDate,VehicleLocationId)
                    VALUES(@Trip,@Tenant,@TripCode,CONVERT(date,SYSUTCDATETIME()),@Vehicle);
                INSERT dbo.AOrder(Id,TenantId,OrderNumber,CustomerId,ShipToSnapshotId,
                    TripId,BusinessDate)
                    VALUES(@Order,@Tenant,@OrderCode,@Customer,@ShipTo,@Trip,
                        CONVERT(date,SYSUTCDATETIME()));
                INSERT dbo.AOrderLine(Id,TenantId,AOrderId,LineNumber,SkuId,
                    BaseQuantity,UomCode,UomToBaseFactor,UnitPrice)
                    VALUES(@OrderLine,@Tenant,@Order,1,@Sku,40,N'EA',1,15);
                INSERT dbo.AOrderTermsApproval(TenantId,AOrderId,TripId,CustomerId,
                    TermsDays,Decision,PolicyVersion,ProposedBy,CheckedBy,
                    EvidenceRef,ApprovedAtUtc,EffectiveFromUtc,EffectiveUntilUtc)
                VALUES(@Tenant,@Order,@Trip,@Customer,7,N'APPROVED',N'C3-SYNTHETIC-v1',
                    N'synthetic-sales-maker',N'synthetic-finance-checker',N'A-terms-fixture',
                    DATEADD(minute,-1,SYSUTCDATETIME()),
                    DATEADD(day,-1,SYSUTCDATETIME()),DATEADD(day,7,SYSUTCDATETIME()));
                INSERT dbo.BCreditPool(TenantId,TripId,CustomerId,PoolCode,CurrencyUnit,ApprovedLimit)
                    VALUES(@Tenant,@Trip,@Customer,N'B',N'SYNTHETIC_UNITLESS',100);
                INSERT dbo.CreditPolicyApproval(Id,TenantId,TripId,CustomerId,PoolCode,
                    CurrencyUnit,CaseNumber,PolicyVersion,ProposedBy,CheckedBy,
                    Decision,Reason,EvidenceRef,ApprovedLimit,
                    EffectiveFromUtc,EffectiveUntilUtc,ReviewedAtUtc)
                VALUES(@Approval,@Tenant,@Trip,@Customer,N'B',N'SYNTHETIC_UNITLESS',
                    @ApprovalCase,N'C4-SYNTHETIC-v1',N'synthetic-actor',
                    N'synthetic-finance-checker',N'APPROVED',N'Test only',
                    N'approved-fixture',100,DATEADD(day,-1,SYSUTCDATETIME()),
                    DATEADD(day,1,SYSUTCDATETIME()),SYSUTCDATETIME());
                INSERT dbo.StockPool(TenantId,StockLocationId,SkuId,OnHandBaseQuantity)
                    VALUES(@Tenant,@Warehouse,@Sku,100);
                INSERT dbo.PhysicalStockOpening(TenantId,StockLocationId,SkuId,
                    SnapshotCode,BaseQuantity,CostAmount)
                    VALUES(@Tenant,@Warehouse,@Sku,N'APP01C4-T0',100,1000);
                INSERT dbo.PhysicalStockBalance(TenantId,StockLocationId,SkuId,
                    OnHandBaseQuantity,OnHandCostAmount)
                    VALUES(@Tenant,@Warehouse,@Sku,100,1000);
                INSERT dbo.PhysicalStockOpening(TenantId,StockLocationId,SkuId,
                    SnapshotCode,BaseQuantity,CostAmount)
                    VALUES(@Tenant,@Vehicle,@Sku,N'APP01C4-T0',0,0);
                INSERT dbo.PhysicalStockBalance(TenantId,StockLocationId,SkuId,
                    OnHandBaseQuantity,OnHandCostAmount)
                    VALUES(@Tenant,@Vehicle,@Sku,0,0);
                """, new {
                    Tenant = tenant, s.Customer, s.ShipTo,
                    Approval = Guid.NewGuid(), ApprovalCase = Id(),
                    s.Sku, s.Warehouse, s.Vehicle, s.Trip, s.Order, s.OrderLine,
                    CustomerCode = Id(), SkuCode = Id(), WhCode = Id(), VehicleCode = Id(),
                    TripCode = Id(), OrderCode = Id()
                });
        }

        await ExpectApplied(client, ACommands + "reserve-a-stock",
            new ReserveAStockCommand(Producer, Id(), Id(),
                s.Order, s.OrderLine, s.Warehouse, 40m), "A_STOCK_RESERVED");
        await ExpectApplied(client, ACommands + "grant-b-quota",
            new GrantBQuotaCommand(Producer, Id(), Id(),
                s.Trip, s.Warehouse, s.Sku, 30m), "B_QUOTA_GRANTED");
        await ExpectApplied(client, ACommands + "confirm-a-order",
            new ConfirmAOrderCommand(Producer, Id(), Id(), s.Order), "A_ORDER_CONFIRMED");

        using (var db = Open(tenant))
        {
            Guid rightA = db.QuerySingle<Guid>("""
                SELECT Id FROM dbo.StockRight
                WHERE TenantId=@Tenant AND TripId=@Trip AND SkuId=@Sku
                    AND StockLocationId=@Warehouse AND RightKind='A'
                """, new { Tenant = tenant, s.Trip, s.Sku, s.Warehouse });
            Guid rightB = db.QuerySingle<Guid>("""
                SELECT Id FROM dbo.StockRight
                WHERE TenantId=@Tenant AND TripId=@Trip AND SkuId=@Sku
                    AND StockLocationId=@Warehouse AND RightKind='B'
                """, new { Tenant = tenant, s.Trip, s.Sku, s.Warehouse });
            db.Execute("""
                INSERT dbo.TripManifest(Id,TenantId,ManifestNumber,TripId,
                    WarehouseLocationId,VehicleLocationId)
                    VALUES(@Manifest,@Tenant,@Number,@Trip,@Warehouse,@Vehicle);
                INSERT dbo.TripManifestLine(Id,TenantId,TripManifestId,TripId,
                    WarehouseLocationId,VehicleLocationId,LineNumber,SkuId,
                    StockRightId,RightKind,PlannedBaseQuantity)
                    VALUES(@ALine,@Tenant,@Manifest,@Trip,@Warehouse,@Vehicle,1,
                        @Sku,@RightA,'A',40);
                INSERT dbo.TripManifestLine(Id,TenantId,TripManifestId,TripId,
                    WarehouseLocationId,VehicleLocationId,LineNumber,SkuId,
                    StockRightId,RightKind,PlannedBaseQuantity)
                    VALUES(@BLine,@Tenant,@Manifest,@Trip,@Warehouse,@Vehicle,2,
                        @Sku,@RightB,'B',30);
                """, new {
                    Tenant = tenant, s.Manifest, s.Trip, s.Warehouse, s.Vehicle,
                    s.Sku, ALine = s.AManifestLine, BLine = s.BManifestLine,
                    RightA = rightA, RightB = rightB, Number = Id()
                });
        }
        return s;
    }

    private static async Task<BusinessCommandOutcome> Post<T>(
        HttpClient client, string path, T command, HttpStatusCode status = HttpStatusCode.OK)
    {
        using var response = await client.PostAsJsonAsync(
            path, command, TestContext.Current.CancellationToken);
        Assert.Equal(status, response.StatusCode);
        var value = await response.Content.ReadFromJsonAsync<BusinessCommandOutcome>(
            TestContext.Current.CancellationToken);
        return Assert.IsType<BusinessCommandOutcome>(value);
    }

    private static async Task ExpectApplied<T>(
        HttpClient client, string path, T command, string outcomeCode)
    {
        var result = await Post(client, path, command);
        Assert.Equal("APPLIED", result.Status);
        Assert.Equal(outcomeCode, result.OutcomeCode);
        Assert.NotNull(result.SourceId);
    }

    private Snapshot Capture(string tenant, Scene s, string rejectedCommandId)
    {
        using var db = Open(tenant);
        var args = new {
            Tenant = tenant, s.Sku, s.Warehouse, s.Vehicle, s.Trip, s.Manifest,
            Producer, RejectCommand = rejectedCommandId
        };
        (decimal mainQty, decimal mainCost) = db.QuerySingle<(decimal, decimal)>("""
            SELECT OnHandBaseQuantity,OnHandCostAmount
            FROM dbo.PhysicalStockBalance
            WHERE TenantId=@Tenant AND StockLocationId=@Warehouse AND SkuId=@Sku
            """, args);
        (decimal tripQty, decimal tripCost) = db.QuerySingle<(decimal, decimal)>("""
            SELECT OnHandBaseQuantity,OnHandCostAmount
            FROM dbo.PhysicalStockBalance
            WHERE TenantId=@Tenant AND StockLocationId=@Vehicle AND SkuId=@Sku
            """, args);
        (decimal openingQty, decimal openingCost) = db.QuerySingle<(decimal, decimal)>("""
            SELECT SUM(BaseQuantity),SUM(CostAmount)
            FROM dbo.PhysicalStockOpening
            WHERE TenantId=@Tenant AND SkuId=@Sku
                AND StockLocationId IN (@Warehouse,@Vehicle)
            """, args);
        (decimal aHold, decimal bQuota) = db.QuerySingle<(decimal, decimal)>("""
            SELECT
                COALESCE(SUM(CASE WHEN RightKind='A' AND Status=N'HELD'
                    THEN GrantedBaseQuantity-ConsumedBaseQuantity-ReleasedBaseQuantity ELSE 0 END),0),
                COALESCE(SUM(CASE WHEN RightKind='B' AND Status=N'HELD'
                    THEN GrantedBaseQuantity-ConsumedBaseQuantity-ReleasedBaseQuantity ELSE 0 END),0)
            FROM dbo.StockRight
            WHERE TenantId=@Tenant AND TripId=@Trip AND SkuId=@Sku
                AND StockLocationId=@Warehouse
            """, args);
        (decimal cogs, decimal arA, decimal arB, decimal custody, decimal fund,
            int nonPhysicalEffects) = db.QuerySingle<(decimal, decimal, decimal, decimal, decimal, int)>("""
            SELECT
                COALESCE(SUM(CASE WHEN e.LedgerKind=N'COGS' THEN e.AmountDelta ELSE 0 END),0),
                COALESCE(SUM(CASE WHEN e.LedgerKind=N'AR_A'
                    OR (e.LedgerKind=N'AR' AND src.SourceKind LIKE N'A%')
                    THEN e.AmountDelta ELSE 0 END),0),
                COALESCE(SUM(CASE WHEN e.LedgerKind=N'AR_B'
                    OR (e.LedgerKind=N'AR' AND src.SourceKind LIKE N'B%')
                    THEN e.AmountDelta ELSE 0 END),0),
                COALESCE(SUM(CASE WHEN e.LedgerKind=N'CUSTODY'
                    THEN e.AmountDelta ELSE 0 END),0),
                COALESCE(SUM(CASE WHEN e.LedgerKind=N'FUND'
                    THEN e.AmountDelta ELSE 0 END),0),
                COUNT(CASE WHEN e.LedgerKind NOT IN
                    (N'RIGHT_A',N'RIGHT_B',N'PHYSICAL_STOCK') THEN 1 END)
            FROM dbo.LedgerEffect e
            JOIN dbo.BusinessSourceLine sl ON sl.TenantId=e.TenantId
                AND sl.Id=e.BusinessSourceLineId
            JOIN dbo.BusinessSource src ON src.TenantId=sl.TenantId
                AND src.Id=sl.BusinessSourceId
            WHERE src.TenantId=@Tenant AND src.TripId=@Trip
            """, args);
        string step = db.QuerySingle<string>("""
            SELECT CASE
                WHEN EXISTS (SELECT 1 FROM dbo.SaleDelivery d
                    WHERE d.TenantId=@Tenant AND d.TripId=@Trip
                        AND d.RightKind='A' AND d.BusinessSourceKind=N'A_DELIVERY')
                    THEN N'T4_A25'
                WHEN EXISTS (SELECT 1 FROM dbo.SaleDelivery d
                    WHERE d.TenantId=@Tenant AND d.TripId=@Trip
                        AND d.RightKind='B' AND d.BusinessSourceKind=N'B_SALE_DELIVERY')
                    THEN N'T3_B20'
                WHEN EXISTS (SELECT 1 FROM dbo.BusinessCommand bc
                    WHERE bc.TenantId=@Tenant AND bc.ProducerId=@Producer
                        AND bc.CommandId=@RejectCommand AND bc.Status=N'REJECTED'
                        AND bc.OutcomeCode=N'REJECTED_QUOTA'
                        AND bc.ReasonCode=N'B_QUOTA_INSUFFICIENT')
                    THEN N'T2_REJECT'
                WHEN EXISTS (SELECT 1 FROM dbo.TripManifest m
                    JOIN dbo.BusinessSource src ON src.TenantId=m.TenantId
                        AND src.Id=m.LoadBusinessSourceId
                    WHERE m.TenantId=@Tenant AND m.Id=@Manifest
                        AND m.Status=N'LOADED' AND src.SourceKind=N'MANIFEST_LOAD')
                    THEN N'T1_LOAD'
                WHEN EXISTS (SELECT 1 FROM dbo.TripManifest m
                    WHERE m.TenantId=@Tenant AND m.Id=@Manifest
                        AND m.Status=N'DRAFT' AND m.LoadBusinessSourceId IS NULL)
                    THEN N'T0'
                ELSE N'UNKNOWN'
            END
            """, args);
        (int physicalLegs, decimal legQtyNet, decimal legCostNet) =
            db.QuerySingle<(int, decimal, decimal)>("""
                SELECT COUNT(*),COALESCE(SUM(e.QuantityDelta),0),
                    COALESCE(SUM(e.AmountDelta),0)
                FROM dbo.LedgerEffect e
                JOIN dbo.BusinessSourceLine sl ON sl.TenantId=e.TenantId
                    AND sl.Id=e.BusinessSourceLineId
                JOIN dbo.BusinessSource src ON src.TenantId=sl.TenantId
                    AND src.Id=sl.BusinessSourceId
                WHERE src.TenantId=@Tenant AND src.TripId=@Trip
                    AND e.LedgerKind=N'PHYSICAL_STOCK'
                """, args);
        string manifestStatus = db.QuerySingle<string>("""
            SELECT Status FROM dbo.TripManifest
            WHERE TenantId=@Tenant AND Id=@Manifest
            """, args);
        int sourceCount = db.ExecuteScalar<int>("""
            SELECT COUNT(*) FROM dbo.BusinessSource
            WHERE TenantId=@Tenant AND TripId=@Trip
            """, args);
        int sourceLines = db.ExecuteScalar<int>("""
            SELECT COUNT(*) FROM dbo.BusinessSourceLine sl
            JOIN dbo.BusinessSource src ON src.TenantId=sl.TenantId
                AND src.Id=sl.BusinessSourceId
            WHERE src.TenantId=@Tenant AND src.TripId=@Trip
            """, args);
        // Include RIGHT_A and RIGHT_B entries, not just physical or financial
        // effects. A rights-only posting on an existing source/line is invisible
        // to the 39 golden numeric cells and to source/line cardinality alone.
        var ledger = db.Query<LedgerEntry>("""
            SELECT e.Id,e.BusinessSourceLineId,e.EffectKey,e.LedgerKind,
                e.CustomerId,e.SkuId,e.StockLocationId,e.QuantityDelta,e.AmountDelta
            FROM dbo.LedgerEffect e
            JOIN dbo.BusinessSourceLine sl ON sl.TenantId=e.TenantId
                AND sl.Id=e.BusinessSourceLineId
            JOIN dbo.BusinessSource src ON src.TenantId=sl.TenantId
                AND src.Id=sl.BusinessSourceId
            WHERE src.TenantId=@Tenant AND src.TripId=@Trip
            ORDER BY e.Id
            """, args).ToArray();
        string ledgerDigest = Convert.ToHexString(
            SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(ledger)));
        // A rejected command must not own any business source, even if a buggy
        // implementation omitted or changed TripId on that source.
        int rejectedCommandSources = db.ExecuteScalar<int>("""
            SELECT COUNT(*)
            FROM dbo.BusinessSource src
            JOIN dbo.BusinessCommand bc ON bc.TenantId=src.TenantId
                AND bc.Id=src.BusinessCommandId
            WHERE bc.TenantId=@Tenant AND bc.ProducerId=@Producer
                AND bc.CommandId=@RejectCommand
            """, args);
        var evidence = new Evidence(manifestStatus, openingQty, openingCost,
            db.ExecuteScalar<decimal>("""
                SELECT AllocatedBaseQuantity FROM dbo.StockPool
                WHERE TenantId=@Tenant AND StockLocationId=@Warehouse AND SkuId=@Sku
                """, args),
            db.ExecuteScalar<int>("""
                SELECT COUNT(*) FROM dbo.StockRight
                WHERE TenantId=@Tenant AND TripId=@Trip AND SkuId=@Sku
                    AND StockLocationId=@Warehouse
                """, args),
            db.ExecuteScalar<int>("""
                SELECT COUNT(*) FROM dbo.TripManifestLine
                WHERE TenantId=@Tenant AND TripManifestId=@Manifest
                """, args),
            sourceCount, sourceLines,
            db.ExecuteScalar<int>("""
                SELECT COUNT(*) FROM dbo.PhysicalStockMovement mv
                JOIN dbo.TripManifestLine ml ON ml.TenantId=mv.TenantId
                    AND ml.Id=mv.TripManifestLineId
                WHERE ml.TenantId=@Tenant AND ml.TripManifestId=@Manifest
                """, args),
            physicalLegs, legQtyNet, legCostNet, nonPhysicalEffects,
            db.ExecuteScalar<int>("""
                SELECT COUNT(*) FROM dbo.BusinessSource
                WHERE TenantId=@Tenant AND TripId=@Trip
                    AND SourceKind NOT IN (N'STOCK_ALLOC',N'A_CONFIRM',N'MANIFEST_LOAD')
                """, args),
            db.ExecuteScalar<int>("""
                SELECT COUNT(*) FROM dbo.BusinessCommand
                WHERE TenantId=@Tenant AND ProducerId=@Producer
                    AND CommandId=@RejectCommand AND Status=N'REJECTED'
                    AND OutcomeCode=N'REJECTED_QUOTA'
                    AND ReasonCode=N'B_QUOTA_INSUFFICIENT'
                """, args),
            ledger.Length, ledgerDigest, rejectedCommandSources);
        // Sold quantity is custody depletion relative to SQL opening, not an
        // invented sale row. The unexpected-source/financial guards below make
        // it impossible to silently treat a stock loss or sale as a T1 transfer.
        var values = new Dictionary<string, decimal> {
            ["mainQty"] = mainQty, ["tripQty"] = tripQty,
            ["soldQty"] = openingQty - mainQty - tripQty,
            ["mainCost"] = mainCost, ["tripCost"] = tripCost, ["cogs"] = cogs,
            ["aHold"] = aHold, ["bQuota"] = bQuota, ["arA"] = arA,
            ["arB"] = arB, ["custody"] = custody, ["fund"] = fund
        };
        return new Snapshot(step, values, evidence);
    }

    private static void AssertPersistenceInvariants(Snapshot snapshot, int stage)
    {
        var e = snapshot.Evidence;
        bool loaded = stage != 0;
        Assert.Equal(loaded ? "LOADED" : "DRAFT", e.ManifestStatus);
        Assert.Equal(100m, e.OpeningQty);
        Assert.Equal(1000m, e.OpeningCost);
        Assert.Equal(70m, e.PoolAllocated);
        Assert.Equal(2, e.Rights);
        Assert.Equal(2, e.ManifestLines);
        Assert.Equal(loaded ? 4 : 3, e.Sources);
        Assert.Equal(loaded ? 5 : 3, e.SourceLines);
        Assert.Equal(loaded ? 2 : 0, e.Movements);
        Assert.Equal(loaded ? 4 : 0, e.PhysicalLegs);
        Assert.Equal(0m, e.PhysicalLegQtyNet);
        Assert.Equal(0m, e.PhysicalLegCostNet);
        Assert.Equal(0, e.NonPhysicalEffects);
        Assert.Equal(0, e.UnknownSources);
        Assert.Equal(loaded ? 6 : 2, e.LedgerRows); // two rights + four transfer legs
        Assert.Equal(0, e.RejectedCommandSources);
        Assert.Equal(stage == 2 ? 1 : 0, e.RejectedCommands);
    }

    private static void AssertNoBusinessEffectsChanged(Snapshot before, Snapshot after)
    {
        foreach (var (field, expected) in before.Values)
            Assert.Equal(expected, after.Values[field]);
        var b = before.Evidence;
        var a = after.Evidence;
        Assert.Equal(b.ManifestStatus, a.ManifestStatus);
        Assert.Equal(b.OpeningQty, a.OpeningQty);
        Assert.Equal(b.OpeningCost, a.OpeningCost);
        Assert.Equal(b.PoolAllocated, a.PoolAllocated);
        Assert.Equal(b.Rights, a.Rights);
        Assert.Equal(b.ManifestLines, a.ManifestLines);
        Assert.Equal(b.Sources, a.Sources);
        Assert.Equal(b.SourceLines, a.SourceLines);
        Assert.Equal(b.Movements, a.Movements);
        Assert.Equal(b.PhysicalLegs, a.PhysicalLegs);
        Assert.Equal(b.NonPhysicalEffects, a.NonPhysicalEffects);
        Assert.Equal(b.UnknownSources, a.UnknownSources);
        Assert.Equal(b.LedgerRows, a.LedgerRows);
        Assert.Equal(b.LedgerDigest, a.LedgerDigest);
        Assert.Equal(b.RejectedCommandSources, a.RejectedCommandSources);
    }

    private async Task WriteEvidence(string tenant, Oracle oracle,
        IReadOnlyList<Observation> reads, string? failure)
    {
        var cells = reads.SelectMany(r => Compare(oracle, r.Row,
            r.Snapshot.Step, r.Snapshot.Values)).ToArray();
        bool passed = failure is null && reads.Count == 3
            && cells.Length == 39 && cells.All(c => c.Match);
        string directory = Path.Combine(AppContext.BaseDirectory, "TestResults", "APP-01C4");
        Directory.CreateDirectory(directory);
        string file = Path.Combine(directory, tenant + "-oracle.json");
        var report = new {
            Schema = "APP01C4_SQL_ORACLE_V1",
            TenantId = tenant,
            Database = tenant == TenantA ? fixture.TenantADb : fixture.TenantBDb,
            Environment = "LocalDB / synthetic / invoiceEligible=false",
            TestRunId = Environment.GetEnvironmentVariable("APP01C4_TEST_RUN_ID") ?? "LOCAL_UNTRACKED",
            SourceHeadSha = Environment.GetEnvironmentVariable("APP01C4_SOURCE_HEAD_SHA"),
            WorkflowSha = Environment.GetEnvironmentVariable("GITHUB_SHA"),
            Baseline = "business/reference-data/expected-results/golden-oracles.json#AC-03",
            OracleSha256 = oracle.Sha256,
            ExpectedCells = 39, CheckedCells = cells.Length,
            MatchedCells = cells.Count(c => c.Match),
            Status = passed ? "PASS" : "FAIL_OR_INCOMPLETE",
            Steps = reads.Select(r => new { r.Row, r.Snapshot.Step,
                Actual = r.Snapshot.Values, r.Snapshot.Evidence }),
            Cells = cells,
            Failure = failure
        };
        await File.WriteAllTextAsync(file,
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }),
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Both_LocalDB_tenants_match_all_39_AC03_cells_from_persisted_T0_T1_T2()
    {
        var oracle = ReadOracle();
        var observations = new Dictionary<string, List<Observation>> {
            [TenantA] = [], [TenantB] = []
        };
        string? failure = null;
        try
        {
            using var factory = fixture.CreateFactory();
            using var a = SqlScratchFixture.WithTenant(factory.CreateClient(), TenantA);
            using var b = SqlScratchFixture.WithTenant(factory.CreateClient(), TenantB);
            Scene sceneA = await Prepare(a, TenantA);
            Scene sceneB = await Prepare(b, TenantB);
            var loadA = new LoadManifestCommand(Producer, Id(), Id(), sceneA.Manifest);
            var loadB = new LoadManifestCommand(Producer, Id(), Id(), sceneB.Manifest);
            var rejectA = new PreflightBSaleCommand(Producer, Id(), Id(),
                sceneA.Trip, sceneA.Sku, 31m, 15m, 465m);
            var rejectB = new PreflightBSaleCommand(Producer, Id(), Id(),
                sceneB.Trip, sceneB.Sku, 31m, 15m, 465m);

            Snapshot t0a = Capture(TenantA, sceneA, rejectA.CommandId);
            Snapshot t0b = Capture(TenantB, sceneB, rejectB.CommandId);
            observations[TenantA].Add(new Observation(0, t0a));
            observations[TenantB].Add(new Observation(0, t0b));
            AssertPersistenceInvariants(t0a, 0);
            AssertPersistenceInvariants(t0b, 0);

            await ExpectApplied(a, BCommands + "load-manifest", loadA, "MANIFEST_LOADED");
            Snapshot t1a = Capture(TenantA, sceneA, rejectA.CommandId);
            observations[TenantA].Add(new Observation(1, t1a));
            AssertPersistenceInvariants(t1a, 1);
            // One tenant's warehouse transfer must not mutate the other DB.
            AssertNoBusinessEffectsChanged(t0b, Capture(TenantB, sceneB, rejectB.CommandId));
            await ExpectApplied(b, BCommands + "load-manifest", loadB, "MANIFEST_LOADED");
            Snapshot t1b = Capture(TenantB, sceneB, rejectB.CommandId);
            observations[TenantB].Add(new Observation(1, t1b));
            AssertPersistenceInvariants(t1b, 1);
            AssertNoBusinessEffectsChanged(t1a, Capture(TenantA, sceneA, rejectA.CommandId));

            foreach (var (client, tenant, scene, reject, t1) in new[] {
                (a, TenantA, sceneA, rejectA, t1a),
                (b, TenantB, sceneB, rejectB, t1b)
            })
            {
                var denied = await Post(client, BCommands + "preflight-b-sale", reject);
                Assert.Equal("REJECTED", denied.Status);
                Assert.Equal("REJECTED_QUOTA", denied.OutcomeCode);
                Assert.Equal("B_QUOTA_INSUFFICIENT", denied.ReasonCode);
                Assert.Null(denied.SourceId);
                Assert.False(denied.Replayed);
                Snapshot t2 = Capture(tenant, scene, reject.CommandId);
                observations[tenant].Add(new Observation(2, t2));
                AssertPersistenceInvariants(t2, 2);
                AssertNoBusinessEffectsChanged(t1, t2);

                var replay = await Post(client, BCommands + "preflight-b-sale", reject);
                Assert.True(replay.Replayed);
                Assert.Equal("REJECTED_QUOTA", replay.OutcomeCode);
                Assert.Equal("B_QUOTA_INSUFFICIENT", replay.ReasonCode);
                var conflict = await Post(client, BCommands + "preflight-b-sale",
                    reject with { BaseQuantity = 30m, OfferedCashAmount = 450m },
                    HttpStatusCode.Conflict);
                Assert.Equal("IDEMPOTENCY_PAYLOAD_MISMATCH", conflict.ReasonCode);
                Snapshot afterRetry = Capture(tenant, scene, reject.CommandId);
                AssertPersistenceInvariants(afterRetry, 2);
                AssertNoBusinessEffectsChanged(t2, afterRetry);
                // Mutation sentinel: an extra/modified rights ledger row changes
                // the fingerprint although every golden numeric cell stays equal.
                var changedRightsLedger = t2 with {
                    Evidence = t2.Evidence with { LedgerDigest = "MUTATED_RIGHT_B" }
                };
                Assert.NotNull(Record.Exception(() =>
                    AssertNoBusinessEffectsChanged(t2, changedRightsLedger)));
            }

            foreach (string tenant in new[] { TenantA, TenantB })
            {
                var reads = observations[tenant];
                Assert.Equal(3, reads.Count);
                var cells = reads.SelectMany(r => Compare(oracle, r.Row,
                    r.Snapshot.Step, r.Snapshot.Values)).ToArray();
                Assert.Equal(39, cells.Length);
                Assert.True(cells.All(c => c.Match),
                    tenant + ": " + string.Join("; ",
                        cells.Where(c => !c.Match).Select(c =>
                            $"{c.Row}/{c.Field} expected={c.Expected}, actual={c.Actual}")));
            }
        }
        catch (Exception ex)
        {
            failure = ex.ToString();
            throw;
        }
        finally
        {
            foreach (string tenant in new[] { TenantA, TenantB })
                await WriteEvidence(tenant, oracle, observations[tenant], failure);
        }
    }
}
