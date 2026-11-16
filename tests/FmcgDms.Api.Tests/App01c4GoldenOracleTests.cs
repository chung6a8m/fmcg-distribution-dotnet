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
    private const string DCommands = "/api/synthetic/app01d/commands/";

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
        public int ValidAllocations { get; set; }
        public decimal AllocatedCash { get; set; }
        public int GrantConsumptions { get; set; }
        public int WitnessMatches { get; set; }
        public long WitnessTimestampDriftMicroseconds { get; set; }
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

    private static Oracle ReadOracle(int checkpoints = 5)
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
            .Take(checkpoints).Select(row => row.EnumerateArray().Select(cell => cell.Clone()).ToArray())
            .ToArray();
        Assert.Equal(checkpoints, rows.Length);
        Assert.All(rows, row => Assert.Equal(13, row.Length));
        Assert.Equal(new[] { "T0", "T1_LOAD", "T2_REJECT", "T3_B20", "T4_A25",
            "T5_HANDOVER", "T6_RETURN" }.Take(checkpoints),
            rows.Select(row => row[0].GetString()));
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

    private string SeedVerifiedCashWitness(string tenant, Scene scene)
    {
        string evidenceRef = Id();
        using var db = Open(tenant);
        db.Execute("""
            INSERT dbo.VerifiedCashEvidence(TenantId,EvidenceRef,TripId,CustomerId,
                ShipToSnapshotId,PaymentMethod,VerificationStatus,VerifiedAmount,
                VerifiedBy,VerifiedAtUtc)
            VALUES(@Tenant,@Ref,@Trip,@Customer,@ShipTo,N'CASH',N'VERIFIED',200,
                N'synthetic-cash-checker',SYSUTCDATETIME());
            """, new { Tenant = tenant, Ref = evidenceRef, scene.Trip,
                scene.Customer, scene.ShipTo });
        return evidenceRef;
    }

    [Fact]
    public void Comparator_fails_closed_on_missing_SQL_measurement()
    {
        var oracle = ReadOracle();
        var cells = Compare(oracle, 4, "T4_A25", new Dictionary<string, decimal>());
        Assert.Equal(13, cells.Count);
        Assert.Equal(12, cells.Count(c => !c.Match));
        Assert.All(cells.Skip(1), c => Assert.Equal("<missing>", c.Actual));
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
                WHEN EXISTS (SELECT 1 FROM dbo.TripUnload u
                    WHERE u.TenantId=@Tenant AND u.TripId=@Trip)
                    THEN N'T6_RETURN'
                WHEN EXISTS (SELECT 1 FROM dbo.CashHandoverAcceptance a
                    WHERE a.TenantId=@Tenant AND a.TripId=@Trip)
                    THEN N'T5_HANDOVER'
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
        // These observations deliberately re-read persisted commercial source,
        // stock issues, AR, receipts and grant counters from SQL. They do not
        // infer T3/T4 from the HTTP outcome or the golden expected cells.
        var posting = db.QuerySingle<PostingEvidence>("""
            SELECT
                (SELECT COUNT(*) FROM dbo.SaleDelivery
                    WHERE TenantId=@Tenant AND TripId=@Trip) Sales,
                (SELECT COUNT(*) FROM dbo.SaleDelivery
                    WHERE TenantId=@Tenant AND TripId=@Trip AND RightKind='B') BSales,
                (SELECT COUNT(*) FROM dbo.SaleDelivery
                    WHERE TenantId=@Tenant AND TripId=@Trip AND RightKind='A') ASales,
                (SELECT COUNT(*) FROM dbo.SaleDelivery d
                    JOIN dbo.BusinessSource src ON src.TenantId=d.TenantId
                        AND src.Id=d.BusinessSourceId AND src.SourceKind=d.BusinessSourceKind
                    JOIN dbo.BusinessSourceLine sl ON sl.TenantId=d.TenantId
                        AND sl.Id=d.BusinessSourceLineId AND sl.BusinessSourceId=src.Id
                        AND sl.SkuId=d.SkuId AND sl.BaseQuantity=d.BaseQuantity
                        AND sl.Amount=d.PayableAmount
                    JOIN dbo.TripManifestLine ml ON ml.TenantId=d.TenantId
                        AND ml.Id=d.TripManifestLineId AND ml.TripId=d.TripId
                        AND ml.StockRightId=d.StockRightId AND ml.LoadSourceLineId=d.LoadSourceLineId
                    WHERE d.TenantId=@Tenant AND d.TripId=@Trip) SourceLinked,
                (SELECT COUNT(*) FROM dbo.DeliveryStockIssue i
                    JOIN dbo.SaleDelivery d ON d.TenantId=i.TenantId AND d.Id=i.SaleDeliveryId
                    WHERE d.TenantId=@Tenant AND d.TripId=@Trip) Issues,
                (SELECT COUNT(*) FROM dbo.ReceivableOpenItem r
                    JOIN dbo.SaleDelivery d ON d.TenantId=r.TenantId AND d.Id=r.SaleDeliveryId
                    WHERE d.TenantId=@Tenant AND d.TripId=@Trip) ArItems,
                (SELECT COUNT(*) FROM dbo.CashReceipt
                    WHERE TenantId=@Tenant AND TripId=@Trip) Receipts,

                (SELECT COUNT(*) FROM dbo.ReceiptAllocation a
                    JOIN dbo.CashReceipt r ON r.TenantId=a.TenantId AND r.Id=a.CashReceiptId
                    WHERE r.TenantId=@Tenant AND r.TripId=@Trip) Allocations,
                (SELECT COUNT(*) FROM dbo.ReceiptAllocation a
                    JOIN dbo.CashReceipt r ON r.TenantId=a.TenantId AND r.Id=a.CashReceiptId
                    JOIN dbo.ReceivableOpenItem oi ON oi.TenantId=a.TenantId
                        AND oi.Id=a.ReceivableOpenItemId
                    JOIN dbo.SaleDelivery d ON d.TenantId=r.TenantId
                        AND d.Id=r.SaleDeliveryId
                    WHERE r.TenantId=@Tenant AND r.TripId=@Trip
                        AND d.RightKind='B' AND oi.SaleDeliveryId=d.Id
                        AND a.CustomerId=r.CustomerId
                        AND a.AppliedAmount=r.AppliedAmount
                        AND a.AppliedAmount=oi.AppliedAmount) ValidAllocations,
                (SELECT COALESCE(SUM(a.AppliedAmount),0) FROM dbo.ReceiptAllocation a
                    JOIN dbo.CashReceipt r ON r.TenantId=a.TenantId AND r.Id=a.CashReceiptId
                    WHERE r.TenantId=@Tenant AND r.TripId=@Trip) AllocatedCash,
                (SELECT COUNT(*) FROM dbo.BGrantConsumption
                    WHERE TenantId=@Tenant AND TripId=@Trip) GrantConsumptions,
                (SELECT COUNT(*) FROM dbo.CashReceipt r
                    JOIN dbo.SaleDelivery d ON d.TenantId=r.TenantId
                        AND d.Id=r.SaleDeliveryId
                    JOIN dbo.VerifiedCashEvidence w ON w.TenantId=r.TenantId
                        AND w.EvidenceRef=r.VerificationEvidenceRef
                        AND w.TripId=r.TripId AND w.CustomerId=r.CustomerId
                        AND w.ShipToSnapshotId=d.ShipToSnapshotId
                        AND w.VerifiedAmount=r.ReceivedAmount
                        AND w.VerificationStatus=N'VERIFIED'
                        AND w.VerifiedAtUtc IS NOT NULL
                        AND ABS(DATEDIFF_BIG(microsecond,w.VerifiedAtUtc,r.VerifiedAtUtc)) <= 3500
                    WHERE r.TenantId=@Tenant AND r.TripId=@Trip
                        AND r.VerificationStatus=N'VERIFIED') WitnessMatches,
                (SELECT COALESCE(MAX(ABS(DATEDIFF_BIG(microsecond,w.VerifiedAtUtc,
                    r.VerifiedAtUtc))),0) FROM dbo.CashReceipt r
                    JOIN dbo.VerifiedCashEvidence w ON w.TenantId=r.TenantId
                        AND w.EvidenceRef=r.VerificationEvidenceRef
                        AND w.TripId=r.TripId AND w.CustomerId=r.CustomerId
                        AND w.VerifiedAmount=r.ReceivedAmount
                        AND w.VerificationStatus=N'VERIFIED'
                    WHERE r.TenantId=@Tenant AND r.TripId=@Trip) WitnessTimestampDriftMicroseconds,
                (SELECT COUNT(*) FROM dbo.CashReceipt r
                    JOIN dbo.SaleDelivery d ON d.TenantId=r.TenantId AND d.Id=r.SaleDeliveryId
                    WHERE d.TenantId=@Tenant AND d.TripId=@Trip AND d.RightKind='A') AReceipts,
                (SELECT COUNT(*) FROM dbo.BGrantConsumption c
                    JOIN dbo.SaleDelivery d ON d.TenantId=c.TenantId AND d.Id=c.SaleDeliveryId
                    WHERE d.TenantId=@Tenant AND d.TripId=@Trip AND d.RightKind='A') AGrantConsumptions,
                (SELECT COUNT(*) FROM dbo.LedgerEffect e
                    JOIN dbo.BusinessSourceLine sl ON sl.TenantId=e.TenantId
                        AND sl.Id=e.BusinessSourceLineId
                    JOIN dbo.BusinessSource src ON src.TenantId=sl.TenantId
                        AND src.Id=sl.BusinessSourceId
                    WHERE src.TenantId=@Tenant AND src.TripId=@Trip
                        AND src.SourceKind=N'B_SALE_DELIVERY') BEffectRows,
                (SELECT COUNT(*) FROM dbo.LedgerEffect e
                    JOIN dbo.BusinessSourceLine sl ON sl.TenantId=e.TenantId
                        AND sl.Id=e.BusinessSourceLineId
                    JOIN dbo.BusinessSource src ON src.TenantId=sl.TenantId
                        AND src.Id=sl.BusinessSourceId
                    WHERE src.TenantId=@Tenant AND src.TripId=@Trip
                        AND src.SourceKind=N'A_DELIVERY') AEffectRows,
                (SELECT COUNT(*) FROM dbo.AConfirmedLineSnapshot
                    WHERE TenantId=@Tenant AND AOrderLineId=@OrderLine) FrozenSnapshots,
                COALESCE((SELECT UnitPriceSnapshot FROM dbo.AConfirmedLineSnapshot
                    WHERE TenantId=@Tenant AND AOrderLineId=@OrderLine),0) FrozenUnitPrice,
                COALESCE((SELECT TermsDaysSnapshot FROM dbo.AConfirmedLineSnapshot
                    WHERE TenantId=@Tenant AND AOrderLineId=@OrderLine),0) FrozenTermsDays,
                (SELECT COALESCE(SUM(i.BaseQuantity),0)
                    FROM dbo.DeliveryStockIssue i
                    JOIN dbo.SaleDelivery d ON d.TenantId=i.TenantId AND d.Id=i.SaleDeliveryId
                    WHERE d.TenantId=@Tenant AND d.TripId=@Trip) IssuedQty,
                (SELECT COALESCE(SUM(i.CostAmount),0)
                    FROM dbo.DeliveryStockIssue i
                    JOIN dbo.SaleDelivery d ON d.TenantId=i.TenantId AND d.Id=i.SaleDeliveryId
                    WHERE d.TenantId=@Tenant AND d.TripId=@Trip) IssuedCost,
                (SELECT COALESCE(SUM(BaseQuantity),0) FROM dbo.SaleDelivery
                    WHERE TenantId=@Tenant AND TripId=@Trip) SaleQty,
                (SELECT COALESCE(SUM(r.OriginalAmount),0) FROM dbo.ReceivableOpenItem r
                    JOIN dbo.SaleDelivery d ON d.TenantId=r.TenantId AND d.Id=r.SaleDeliveryId
                    WHERE d.TenantId=@Tenant AND d.TripId=@Trip) OriginalAr,
                (SELECT COALESCE(SUM(r.AppliedAmount),0) FROM dbo.ReceivableOpenItem r
                    JOIN dbo.SaleDelivery d ON d.TenantId=r.TenantId AND d.Id=r.SaleDeliveryId
                    WHERE d.TenantId=@Tenant AND d.TripId=@Trip) AppliedAr,
                (SELECT COALESCE(SUM(r.OutstandingAmount),0) FROM dbo.ReceivableOpenItem r
                    JOIN dbo.SaleDelivery d ON d.TenantId=r.TenantId AND d.Id=r.SaleDeliveryId
                    WHERE d.TenantId=@Tenant AND d.TripId=@Trip) OutstandingAr,
                (SELECT COALESCE(SUM(ReceivedAmount),0) FROM dbo.CashReceipt
                    WHERE TenantId=@Tenant AND TripId=@Trip) ReceiptReceived,
                (SELECT COALESCE(SUM(AppliedAmount),0) FROM dbo.CashReceipt
                    WHERE TenantId=@Tenant AND TripId=@Trip) ReceiptApplied,
                (SELECT COALESCE(SUM(ConsumedAmount),0) FROM dbo.BCreditGrant
                    WHERE TenantId=@Tenant AND TripId=@Trip AND CustomerId=@Customer) GrantConsumed,
                (SELECT COALESCE(SUM(ConsumedAmount),0) FROM dbo.BGrantConsumption
                    WHERE TenantId=@Tenant AND TripId=@Trip) SourceCreditConsumed,
                (SELECT BusinessDate FROM dbo.Trip
                    WHERE TenantId=@Tenant AND Id=@Trip) TripDate,
                (SELECT MIN(r.DueBusinessDate) FROM dbo.ReceivableOpenItem r
                    JOIN dbo.SaleDelivery d ON d.TenantId=r.TenantId AND d.Id=r.SaleDeliveryId
                    WHERE d.TenantId=@Tenant AND d.TripId=@Trip
                        AND d.RightKind='A') ADue,
                (SELECT Status FROM dbo.AOrder
                    WHERE TenantId=@Tenant AND Id=@Order) OrderStatus
            """, new { Tenant = tenant, s.Trip, s.Order, s.OrderLine, s.Customer });
        string postingDigest = Convert.ToHexString(
            SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(posting)));
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
        return new Snapshot(step, values, evidence, posting, postingDigest);
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
        Assert.Equal(before.PostingDigest, after.PostingDigest);
        Assert.Equal(b.RejectedCommandSources, a.RejectedCommandSources);
    }

    private void AssertPostedLineage(string tenant, Scene scene, Snapshot snapshot, int stage)
    {
        Assert.True(stage is 3 or 4);
        // The golden 65 cells alone cannot detect zero-net, wrong-kind or
        // cancelling extra source/effect rows. Require the exact persisted
        // posting envelope at each stage, including the B credit grant.
        var e = snapshot.Evidence;
        Assert.Equal("LOADED", e.ManifestStatus);
        Assert.Equal(100m, e.OpeningQty);
        Assert.Equal(1000m, e.OpeningCost);
        Assert.Equal(70m, e.PoolAllocated);
        Assert.Equal(2, e.Rights);
        Assert.Equal(2, e.ManifestLines);
        Assert.Equal(2, e.Movements);
        Assert.Equal(stage == 3 ? 6 : 7, e.Sources);
        Assert.Equal(stage == 3 ? 7 : 8, e.SourceLines);
        Assert.Equal(stage == 3 ? 14 : 18, e.LedgerRows);
        Assert.Equal(stage == 3 ? 5 : 6, e.PhysicalLegs);
        Assert.Equal(stage == 3 ? -20m : -45m, e.PhysicalLegQtyNet);
        Assert.Equal(stage == 3 ? -200m : -450m, e.PhysicalLegCostNet);
        Assert.Equal(stage == 3 ? 7 : 10, e.NonPhysicalEffects);
        Assert.Equal(stage == 3 ? 2 : 3, e.UnknownSources);
        Assert.Equal(0, e.RejectedCommandSources);
        Assert.Equal(1, e.RejectedCommands);

        var p = snapshot.Posting;
        Assert.Equal(stage == 3 ? 1 : 2, p.Sales);
        Assert.Equal(1, p.BSales);
        Assert.Equal(stage == 3 ? 0 : 1, p.ASales);
        Assert.Equal(p.Sales, p.SourceLinked);
        Assert.Equal(p.Sales, p.Issues);
        Assert.Equal(p.Sales, p.ArItems);
        Assert.Equal(1, p.Receipts);
        Assert.Equal(1, p.Allocations);
        Assert.Equal(1, p.ValidAllocations);
        Assert.Equal(200m, p.AllocatedCash);
        Assert.Equal(1, p.GrantConsumptions);
        Assert.Equal(1, p.WitnessMatches);
        // SqlClient may bind DateTime as SQL datetime (3.33ms precision) when
        // inserting receipt. Scope/ref/status/amount stay exact; record bounded
        // timestamp rounding independently rather than treating it as fraud.
        Assert.InRange(p.WitnessTimestampDriftMicroseconds, 0L, 3500L);
        Assert.Equal(0, p.AReceipts);
        Assert.Equal(0, p.AGrantConsumptions);
        Assert.Equal(7, p.BEffectRows);
        Assert.Equal(stage == 3 ? 0 : 4, p.AEffectRows);
        Assert.Equal(1, p.FrozenSnapshots);
        Assert.Equal(15m, p.FrozenUnitPrice);
        Assert.Equal(7, p.FrozenTermsDays);
        Assert.Equal(stage == 3 ? 20m : 45m, p.SaleQty);
        Assert.Equal(stage == 3 ? 20m : 45m, p.IssuedQty);
        Assert.Equal(stage == 3 ? 200m : 450m, p.IssuedCost);
        Assert.Equal(stage == 3 ? 300m : 675m, p.OriginalAr);
        Assert.Equal(200m, p.AppliedAr);
        Assert.Equal(stage == 3 ? 100m : 475m, p.OutstandingAr);
        Assert.Equal(200m, p.ReceiptReceived);
        Assert.Equal(200m, p.ReceiptApplied);
        Assert.Equal(p.ReceiptApplied, p.AllocatedCash);
        Assert.Equal(p.AppliedAr, p.AllocatedCash);
        Assert.Equal(100m, p.GrantConsumed);
        Assert.Equal(100m, p.SourceCreditConsumed);
        Assert.Equal(stage == 3 ? "CONFIRMED" : "PARTIALLY_DELIVERED", p.OrderStatus);
        if (stage == 3) Assert.Null(p.ADue);
        else Assert.Equal(p.TripDate.Date.AddDays(7), p.ADue?.Date);

        // Independent physical/source and financial/ledger reconciliations.
        Assert.Equal(p.IssuedQty, snapshot.Values["soldQty"]);
        Assert.Equal(p.IssuedCost, snapshot.Values["cogs"]);
        Assert.Equal(p.OutstandingAr, snapshot.Values["arA"] + snapshot.Values["arB"]);
        Assert.Equal(p.ReceiptReceived, snapshot.Values["custody"]);
        Assert.Equal(70m - p.IssuedQty, snapshot.Values["tripQty"]);
        Assert.Equal(700m - p.IssuedCost, snapshot.Values["tripCost"]);
        Assert.Equal(30m, snapshot.Values["mainQty"]);
        Assert.Equal(300m, snapshot.Values["mainCost"]);
        Assert.Equal(0m, snapshot.Values["fund"]);
        Assert.Equal(0, snapshot.Evidence.RejectedCommandSources);
        Assert.Equal(1, snapshot.Evidence.RejectedCommands);

        using var db = Open(tenant);
        // No extra or substituted source kind may hide behind matching sums
        // and an otherwise correct seven-plus-four delivery effect set.
        var sourceKinds = db.Query<(string Kind, int SourceCount)>("""
            SELECT SourceKind, COUNT(*) AS SourceCount FROM dbo.BusinessSource
            WHERE TenantId=@Tenant AND TripId=@Trip
            GROUP BY SourceKind
            """, new { Tenant = tenant, scene.Trip })
            .ToDictionary(x => x.Kind, x => x.SourceCount, StringComparer.Ordinal);
        Assert.Equal(stage == 3 ? 5 : 6, sourceKinds.Count);
        Assert.Equal(2, sourceKinds["STOCK_ALLOC"]);
        Assert.Equal(1, sourceKinds["A_CONFIRM"]);
        Assert.Equal(1, sourceKinds["MANIFEST_LOAD"]);
        Assert.Equal(1, sourceKinds["B_CREDIT_GRANT"]);
        Assert.Equal(1, sourceKinds["B_SALE_DELIVERY"]);
        if (stage == 4) Assert.Equal(1, sourceKinds["A_DELIVERY"]);
        var effects = db.Query<PostingEffectRow>("""
            SELECT src.SourceKind,e.EffectKey,e.LedgerKind,e.QuantityDelta,e.AmountDelta
            FROM dbo.LedgerEffect e
            JOIN dbo.BusinessSourceLine sl ON sl.TenantId=e.TenantId
                AND sl.Id=e.BusinessSourceLineId
            JOIN dbo.BusinessSource src ON src.TenantId=sl.TenantId
                AND src.Id=sl.BusinessSourceId
            WHERE src.TenantId=@Tenant AND src.TripId=@Trip
                AND src.SourceKind IN (N'B_CREDIT_GRANT',N'B_SALE_DELIVERY',N'A_DELIVERY')
            """, new { Tenant = tenant, scene.Trip }).ToArray();
        static void CheckLegs(PostingEffectRow[] rows, string kind,
            params (string Key, string Ledger, decimal Quantity, decimal Amount)[] expected)
        {
            var current = rows.Where(x => x.SourceKind == kind).ToArray();
            Assert.Equal(expected.Length, current.Length);
            foreach (var e in expected)
            {
                var leg = Assert.Single(current, x => x.EffectKey == e.Key);
                Assert.Equal(e.Ledger, leg.LedgerKind);
                Assert.Equal(e.Quantity, leg.QuantityDelta);
                Assert.Equal(e.Amount, leg.AmountDelta);
            }
        }
        CheckLegs(effects, "B_CREDIT_GRANT",
            ("RIGHT-ALLOCATED", "RIGHT_CREDIT_B", 0m, 100m));
        CheckLegs(effects, "B_SALE_DELIVERY",
            ("PHYSICAL-OUT", "PHYSICAL_STOCK", -20m, -200m),
            ("COGS", "COGS", 0m, 200m),
            ("AR-GROSS", "AR", 0m, 300m),
            ("AR-CASH-ALLOC", "AR", 0m, -200m),
            ("CASH-CUSTODY", "CUSTODY", 0m, 200m),
            ("RIGHT-B-USED", "RIGHT_B_CONSUMED", -20m, 0m),
            ("CREDIT-B-USED", "RIGHT_CREDIT_B_CONSUMED", 0m, -100m));
        if (stage == 3)
            CheckLegs(effects, "A_DELIVERY");
        else
            CheckLegs(effects, "A_DELIVERY",
                ("PHYSICAL-OUT", "PHYSICAL_STOCK", -25m, -250m),
                ("COGS", "COGS", 0m, 250m),
                ("AR-GROSS", "AR", 0m, 375m),
                ("RIGHT-A-USED", "RIGHT_A_CONSUMED", -25m, 0m));
    }

    private async Task WriteEvidence(string tenant, Oracle oracle,
        IReadOnlyList<Observation> reads, string? failure,
        int checkpoints = 5)
    {
        var cells = reads.SelectMany(r => Compare(oracle, r.Row,
            r.Snapshot.Step, r.Snapshot.Values)).ToArray();
        int expectedCells = checkpoints * 13;
        bool passed = failure is null && reads.Count == checkpoints
            && cells.Length == expectedCells && cells.All(c => c.Match);
        string directory = Path.Combine(AppContext.BaseDirectory, "TestResults", checkpoints == 7 ? "APP-01D" : "APP-01C4");
        Directory.CreateDirectory(directory);
        string file = Path.Combine(directory, tenant + "-oracle.json");
        var report = new {
            Schema = checkpoints == 7 ? "APP01D_SQL_ORACLE_V1" : "APP01C4_SQL_ORACLE_V1",
            TenantId = tenant,
            Database = tenant == TenantA ? fixture.TenantADb : fixture.TenantBDb,
            Environment = "LocalDB / synthetic / invoiceEligible=false",
            TestRunId = Environment.GetEnvironmentVariable(
                checkpoints == 7 ? "APP01D_TEST_RUN_ID" : "APP01C4_TEST_RUN_ID") ?? "LOCAL_UNTRACKED",
            SourceHeadSha = Environment.GetEnvironmentVariable(
                checkpoints == 7 ? "APP01D_SOURCE_HEAD_SHA" : "APP01C4_SOURCE_HEAD_SHA"),
            WorkflowSha = Environment.GetEnvironmentVariable("GITHUB_SHA"),
            Baseline = "business/reference-data/expected-results/golden-oracles.json#AC-03",
            OracleSha256 = oracle.Sha256,
            ExpectedCells = expectedCells, CheckedCells = cells.Length,
            MatchedCells = cells.Count(c => c.Match),
            Status = passed ? "PASS" : "FAIL_OR_INCOMPLETE",
            Steps = reads.Select(r => new { r.Row, r.Snapshot.Step,
                Actual = r.Snapshot.Values, r.Snapshot.Evidence,
                r.Snapshot.Posting, r.Snapshot.PostingDigest }),
            Cells = cells,
            Failure = failure
        };
        await File.WriteAllTextAsync(file,
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }),
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Both_LocalDB_tenants_match_all_65_AC03_cells_from_persisted_T0_through_T4()
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

            // Stage T3: 100 synthetic approved credit is granted only after
            // the T2 capture. A separate, preexisting verified cash witness
            // authorizes B20, not an untrusted request flag. Then A25 posts
            // against its frozen Confirm snapshot on the same loaded trip.
            foreach (var (client, tenant, scene, rejectCommand) in new[] {
                (a, TenantA, sceneA, rejectA.CommandId),
                (b, TenantB, sceneB, rejectB.CommandId)
            })
            {
                string otherTenant = tenant == TenantA ? TenantB : TenantA;
                Scene otherScene = tenant == TenantA ? sceneB : sceneA;
                string otherReject = tenant == TenantA ? rejectB.CommandId : rejectA.CommandId;
                Snapshot otherBefore = Capture(otherTenant, otherScene, otherReject);

                await ExpectApplied(client, ACommands + "grant-b-credit",
                    new GrantBCreditCommand(Producer, Id(), Id(), scene.Trip,
                        scene.Customer, scene.ShipTo, "B", "SYNTHETIC_UNITLESS", 100m),
                    "B_CREDIT_GRANTED");
                var b20 = new PostBSaleDeliveryReceiptCommand(
                    Producer, Id(), Id(), scene.Trip, scene.Customer, scene.ShipTo,
                    scene.BManifestLine, scene.Sku, 20m, 15m, 200m,
                    SeedVerifiedCashWitness(tenant, scene));
                await ExpectApplied(client, CCommands + "post-b-sale-delivery-receipt",
                    b20, "B_SALE_DELIVERY_POSTED");
                Snapshot t3 = Capture(tenant, scene, rejectCommand);
                observations[tenant].Add(new Observation(3, t3));
                AssertPostedLineage(tenant, scene, t3, 3);
                AssertNoBusinessEffectsChanged(otherBefore,
                    Capture(otherTenant, otherScene, otherReject));

                var bReplay = await Post(client, CCommands + "post-b-sale-delivery-receipt", b20);
                Assert.Equal("APPLIED", bReplay.Status);
                Assert.True(bReplay.Replayed);
                AssertNoBusinessEffectsChanged(t3, Capture(tenant, scene, rejectCommand));

                var a25 = new PostAPartialDeliveryCommand(
                    Producer, Id(), Id(), scene.Trip, scene.Customer, scene.ShipTo,
                    scene.Order, scene.OrderLine, scene.AManifestLine, scene.Sku, 25m);
                await ExpectApplied(client, CCommands + "post-a-partial-delivery",
                    a25, "A_PARTIAL_DELIVERY_POSTED");
                Snapshot t4 = Capture(tenant, scene, rejectCommand);
                observations[tenant].Add(new Observation(4, t4));
                AssertPostedLineage(tenant, scene, t4, 4);
                AssertNoBusinessEffectsChanged(otherBefore,
                    Capture(otherTenant, otherScene, otherReject));

                var aReplay = await Post(client, CCommands + "post-a-partial-delivery", a25);
                Assert.Equal("APPLIED", aReplay.Status);
                Assert.True(aReplay.Replayed);
                AssertNoBusinessEffectsChanged(t4, Capture(tenant, scene, rejectCommand));
                // A25 must not mutate any B-cash/grant/receipt facts.
                Assert.Equal(t3.Posting.BSales, t4.Posting.BSales);
                Assert.Equal(t3.Posting.Receipts, t4.Posting.Receipts);
                Assert.Equal(t3.Posting.ReceiptReceived, t4.Posting.ReceiptReceived);
                Assert.Equal(t3.Posting.GrantConsumed, t4.Posting.GrantConsumed);
                Assert.Equal(t3.Posting.SourceCreditConsumed, t4.Posting.SourceCreditConsumed);
            }

            foreach (string tenant in new[] { TenantA, TenantB })
            {
                var reads = observations[tenant];
                Assert.Equal(5, reads.Count);
                var cells = reads.SelectMany(r => Compare(oracle, r.Row,
                    r.Snapshot.Step, r.Snapshot.Values)).ToArray();
                Assert.Equal(65, cells.Length);
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

    private sealed class App01dEffect
    {
        public string SourceKind { get; set; } = "";
        public string EffectKey { get; set; } = "";
        public string LedgerKind { get; set; } = "";
        public Guid? StockLocationId { get; set; }
        public decimal QuantityDelta { get; set; }
        public decimal AmountDelta { get; set; }
    }

    private sealed class App01dUnloadedLine
    {
        public string RightKind { get; set; } = "";
        public decimal BaseQuantity { get; set; }
        public decimal CostAmount { get; set; }
        public decimal ReleasedBQuantity { get; set; }
    }

    private void VerifyApp01dPostingFromSql(string tenant, Scene scene, bool unloaded)
    {
        using var db = Open(tenant);
        var args = new { Tenant = tenant, scene.Trip, scene.Sku,
            scene.Warehouse, scene.Vehicle };
        // Independent witness, not command-return data: cashier must match a
        // physical count, different actor, exact offer, Trip and amount.
        var (offers, accepted, witnessed, receipt, allocated, money) =
            db.QuerySingle<(int, int, int, decimal, decimal, decimal)>("""
            SELECT
              (SELECT COUNT(*) FROM dbo.CashHandoverOffer
                WHERE TenantId=@Tenant AND TripId=@Trip),
              (SELECT COUNT(*) FROM dbo.CashHandoverAcceptance
                WHERE TenantId=@Tenant AND TripId=@Trip),
              (SELECT COUNT(*) FROM dbo.CashHandoverAcceptance a
                JOIN dbo.CashHandoverOffer o ON o.TenantId=a.TenantId
                  AND o.Id=a.OfferId AND o.TripId=a.TripId
                  AND o.OfferedAmount=a.AcceptedAmount
                JOIN dbo.CashierCountEvidence w ON w.TenantId=a.TenantId
                  AND w.EvidenceRef=a.EvidenceRef AND w.TripId=a.TripId
                  AND w.CountedAmount=a.AcceptedAmount
                  AND w.CountedBy=a.AcceptedBy
                  AND w.VerificationStatus=N'VERIFIED'
                WHERE a.TenantId=@Tenant AND a.TripId=@Trip
                  AND o.OfferedBy<>a.AcceptedBy),
              (SELECT COALESCE(SUM(ReceivedAmount),0) FROM dbo.CashReceipt
                WHERE TenantId=@Tenant AND TripId=@Trip),
              (SELECT COALESCE(SUM(a.AppliedAmount),0) FROM dbo.ReceiptAllocation a
                JOIN dbo.CashReceipt r ON r.TenantId=a.TenantId
                  AND r.Id=a.CashReceiptId
                WHERE r.TenantId=@Tenant AND r.TripId=@Trip),
              (SELECT COALESCE(SUM(AcceptedAmount),0)
                FROM dbo.CashHandoverAcceptance
                WHERE TenantId=@Tenant AND TripId=@Trip)
            """, args);
        Assert.Equal(1, offers);
        Assert.Equal(1, accepted);
        Assert.Equal(1, witnessed);
        Assert.Equal(200m, receipt);
        Assert.Equal(200m, allocated);
        Assert.Equal(200m, money);
        // Independent pool-capacity check outside the locked 91 oracle cells:
        // unused B rights must free physical allocation capacity at T6.
        Assert.Equal(unloaded ? 60m : 70m, db.ExecuteScalar<decimal>("""
            SELECT AllocatedBaseQuantity FROM dbo.StockPool
            WHERE TenantId=@Tenant AND StockLocationId=@Warehouse AND SkuId=@Sku
            """, args));

        var sourceKinds = db.Query<(string SourceKind, int Total)>("""
            SELECT SourceKind,COUNT(*) AS Total FROM dbo.BusinessSource
            WHERE TenantId=@Tenant AND TripId=@Trip
              AND SourceKind IN (N'CASH_HANDOVER_OFFER',
                N'CASH_HANDOVER_ACCEPT',N'TRIP_UNLOAD')
            GROUP BY SourceKind
            """, args).ToDictionary(x => x.SourceKind, x => x.Total);
        Assert.Equal(unloaded ? 3 : 2, sourceKinds.Count);
        Assert.Equal(1, sourceKinds["CASH_HANDOVER_OFFER"]);
        Assert.Equal(1, sourceKinds["CASH_HANDOVER_ACCEPT"]);
        if (unloaded) Assert.Equal(1, sourceKinds["TRIP_UNLOAD"]);

        var effects = db.Query<App01dEffect>("""
            SELECT src.SourceKind,e.EffectKey,e.LedgerKind,e.StockLocationId,
                e.QuantityDelta,e.AmountDelta
            FROM dbo.LedgerEffect e
            JOIN dbo.BusinessSourceLine sl ON sl.TenantId=e.TenantId
                AND sl.Id=e.BusinessSourceLineId
            JOIN dbo.BusinessSource src ON src.TenantId=sl.TenantId
                AND src.Id=sl.BusinessSourceId
            WHERE src.TenantId=@Tenant AND src.TripId=@Trip
              AND src.SourceKind IN (N'CASH_HANDOVER_OFFER',
                N'CASH_HANDOVER_ACCEPT',N'TRIP_UNLOAD')
            """, args).ToArray();
        // Exactly zero ledger effects from offer, two from acceptance, five
        // from unload. Unexpected source kind/key fails even if amounts net 0.
        Assert.Equal(unloaded ? 7 : 2, effects.Length);
        Assert.DoesNotContain(effects, x => x.SourceKind == "CASH_HANDOVER_OFFER");
        var acceptEffects = effects.Where(x => x.SourceKind == "CASH_HANDOVER_ACCEPT").ToArray();
        Assert.Equal(2, acceptEffects.Length);
        var outCash = Assert.Single(acceptEffects, x => x.EffectKey == "CUSTODY-OUT");
        Assert.Equal("CUSTODY", outCash.LedgerKind);
        Assert.Equal(0m, outCash.QuantityDelta);
        Assert.Equal(-200m, outCash.AmountDelta);
        var inFund = Assert.Single(acceptEffects, x => x.EffectKey == "FUND-IN");
        Assert.Equal("FUND", inFund.LedgerKind);
        Assert.Equal(0m, inFund.QuantityDelta);
        Assert.Equal(200m, inFund.AmountDelta);

        var a = db.QuerySingle<(decimal GrantedBaseQuantity,
            decimal ConsumedBaseQuantity, decimal ReleasedBaseQuantity, string Status)>("""
            SELECT GrantedBaseQuantity,ConsumedBaseQuantity,ReleasedBaseQuantity,Status
            FROM dbo.StockRight WHERE TenantId=@Tenant AND TripId=@Trip AND SkuId=@Sku
              AND RightKind='A'
            """, args);
        var b = db.QuerySingle<(decimal GrantedBaseQuantity,
            decimal ConsumedBaseQuantity, decimal ReleasedBaseQuantity, string Status)>("""
            SELECT GrantedBaseQuantity,ConsumedBaseQuantity,ReleasedBaseQuantity,Status
            FROM dbo.StockRight WHERE TenantId=@Tenant AND TripId=@Trip AND SkuId=@Sku
              AND RightKind='B'
            """, args);
        Assert.Equal((40m,25m,0m,"HELD"), a);
        Assert.Equal((30m,20m,unloaded ? 10m : 0m,
            unloaded ? "RELEASED" : "HELD"), b);
        if (!unloaded)
        {
            Assert.Equal(0, db.ExecuteScalar<int>("""
                SELECT COUNT(*) FROM dbo.TripUnload
                WHERE TenantId=@Tenant AND TripId=@Trip
                """, args));
            return;
        }

        var lines = db.Query<App01dUnloadedLine>("""
            SELECT l.RightKind,l.BaseQuantity,l.CostAmount,l.ReleasedBQuantity
            FROM dbo.TripUnloadLine l
            JOIN dbo.TripUnload u ON u.TenantId=l.TenantId AND u.Id=l.TripUnloadId
            JOIN dbo.TripManifestLine ml ON ml.TenantId=l.TenantId
                AND ml.Id=l.TripManifestLineId AND ml.TripId=l.TripId
                AND ml.StockRightId=l.StockRightId AND ml.LoadSourceLineId=l.LoadSourceLineId
            JOIN dbo.BusinessSourceLine sl ON sl.TenantId=l.TenantId
                AND sl.Id=l.SourceLineId AND sl.BusinessSourceId=u.Id
                AND sl.SkuId=l.SkuId AND sl.BaseQuantity=l.BaseQuantity
                AND sl.Amount=l.CostAmount
            WHERE u.TenantId=@Tenant AND u.TripId=@Trip
            """, args).ToArray();
        Assert.Equal(2, lines.Length);
        var aReturn = Assert.Single(lines, x => x.RightKind == "A");
        var bReturn = Assert.Single(lines, x => x.RightKind == "B");
        Assert.Equal((15m,150m,0m),
            (aReturn.BaseQuantity,aReturn.CostAmount,aReturn.ReleasedBQuantity));
        Assert.Equal((10m,100m,10m),
            (bReturn.BaseQuantity,bReturn.CostAmount,bReturn.ReleasedBQuantity));
        var unloadEffects = effects.Where(x => x.SourceKind == "TRIP_UNLOAD").ToArray();
        Assert.Equal(5, unloadEffects.Length);
        var vehicle = unloadEffects.Where(x => x.EffectKey == "VEHICLE-OUT").ToArray();
        var warehouse = unloadEffects.Where(x => x.EffectKey == "WAREHOUSE-IN").ToArray();
        var release = Assert.Single(unloadEffects, x => x.EffectKey == "RIGHT-B-RELEASE");
        Assert.Equal(2, vehicle.Length);
        Assert.Equal(2, warehouse.Length);
        Assert.All(vehicle, x => {
            Assert.Equal("PHYSICAL_STOCK", x.LedgerKind);
            Assert.Equal(scene.Vehicle, x.StockLocationId);
        });
        Assert.All(warehouse, x => {
            Assert.Equal("PHYSICAL_STOCK", x.LedgerKind);
            Assert.Equal(scene.Warehouse, x.StockLocationId);
        });
        Assert.Equal((-25m,-250m),
            (vehicle.Sum(x => x.QuantityDelta),vehicle.Sum(x => x.AmountDelta)));
        Assert.Equal((25m,250m),
            (warehouse.Sum(x => x.QuantityDelta),warehouse.Sum(x => x.AmountDelta)));
        Assert.Equal("RIGHT_B_RELEASED", release.LedgerKind);
        Assert.Equal(-10m, release.QuantityDelta);
        Assert.Equal(0m, release.AmountDelta);
        Assert.Equal("RECONCILING", db.QuerySingle<string>("""
            SELECT Status FROM dbo.Trip WHERE TenantId=@Tenant AND Id=@Trip
            """, args));
        Assert.Equal(0, db.ExecuteScalar<int>("""
            SELECT COUNT(*) FROM dbo.SaleDelivery
            WHERE TenantId=@Tenant AND TripId=@Trip AND InvoiceEligible=1
            """, args));
    }

    [Fact]
    public async Task Two_tenants_match_91_independent_SQL_cells_through_cashier_and_unload()
    {
        var oracle = ReadOracle(7);
        var reads = new Dictionary<string, List<Observation>> {
            [TenantA] = [], [TenantB] = []
        };
        string? failure = null;
        try
        {
            using var factory = fixture.CreateFactory();
            using var salesA = SqlScratchFixture.WithTenant(factory.CreateClient(), TenantA);
            using var salesB = SqlScratchFixture.WithTenant(factory.CreateClient(), TenantB);
            using var cashierA = SqlScratchFixture.WithTenant(factory.CreateClient(), TenantA,
                actor: "synthetic-cashier", roles: ["cashier"]);
            using var cashierB = SqlScratchFixture.WithTenant(factory.CreateClient(), TenantB,
                actor: "synthetic-cashier", roles: ["cashier"]);
            var sceneA = await Prepare(salesA, TenantA);
            var sceneB = await Prepare(salesB, TenantB);
            var rejectA = new PreflightBSaleCommand(Producer, Id(), Id(),
                sceneA.Trip, sceneA.Sku, 31m, 15m, 465m);
            var rejectB = new PreflightBSaleCommand(Producer, Id(), Id(),
                sceneB.Trip, sceneB.Sku, 31m, 15m, 465m);
            foreach (var (sales, cashier, tenant, scene, reject) in new[] {
                (salesA,cashierA,TenantA,sceneA,rejectA),
                (salesB,cashierB,TenantB,sceneB,rejectB)
            })
            {
                string other = tenant == TenantA ? TenantB : TenantA;
                Scene otherScene = tenant == TenantA ? sceneB : sceneA;
                string otherReject = tenant == TenantA ? rejectB.CommandId : rejectA.CommandId;
                var otherBefore = Capture(other, otherScene, otherReject);
                var t0 = Capture(tenant, scene, reject.CommandId);
                reads[tenant].Add(new Observation(0,t0));
                await ExpectApplied(sales, BCommands + "load-manifest",
                    new LoadManifestCommand(Producer,Id(),Id(),scene.Manifest), "MANIFEST_LOADED");
                var t1 = Capture(tenant, scene, reject.CommandId);
                reads[tenant].Add(new Observation(1,t1));
                var denial = await Post(sales,BCommands + "preflight-b-sale",reject);
                Assert.Equal("B_QUOTA_INSUFFICIENT",denial.ReasonCode);
                var t2 = Capture(tenant,scene,reject.CommandId);
                reads[tenant].Add(new Observation(2,t2));
                AssertNoBusinessEffectsChanged(t1,t2);

                await ExpectApplied(sales,ACommands + "grant-b-credit",
                    new GrantBCreditCommand(Producer,Id(),Id(),scene.Trip,
                        scene.Customer,scene.ShipTo,"B","SYNTHETIC_UNITLESS",100m),
                    "B_CREDIT_GRANTED");
                await ExpectApplied(sales,CCommands + "post-b-sale-delivery-receipt",
                    new PostBSaleDeliveryReceiptCommand(Producer,Id(),Id(),
                        scene.Trip,scene.Customer,scene.ShipTo,scene.BManifestLine,
                        scene.Sku,20m,15m,200m,SeedVerifiedCashWitness(tenant,scene)),
                    "B_SALE_DELIVERY_POSTED");
                var t3 = Capture(tenant,scene,reject.CommandId);
                reads[tenant].Add(new Observation(3,t3));
                AssertPostedLineage(tenant,scene,t3,3);
                await ExpectApplied(sales,CCommands + "post-a-partial-delivery",
                    new PostAPartialDeliveryCommand(Producer,Id(),Id(),
                        scene.Trip,scene.Customer,scene.ShipTo,scene.Order,
                        scene.OrderLine,scene.AManifestLine,scene.Sku,25m),
                    "A_PARTIAL_DELIVERY_POSTED");
                var t4 = Capture(tenant,scene,reject.CommandId);
                reads[tenant].Add(new Observation(4,t4));
                AssertPostedLineage(tenant,scene,t4,4);

                const string stringPath = "/api/synthetic/app01d/commands/";
                var offer = new OfferCashHandoverCommand(Producer,Id(),Id(),scene.Trip,200m);
                var offered = await Post(sales,stringPath + "offer-cash-handover",offer);
                Assert.Equal("CASH_HANDOVER_OFFERED",offered.OutcomeCode);
                var offerSource = Assert.IsType<Guid>(offered.SourceId);
                var afterOffer = Capture(tenant,scene,reject.CommandId);
                Assert.Equal("T4_A25",afterOffer.Step);
                foreach (var (field,value) in t4.Values)
                    Assert.Equal(value,afterOffer.Values[field]);
                Assert.Equal(t4.Evidence.LedgerDigest,afterOffer.Evidence.LedgerDigest);
                Assert.Equal(t4.PostingDigest,afterOffer.PostingDigest);
                Assert.Equal(t4.Evidence.Sources+1,afterOffer.Evidence.Sources);
                Assert.True((await Post(sales,stringPath+"offer-cash-handover",offer)).Replayed);
                Assert.Equal("IDEMPOTENCY_PAYLOAD_MISMATCH",
                    (await Post(sales,stringPath+"offer-cash-handover",
                        offer with {OfferedAmount=190m},HttpStatusCode.Conflict)).ReasonCode);

                string countRef = Id();
                using (var db = Open(tenant))
                    db.Execute("""
                        INSERT dbo.CashierCountEvidence(TenantId,EvidenceRef,TripId,
                            CountedAmount,CountedBy,VerificationStatus,CountedAtUtc)
                        VALUES(@Tenant,@CountRef,@Trip,200,N'synthetic-cashier',
                            N'VERIFIED',SYSUTCDATETIME())
                        """,new {Tenant=tenant,CountRef=countRef,scene.Trip});
                var accept = new AcceptCashHandoverCommand(Producer,Id(),Id(),
                    scene.Trip,offerSource,countRef);
                using (var wrongRole = await sales.PostAsJsonAsync(
                    stringPath+"accept-cash-handover",accept,TestContext.Current.CancellationToken))
                    Assert.Equal(HttpStatusCode.Forbidden,wrongRole.StatusCode);
                var wrongWitness = await Post(cashier,stringPath+"accept-cash-handover",
                    accept with {CommandId=Id(),CountEvidenceRef="unknown"});
                Assert.Equal("CASHIER_COUNT_NOT_VERIFIED",wrongWitness.ReasonCode);
                AssertNoBusinessEffectsChanged(afterOffer,Capture(tenant,scene,reject.CommandId));
                var accepted = await Post(cashier,stringPath+"accept-cash-handover",accept);
                Assert.Equal("CASH_HANDOVER_ACCEPTED",accepted.OutcomeCode);
                var t5 = Capture(tenant,scene,reject.CommandId);
                reads[tenant].Add(new Observation(5,t5));
                VerifyApp01dPostingFromSql(tenant,scene,unloaded:false);
                Assert.Equal(t4.PostingDigest,t5.PostingDigest);
                Assert.True((await Post(cashier,stringPath+"accept-cash-handover",accept)).Replayed);
                AssertNoBusinessEffectsChanged(t5,Capture(tenant,scene,reject.CommandId));
                var doubleAccept = await Post(cashier,stringPath+"accept-cash-handover",
                    accept with {CommandId=Id(),SourceNumber=Id()});
                Assert.Equal("OFFER_ALREADY_ACCEPTED",doubleAccept.ReasonCode);

                var unload = new UnloadTripRemainderCommand(Producer,Id(),Id(),
                    scene.Trip,scene.Manifest);
                var returned = await Post(sales,stringPath+"unload-trip-remainder",unload);
                Assert.Equal("TRIP_REMAINDER_UNLOADED",returned.OutcomeCode);
                var t6 = Capture(tenant,scene,reject.CommandId);
                reads[tenant].Add(new Observation(6,t6));
                VerifyApp01dPostingFromSql(tenant,scene,unloaded:true);
                Assert.Equal(t5.PostingDigest,t6.PostingDigest);
                foreach (var field in new[] {"cogs","arA","arB","custody","fund"})
                    Assert.Equal(t5.Values[field],t6.Values[field]);
                Assert.True((await Post(sales,stringPath+"unload-trip-remainder",unload)).Replayed);
                AssertNoBusinessEffectsChanged(t6,Capture(tenant,scene,reject.CommandId));
                var doubleUnload = await Post(sales,stringPath+"unload-trip-remainder",
                    unload with {CommandId=Id(),SourceNumber=Id()});
                Assert.Equal("TRIP_NOT_ELIGIBLE",doubleUnload.ReasonCode);
                AssertNoBusinessEffectsChanged(t6,Capture(tenant,scene,reject.CommandId));
                AssertNoBusinessEffectsChanged(otherBefore,Capture(other,otherScene,otherReject));
            }
            foreach (string tenant in new[] {TenantA,TenantB})
            {
                var observations = reads[tenant];
                Assert.Equal(7,observations.Count);
                var cells = observations.SelectMany(x=>Compare(oracle,x.Row,
                    x.Snapshot.Step,x.Snapshot.Values)).ToArray();
                Assert.Equal(91,cells.Length);
                Assert.True(cells.All(x=>x.Match),tenant+": "+string.Join("; ",
                    cells.Where(x=>!x.Match).Select(x=>
                        $"{x.Row}/{x.Field} expected={x.Expected}, actual={x.Actual}")));
            }
        }
        catch (Exception ex)
        {
            failure=ex.ToString();
            throw;
        }
        finally
        {
            foreach (string tenant in new[] {TenantA,TenantB})
                await WriteEvidence(tenant,oracle,reads[tenant],failure,checkpoints:7);
        }
    }


    [Fact]
    public async Task Unload_releases_never_loaded_B_quota_when_manifest_is_partial()
    {
        using var factory = fixture.CreateFactory();
        using var sales = SqlScratchFixture.WithTenant(factory.CreateClient(), TenantA);
        using var warehouse = SqlScratchFixture.WithTenant(factory.CreateClient(), TenantA,
            actor: "synthetic-warehouse", roles: ["warehouse"]);
        var scene = await Prepare(sales, TenantA);
        using (var db = Open(TenantA))
            db.Execute("""
                UPDATE dbo.TripManifestLine SET PlannedBaseQuantity=20
                WHERE TenantId=@Tenant AND Id=@Line AND RightKind='B'
                """, new { Tenant = TenantA, Line = scene.BManifestLine });

        await ExpectApplied(sales, BCommands + "load-manifest",
            new LoadManifestCommand(Producer, Id(), Id(), scene.Manifest), "MANIFEST_LOADED");
        await ExpectApplied(warehouse, DCommands + "unload-trip-remainder",
            new UnloadTripRemainderCommand(Producer, Id(), Id(),
                scene.Trip, scene.Manifest), "TRIP_REMAINDER_UNLOADED");

        using var dbCheck = Open(TenantA);
        var args = new { Tenant = TenantA, scene.Trip, scene.Sku,
            scene.Warehouse, scene.Vehicle };
        var bLine = dbCheck.QuerySingle<(decimal Qty, decimal Cost, decimal BReleased)>("""
            SELECT BaseQuantity,CostAmount,ReleasedBQuantity FROM dbo.TripUnloadLine
            WHERE TenantId=@Tenant AND TripId=@Trip AND SkuId=@Sku AND RightKind='B'
            """, args);
        Assert.Equal((20m, 200m, 30m), bLine);
        var bRight = dbCheck.QuerySingle<(decimal Granted, decimal Consumed,
            decimal Released, string Status)>("""
            SELECT GrantedBaseQuantity,ConsumedBaseQuantity,ReleasedBaseQuantity,Status
            FROM dbo.StockRight
            WHERE TenantId=@Tenant AND TripId=@Trip AND SkuId=@Sku AND RightKind='B'
            """, args);
        Assert.Equal((30m, 0m, 30m, "RELEASED"), bRight);
        Assert.Equal(40m, dbCheck.ExecuteScalar<decimal>("""
            SELECT AllocatedBaseQuantity FROM dbo.StockPool
            WHERE TenantId=@Tenant AND StockLocationId=@Warehouse AND SkuId=@Sku
            """, args));
        Assert.Equal((100m, 1000m), dbCheck.QuerySingle<(decimal Qty, decimal Cost)>("""
            SELECT OnHandBaseQuantity,OnHandCostAmount FROM dbo.PhysicalStockBalance
            WHERE TenantId=@Tenant AND StockLocationId=@Warehouse AND SkuId=@Sku
            """, args));
        Assert.Equal((0m, 0m), dbCheck.QuerySingle<(decimal Qty, decimal Cost)>("""
            SELECT OnHandBaseQuantity,OnHandCostAmount FROM dbo.PhysicalStockBalance
            WHERE TenantId=@Tenant AND StockLocationId=@Vehicle AND SkuId=@Sku
            """, args));
    }

    [Fact]
    public async Task Unload_records_release_only_B_line_after_partially_loaded_B_is_fully_sold()
    {
        using var factory = fixture.CreateFactory();
        using var sales = SqlScratchFixture.WithTenant(factory.CreateClient(), TenantB);
        using var cashier = SqlScratchFixture.WithTenant(factory.CreateClient(), TenantB,
            actor: "synthetic-cashier", roles: ["cashier"]);
        using var warehouse = SqlScratchFixture.WithTenant(factory.CreateClient(), TenantB,
            actor: "synthetic-warehouse", roles: ["warehouse"]);
        var scene = await Prepare(sales, TenantB);
        using (var db = Open(TenantB))
            db.Execute("""
                UPDATE dbo.TripManifestLine SET PlannedBaseQuantity=20
                WHERE TenantId=@Tenant AND Id=@Line AND RightKind='B'
                """, new { Tenant = TenantB, Line = scene.BManifestLine });

        await ExpectApplied(sales, BCommands + "load-manifest",
            new LoadManifestCommand(Producer, Id(), Id(), scene.Manifest), "MANIFEST_LOADED");
        await ExpectApplied(sales, ACommands + "grant-b-credit",
            new GrantBCreditCommand(Producer, Id(), Id(),
                scene.Trip, scene.Customer, scene.ShipTo, "B",
                "SYNTHETIC_UNITLESS", 100m), "B_CREDIT_GRANTED");
        await ExpectApplied(sales, CCommands + "post-b-sale-delivery-receipt",
            new PostBSaleDeliveryReceiptCommand(Producer, Id(), Id(),
                scene.Trip, scene.Customer, scene.ShipTo, scene.BManifestLine,
                scene.Sku, 20m, 15m, 200m, SeedVerifiedCashWitness(TenantB, scene)),
            "B_SALE_DELIVERY_POSTED");
        var offered = await Post(sales, DCommands + "offer-cash-handover",
            new OfferCashHandoverCommand(Producer, Id(), Id(), scene.Trip, 200m));
        Assert.Equal("CASH_HANDOVER_OFFERED", offered.OutcomeCode);
        string countRef = Id();
        using (var db = Open(TenantB))
            db.Execute("""
                INSERT dbo.CashierCountEvidence(TenantId,EvidenceRef,TripId,
                    CountedAmount,CountedBy,VerificationStatus,CountedAtUtc)
                VALUES(@Tenant,@Evidence,@Trip,200,N'synthetic-cashier',
                    N'VERIFIED',SYSUTCDATETIME())
                """, new { Tenant = TenantB, Evidence = countRef, scene.Trip });
        await ExpectApplied(cashier, DCommands + "accept-cash-handover",
            new AcceptCashHandoverCommand(Producer, Id(), Id(),
                scene.Trip, Assert.IsType<Guid>(offered.SourceId), countRef),
            "CASH_HANDOVER_ACCEPTED");
        await ExpectApplied(warehouse, DCommands + "unload-trip-remainder",
            new UnloadTripRemainderCommand(Producer, Id(), Id(),
                scene.Trip, scene.Manifest), "TRIP_REMAINDER_UNLOADED");

        using var verify = Open(TenantB);
        var args = new { Tenant = TenantB, scene.Trip, scene.Sku,
            scene.Warehouse, scene.Vehicle };
        var bLine = verify.QuerySingle<(decimal Qty, decimal Cost, decimal BRelease)>("""
            SELECT BaseQuantity,CostAmount,ReleasedBQuantity
            FROM dbo.TripUnloadLine WHERE TenantId=@Tenant AND TripId=@Trip
                AND SkuId=@Sku AND RightKind='B'
            """, args);
        Assert.Equal((0m, 0m, 10m), bLine);
        var bRight = verify.QuerySingle<(decimal Granted, decimal Consumed,
            decimal Released, string Status)>("""
            SELECT GrantedBaseQuantity,ConsumedBaseQuantity,ReleasedBaseQuantity,Status
            FROM dbo.StockRight WHERE TenantId=@Tenant AND TripId=@Trip
                AND SkuId=@Sku AND RightKind='B'
            """, args);
        Assert.Equal((30m, 20m, 10m, "RELEASED"), bRight);
        Assert.Equal(60m, verify.ExecuteScalar<decimal>("""
            SELECT AllocatedBaseQuantity FROM dbo.StockPool
            WHERE TenantId=@Tenant AND StockLocationId=@Warehouse AND SkuId=@Sku
            """, args));
        Assert.Equal((80m, 800m), verify.QuerySingle<(decimal Qty, decimal Cost)>("""
            SELECT OnHandBaseQuantity,OnHandCostAmount FROM dbo.PhysicalStockBalance
            WHERE TenantId=@Tenant AND StockLocationId=@Warehouse AND SkuId=@Sku
            """, args));
        Assert.Equal((0m, 0m), verify.QuerySingle<(decimal Qty, decimal Cost)>("""
            SELECT OnHandBaseQuantity,OnHandCostAmount FROM dbo.PhysicalStockBalance
            WHERE TenantId=@Tenant AND StockLocationId=@Vehicle AND SkuId=@Sku
            """, args));
        Assert.Equal(1, verify.ExecuteScalar<int>("""
            SELECT COUNT(*) FROM dbo.LedgerEffect e
            JOIN dbo.BusinessSourceLine sl ON sl.TenantId=e.TenantId
                AND sl.Id=e.BusinessSourceLineId
            JOIN dbo.TripUnloadLine ul ON ul.TenantId=sl.TenantId
                AND ul.SourceLineId=sl.Id
            WHERE ul.TenantId=@Tenant AND ul.TripId=@Trip AND ul.RightKind='B'
                AND e.EffectKey=N'RIGHT-B-RELEASE'
                AND e.LedgerKind=N'RIGHT_B_RELEASED' AND e.QuantityDelta=-10
            """, args));
    }

}
