using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using FmcgDms.Api.Persistence;
using Xunit;

namespace FmcgDms.Api.Tests;

/// <summary>
/// APP-01E.5 reads source tables independently of the command handlers and
/// of the existing C.4/D golden-oracle capture. The only shared fixture is
/// the real nonce-owned, two-tenant LocalDB and supported HTTP commands.
/// </summary>
public sealed partial class App01e2CrossCommandRaceTests
{
    private const string E5Producer = "app01e5-reconcile";
    private static readonly string[] E5Columns =
    [
        "step", "mainQty", "tripQty", "soldQty", "mainCost", "tripCost",
        "cogs", "aHold", "bQuota", "arA", "arB", "custody", "fund"
    ];
    private static readonly string[] E5Steps =
    [
        "T0", "T1_LOAD", "T2_REJECT", "T3_B20", "T4_A25",
        "T5_HANDOVER", "T6_RETURN"
    ];
    // Four pre-positioned APP-01E.2 seed commands include the synthetic B
    // credit grant at T0. No financial posting exists until the actual B20.
    private static readonly int[] E5Sources = [4, 5, 5, 6, 7, 9, 10];
    private static readonly int[] E5Lines = [4, 6, 6, 7, 8, 10, 12];
    private static readonly int[] E5Effects = [3, 7, 7, 14, 18, 20, 25];

    private sealed record E5Cell(int Row, string Field, string Expected,
        string Actual, decimal? Difference, bool Match);
    private sealed record E5Equation(string Name, decimal Expected,
        decimal Actual, decimal Difference, bool Match);
    private sealed record E5Checkpoint(int Row, string Step, SqlSnapshot Sql,
        FullSqlState Full, E5SqlFacts Facts, E5Equation[] Equations);
    private sealed record E5ReplayWitness(string Path, string CommandId, string Sha256);
    private sealed class E5SqlFacts
    {
        public string PersistedStep { get; set; } = "";
        public string RejectStatus { get; set; } = "";
        public string RejectOutcome { get; set; } = "";
        public string RejectReason { get; set; } = "";
        public decimal OpeningQty { get; set; }
        public decimal OpeningCost { get; set; }
        public decimal IssuedQty { get; set; }
        public decimal IssuedCost { get; set; }
        public decimal AStockIssuedQty { get; set; }
        public decimal BStockIssuedQty { get; set; }
        public decimal SaleQty { get; set; }
        public decimal ArAOutstanding { get; set; }
        public decimal ArBOutstanding { get; set; }
        public decimal CashReceived { get; set; }
        public decimal CashAllocated { get; set; }
        public decimal HandoverAccepted { get; set; }
        public decimal GrantedA { get; set; }
        public decimal GrantedB { get; set; }
        public decimal ConsumedA { get; set; }
        public decimal ConsumedB { get; set; }
        public decimal ReleasedB { get; set; }
        public decimal AHeld { get; set; }
        public decimal BQuotaHeld { get; set; }
        public decimal LoadQty { get; set; }
        public decimal LoadCost { get; set; }
        public decimal ReturnQty { get; set; }
        public decimal ReturnCost { get; set; }
        public decimal ReturnBReleased { get; set; }
        public decimal CreditConsumed { get; set; }
        public decimal SourceCreditConsumed { get; set; }
        public decimal PhysicalLedgerQtyNet { get; set; }
        public decimal PhysicalLedgerCostNet { get; set; }
        public int SourceCount { get; set; }
        public int SourceLineCount { get; set; }
        public int EffectCount { get; set; }
        public int LinkedAppliedSources { get; set; }
        public int SourcesWithoutLine { get; set; }
        public int UnknownSourceKinds { get; set; }
        public int OfferLedgerEffects { get; set; }
        public int RejectedSources { get; set; }
        public int Sales { get; set; }
        public int Receipts { get; set; }
        public int Offers { get; set; }
        public int Acceptances { get; set; }
        public int Unloads { get; set; }
        public int InvoiceEligible { get; set; }
    }

    private static (JsonElement[][] Rows, string Sha256) E5Oracle()
    {
        byte[] bytes = File.ReadAllBytes(Path.Combine(
            AppContext.BaseDirectory, "golden-oracles.json"));
        using var doc = JsonDocument.Parse(bytes);
        JsonElement oracle = doc.RootElement.GetProperty("AC-03");
        Assert.Equal("MODEL_ONLY", oracle.GetProperty("status").GetString());
        Assert.Equal("SYNTHETIC_UNITLESS", oracle.GetProperty("unit").GetString());
        Assert.False(oracle.GetProperty("invoiceEligible").GetBoolean());
        Assert.Equal(E5Columns, oracle.GetProperty("cols")
            .EnumerateArray().Select(x => x.GetString()!).ToArray());
        JsonElement[][] rows = oracle.GetProperty("steps").EnumerateArray()
            .Select(x => x.EnumerateArray().Select(v => v.Clone()).ToArray()).ToArray();
        Assert.Equal(7, rows.Length);
        for (int row = 0; row < 7; row++)
        {
            Assert.Equal(13, rows[row].Length);
            Assert.Equal(E5Steps[row], rows[row][0].GetString());
        }
        return (rows, Convert.ToHexString(SHA256.HashData(bytes)));
    }

    // Deliberately query physical custody, operational posting rows, rights,
    // immutable source/command lineage and ledger totals separately. Never
    // derive these measurements from the HTTP outcome or the golden expected.
    private E5SqlFacts E5ReadFacts(string tenant, Scene scene, string rejectId)
    {
        using var db = Open(tenant);
        var p = new { Tenant = tenant, scene.Trip, scene.Sku,
            scene.Warehouse, scene.Vehicle, scene.Manifest,
            Producer = E5Producer, Reject = rejectId };
        return db.QuerySingle<E5SqlFacts>("""
            SELECT
              (SELECT CASE
                WHEN EXISTS(SELECT 1 FROM dbo.TripUnload
                    WHERE TenantId=@Tenant AND TripId=@Trip) THEN N'T6_RETURN'
                WHEN EXISTS(SELECT 1 FROM dbo.CashHandoverAcceptance
                    WHERE TenantId=@Tenant AND TripId=@Trip) THEN N'T5_HANDOVER'
                WHEN EXISTS(SELECT 1 FROM dbo.SaleDelivery
                    WHERE TenantId=@Tenant AND TripId=@Trip AND RightKind='A') THEN N'T4_A25'
                WHEN EXISTS(SELECT 1 FROM dbo.SaleDelivery
                    WHERE TenantId=@Tenant AND TripId=@Trip AND RightKind='B') THEN N'T3_B20'
                WHEN EXISTS(SELECT 1 FROM dbo.BusinessCommand
                    WHERE TenantId=@Tenant AND ProducerId=@Producer AND CommandId=@Reject
                      AND Status=N'REJECTED' AND OutcomeCode=N'REJECTED_QUOTA'
                      AND ReasonCode=N'B_QUOTA_INSUFFICIENT') THEN N'T2_REJECT'
                WHEN EXISTS(SELECT 1 FROM dbo.TripManifest
                    WHERE TenantId=@Tenant AND Id=@Manifest AND Status=N'LOADED')
                    THEN N'T1_LOAD'
                ELSE N'T0' END) PersistedStep,
              COALESCE((SELECT Status FROM dbo.BusinessCommand
                WHERE TenantId=@Tenant AND ProducerId=@Producer AND CommandId=@Reject),N'NONE') RejectStatus,
              COALESCE((SELECT OutcomeCode FROM dbo.BusinessCommand
                WHERE TenantId=@Tenant AND ProducerId=@Producer AND CommandId=@Reject),N'NONE') RejectOutcome,
              COALESCE((SELECT ReasonCode FROM dbo.BusinessCommand
                WHERE TenantId=@Tenant AND ProducerId=@Producer AND CommandId=@Reject),N'NONE') RejectReason,
              (SELECT COALESCE(SUM(BaseQuantity),0) FROM dbo.PhysicalStockOpening
                WHERE TenantId=@Tenant AND SkuId=@Sku AND StockLocationId IN (@Warehouse,@Vehicle)) OpeningQty,
              (SELECT COALESCE(SUM(CostAmount),0) FROM dbo.PhysicalStockOpening
                WHERE TenantId=@Tenant AND SkuId=@Sku AND StockLocationId IN (@Warehouse,@Vehicle)) OpeningCost,
              (SELECT COALESCE(SUM(i.BaseQuantity),0) FROM dbo.DeliveryStockIssue i
                JOIN dbo.SaleDelivery d ON d.TenantId=i.TenantId AND d.Id=i.SaleDeliveryId
                WHERE d.TenantId=@Tenant AND d.TripId=@Trip) IssuedQty,
              (SELECT COALESCE(SUM(i.CostAmount),0) FROM dbo.DeliveryStockIssue i
                JOIN dbo.SaleDelivery d ON d.TenantId=i.TenantId AND d.Id=i.SaleDeliveryId
                WHERE d.TenantId=@Tenant AND d.TripId=@Trip) IssuedCost,
              (SELECT COALESCE(SUM(i.BaseQuantity),0) FROM dbo.DeliveryStockIssue i
                JOIN dbo.SaleDelivery d ON d.TenantId=i.TenantId AND d.Id=i.SaleDeliveryId
                WHERE d.TenantId=@Tenant AND d.TripId=@Trip AND d.RightKind='A') AStockIssuedQty,
              (SELECT COALESCE(SUM(i.BaseQuantity),0) FROM dbo.DeliveryStockIssue i
                JOIN dbo.SaleDelivery d ON d.TenantId=i.TenantId AND d.Id=i.SaleDeliveryId
                WHERE d.TenantId=@Tenant AND d.TripId=@Trip AND d.RightKind='B') BStockIssuedQty,
              (SELECT COALESCE(SUM(BaseQuantity),0) FROM dbo.SaleDelivery
                WHERE TenantId=@Tenant AND TripId=@Trip) SaleQty,
              (SELECT COALESCE(SUM(r.OutstandingAmount),0) FROM dbo.ReceivableOpenItem r
                JOIN dbo.SaleDelivery d ON d.TenantId=r.TenantId AND d.Id=r.SaleDeliveryId
                WHERE d.TenantId=@Tenant AND d.TripId=@Trip AND d.RightKind='A') ArAOutstanding,
              (SELECT COALESCE(SUM(r.OutstandingAmount),0) FROM dbo.ReceivableOpenItem r
                JOIN dbo.SaleDelivery d ON d.TenantId=r.TenantId AND d.Id=r.SaleDeliveryId
                WHERE d.TenantId=@Tenant AND d.TripId=@Trip AND d.RightKind='B') ArBOutstanding,
              (SELECT COALESCE(SUM(ReceivedAmount),0) FROM dbo.CashReceipt
                WHERE TenantId=@Tenant AND TripId=@Trip) CashReceived,
              (SELECT COALESCE(SUM(a.AppliedAmount),0) FROM dbo.ReceiptAllocation a
                JOIN dbo.CashReceipt r ON r.TenantId=a.TenantId AND r.Id=a.CashReceiptId
                WHERE r.TenantId=@Tenant AND r.TripId=@Trip) CashAllocated,
              (SELECT COALESCE(SUM(a.AcceptedAmount),0)
                FROM dbo.CashHandoverAcceptance a
                JOIN dbo.CashHandoverOffer o ON o.TenantId=a.TenantId
                  AND o.Id=a.OfferId AND o.TripId=a.TripId
                  AND o.OfferedAmount=a.AcceptedAmount AND o.OfferedBy<>a.AcceptedBy
                JOIN dbo.CashierCountEvidence w ON w.TenantId=a.TenantId
                  AND w.EvidenceRef=a.EvidenceRef AND w.TripId=a.TripId
                  AND w.CountedAmount=a.AcceptedAmount AND w.CountedBy=a.AcceptedBy
                  AND w.VerificationStatus=N'VERIFIED'
                WHERE a.TenantId=@Tenant AND a.TripId=@Trip) HandoverAccepted,
              (SELECT COALESCE(SUM(GrantedBaseQuantity),0) FROM dbo.StockRight
                WHERE TenantId=@Tenant AND TripId=@Trip AND RightKind='A') GrantedA,
              (SELECT COALESCE(SUM(GrantedBaseQuantity),0) FROM dbo.StockRight
                WHERE TenantId=@Tenant AND TripId=@Trip AND RightKind='B') GrantedB,
              (SELECT COALESCE(SUM(ConsumedBaseQuantity),0) FROM dbo.StockRight
                WHERE TenantId=@Tenant AND TripId=@Trip AND RightKind='A') ConsumedA,
              (SELECT COALESCE(SUM(ConsumedBaseQuantity),0) FROM dbo.StockRight
                WHERE TenantId=@Tenant AND TripId=@Trip AND RightKind='B') ConsumedB,
              (SELECT COALESCE(SUM(ReleasedBaseQuantity),0) FROM dbo.StockRight
                WHERE TenantId=@Tenant AND TripId=@Trip AND RightKind='B') ReleasedB,
              (SELECT COALESCE(SUM(GrantedBaseQuantity-ConsumedBaseQuantity-ReleasedBaseQuantity),0)
                FROM dbo.StockRight WHERE TenantId=@Tenant AND TripId=@Trip
                  AND RightKind='A' AND Status='HELD') AHeld,
              (SELECT COALESCE(SUM(GrantedBaseQuantity-ConsumedBaseQuantity-ReleasedBaseQuantity),0)
                FROM dbo.StockRight WHERE TenantId=@Tenant AND TripId=@Trip
                  AND RightKind='B' AND Status='HELD') BQuotaHeld,
              (SELECT COALESCE(SUM(m.BaseQuantity),0) FROM dbo.PhysicalStockMovement m
                JOIN dbo.TripManifestLine l ON l.TenantId=m.TenantId AND l.Id=m.TripManifestLineId
                WHERE l.TenantId=@Tenant AND l.TripId=@Trip) LoadQty,
              (SELECT COALESCE(SUM(m.CostAmount),0) FROM dbo.PhysicalStockMovement m
                JOIN dbo.TripManifestLine l ON l.TenantId=m.TenantId AND l.Id=m.TripManifestLineId
                WHERE l.TenantId=@Tenant AND l.TripId=@Trip) LoadCost,
              (SELECT COALESCE(SUM(BaseQuantity),0) FROM dbo.TripUnloadLine
                WHERE TenantId=@Tenant AND TripId=@Trip) ReturnQty,
              (SELECT COALESCE(SUM(CostAmount),0) FROM dbo.TripUnloadLine
                WHERE TenantId=@Tenant AND TripId=@Trip) ReturnCost,
              (SELECT COALESCE(SUM(ReleasedBQuantity),0) FROM dbo.TripUnloadLine
                WHERE TenantId=@Tenant AND TripId=@Trip) ReturnBReleased,
              (SELECT COALESCE(SUM(ConsumedAmount),0) FROM dbo.BCreditGrant
                WHERE TenantId=@Tenant AND TripId=@Trip) CreditConsumed,
              (SELECT COALESCE(SUM(ConsumedAmount),0) FROM dbo.BGrantConsumption
                WHERE TenantId=@Tenant AND TripId=@Trip) SourceCreditConsumed,
              (SELECT COALESCE(SUM(e.QuantityDelta),0) FROM dbo.LedgerEffect e
                JOIN dbo.BusinessSourceLine l ON l.TenantId=e.TenantId AND l.Id=e.BusinessSourceLineId
                JOIN dbo.BusinessSource s ON s.TenantId=l.TenantId AND s.Id=l.BusinessSourceId
                WHERE s.TenantId=@Tenant AND s.TripId=@Trip AND e.LedgerKind='PHYSICAL_STOCK') PhysicalLedgerQtyNet,
              (SELECT COALESCE(SUM(e.AmountDelta),0) FROM dbo.LedgerEffect e
                JOIN dbo.BusinessSourceLine l ON l.TenantId=e.TenantId AND l.Id=e.BusinessSourceLineId
                JOIN dbo.BusinessSource s ON s.TenantId=l.TenantId AND s.Id=l.BusinessSourceId
                WHERE s.TenantId=@Tenant AND s.TripId=@Trip AND e.LedgerKind='PHYSICAL_STOCK') PhysicalLedgerCostNet,
              (SELECT COUNT(*) FROM dbo.BusinessSource
                WHERE TenantId=@Tenant AND TripId=@Trip) SourceCount,
              (SELECT COUNT(*) FROM dbo.BusinessSourceLine l
                JOIN dbo.BusinessSource s ON s.TenantId=l.TenantId AND s.Id=l.BusinessSourceId
                WHERE s.TenantId=@Tenant AND s.TripId=@Trip) SourceLineCount,
              (SELECT COUNT(*) FROM dbo.LedgerEffect e
                JOIN dbo.BusinessSourceLine l ON l.TenantId=e.TenantId AND l.Id=e.BusinessSourceLineId
                JOIN dbo.BusinessSource s ON s.TenantId=l.TenantId AND s.Id=l.BusinessSourceId
                WHERE s.TenantId=@Tenant AND s.TripId=@Trip) EffectCount,
              (SELECT COUNT(*) FROM dbo.BusinessSource s
                JOIN dbo.BusinessCommand c ON c.TenantId=s.TenantId AND c.Id=s.BusinessCommandId
                  AND c.Status='APPLIED' AND c.ActorId=s.ActorId
                WHERE s.TenantId=@Tenant AND s.TripId=@Trip) LinkedAppliedSources,
              (SELECT COUNT(*) FROM dbo.BusinessSource s
                WHERE s.TenantId=@Tenant AND s.TripId=@Trip
                  AND NOT EXISTS (SELECT 1 FROM dbo.BusinessSourceLine l
                    WHERE l.TenantId=s.TenantId AND l.BusinessSourceId=s.Id)) SourcesWithoutLine,
              (SELECT COUNT(*) FROM dbo.BusinessSource s
                WHERE s.TenantId=@Tenant AND s.TripId=@Trip
                  AND s.SourceKind NOT IN ('STOCK_ALLOC','A_CONFIRM','B_CREDIT_GRANT',
                    'MANIFEST_LOAD','B_SALE_DELIVERY','A_DELIVERY',
                    'CASH_HANDOVER_OFFER','CASH_HANDOVER_ACCEPT','TRIP_UNLOAD')) UnknownSourceKinds,
              (SELECT COUNT(*) FROM dbo.LedgerEffect e
                JOIN dbo.BusinessSourceLine l ON l.TenantId=e.TenantId AND l.Id=e.BusinessSourceLineId
                JOIN dbo.BusinessSource s ON s.TenantId=l.TenantId AND s.Id=l.BusinessSourceId
                WHERE s.TenantId=@Tenant AND s.TripId=@Trip AND s.SourceKind='CASH_HANDOVER_OFFER') OfferLedgerEffects,
              (SELECT COUNT(*) FROM dbo.BusinessSource s
                JOIN dbo.BusinessCommand c ON c.TenantId=s.TenantId AND c.Id=s.BusinessCommandId
                WHERE c.TenantId=@Tenant AND c.ProducerId=@Producer AND c.CommandId=@Reject) RejectedSources,
              (SELECT COUNT(*) FROM dbo.SaleDelivery WHERE TenantId=@Tenant AND TripId=@Trip) Sales,
              (SELECT COUNT(*) FROM dbo.CashReceipt WHERE TenantId=@Tenant AND TripId=@Trip) Receipts,
              (SELECT COUNT(*) FROM dbo.CashHandoverOffer WHERE TenantId=@Tenant AND TripId=@Trip) Offers,
              (SELECT COUNT(*) FROM dbo.CashHandoverAcceptance WHERE TenantId=@Tenant AND TripId=@Trip) Acceptances,
              (SELECT COUNT(*) FROM dbo.TripUnload WHERE TenantId=@Tenant AND TripId=@Trip) Unloads,
              (SELECT COUNT(*) FROM dbo.SaleDelivery
                WHERE TenantId=@Tenant AND TripId=@Trip AND InvoiceEligible=1) InvoiceEligible
            """, p);
    }

    private static E5Equation[] E5Reconcile(int row, SqlSnapshot sql, E5SqlFacts f)
    {
        SqlNumbers n = sql.Numbers;
        var result = new List<E5Equation>();
        void Eq(string name, decimal expected, decimal actual) =>
            result.Add(new E5Equation(name, expected, actual, actual - expected,
                expected == actual));
        Eq("opening-qty=physical+issued", f.OpeningQty,
            n.WarehouseQty + n.VehicleQty + f.IssuedQty);
        Eq("opening-cost=physical+issued", f.OpeningCost,
            n.WarehouseCost + n.VehicleCost + f.IssuedCost);
        Eq("sale-qty=issued", f.SaleQty, f.IssuedQty);
        Eq("cogs=issued-cost", f.IssuedCost, n.Cogs);
        Eq("warehouse-qty=opening-load+return", f.OpeningQty - f.LoadQty + f.ReturnQty,
            n.WarehouseQty);
        Eq("warehouse-cost=opening-load+return", f.OpeningCost - f.LoadCost + f.ReturnCost,
            n.WarehouseCost);
        Eq("vehicle-qty=load-issued-return", f.LoadQty - f.IssuedQty - f.ReturnQty,
            n.VehicleQty);
        Eq("vehicle-cost=load-issued-return", f.LoadCost - f.IssuedCost - f.ReturnCost,
            n.VehicleCost);
        Eq("ar-A=receivable-outstanding", f.ArAOutstanding, n.ArA);
        Eq("ar-B=receivable-outstanding", f.ArBOutstanding, n.ArB);
        Eq("receipt=allocation", f.CashReceived, f.CashAllocated);
        Eq("receipt=ledger-custody+fund", f.CashReceived, n.Custody + n.Fund);
        Eq("fund=verified-acceptance", f.HandoverAccepted, n.Fund);
        Eq("credit-grant=source-consumption", f.SourceCreditConsumed, f.CreditConsumed);
        Eq("B-ar=consumed-credit", f.CreditConsumed, n.ArB);
        Eq("A-consumed=issued-A", f.AStockIssuedQty, f.ConsumedA);
        Eq("B-consumed=issued-B", f.BStockIssuedQty, f.ConsumedB);
        Eq("pool=granted-released", f.GrantedA + f.GrantedB - f.ReleasedB,
            n.PoolAllocated);
        Eq("A-held=granted-consumed", f.GrantedA - f.ConsumedA, f.AHeld);
        Eq("B-held=granted-consumed-released", f.GrantedB - f.ConsumedB - f.ReleasedB,
            f.BQuotaHeld);
        Eq("released-B=unload-source", f.ReturnBReleased, f.ReleasedB);
        Eq("physical-ledger-qty=negative-issued", -f.IssuedQty, f.PhysicalLedgerQtyNet);
        Eq("physical-ledger-cost=negative-issued", -f.IssuedCost, f.PhysicalLedgerCostNet);
        Eq("sources-expected", E5Sources[row], f.SourceCount);
        Eq("source-lines-expected", E5Lines[row], f.SourceLineCount);
        Eq("ledger-effects-expected", E5Effects[row], f.EffectCount);
        Eq("source-to-applied-command", f.SourceCount, f.LinkedAppliedSources);
        Eq("sources-without-lines=0", 0m, f.SourcesWithoutLine);
        Eq("unexpected-source-kinds=0", 0m, f.UnknownSourceKinds);
        Eq("nonposting-offer-effects=0", 0m, f.OfferLedgerEffects);
        Eq("rejected-source=0", 0m, f.RejectedSources);
        Eq("sales-count", row < 3 ? 0m : row == 3 ? 1m : 2m, f.Sales);
        Eq("receipt-count", row < 3 ? 0m : 1m, f.Receipts);
        Eq("offer-count", row < 5 ? 0m : 1m, f.Offers);
        Eq("accept-count", row < 5 ? 0m : 1m, f.Acceptances);
        Eq("unload-count", row < 6 ? 0m : 1m, f.Unloads);
        Eq("invoice-eligible=0", 0m, f.InvoiceEligible);
        return result.ToArray();
    }

    private static E5Cell[] E5Compare(int row, string step,
        E5SqlFacts f, SqlSnapshot sql, JsonElement[] expected)
    {
        var n = sql.Numbers;
        decimal[] observed = [n.WarehouseQty, n.VehicleQty, f.IssuedQty,
            n.WarehouseCost, n.VehicleCost, n.Cogs, f.AHeld, f.BQuotaHeld,
            n.ArA, n.ArB, n.Custody, n.Fund];
        var cells = new List<E5Cell>(13)
        {
            new(row, "step", expected[0].GetString()!, step, null,
                string.Equals(expected[0].GetString(), step, StringComparison.Ordinal))
        };
        for (int i = 1; i < E5Columns.Length; i++)
        {
            decimal want = expected[i].GetDecimal();
            decimal got = observed[i - 1];
            cells.Add(new E5Cell(row, E5Columns[i],
                want.ToString(CultureInfo.InvariantCulture),
                got.ToString(CultureInfo.InvariantCulture), got - want, got == want));
        }
        return cells.ToArray();
    }

    [Fact]
    public void App01e5_reconciliation_rejects_mutated_oracle_and_equation()
    {
        var oracle = E5Oracle();
        var expected = oracle.Rows[0];
        var fake = new E5SqlFacts { IssuedQty = 1m, AHeld = 40m, BQuotaHeld = 30m };
        var nums = new SqlNumbers
        {
            WarehouseQty = 100m, WarehouseCost = 1000m,
            VehicleQty = 0m, VehicleCost = 0m
        };
        var snapshot = new SqlSnapshot("fixture", "tenant-a", "DRAFT", "PREPARED",
            nums, new string('0', 64));
        Assert.Contains(E5Compare(0, "T0", fake, snapshot, expected),
            c => c.Field == "soldQty" && !c.Match && c.Difference == 1m);
        Assert.Contains(E5Reconcile(0, snapshot, fake),
            c => c.Name == "opening-qty=physical+issued" && !c.Match);
    }

    [Theory]
    [InlineData("tenant-a")]
    [InlineData("tenant-b")]
    public async Task App01e5_reconcile_independent_SQL_across_T0_to_T6_and_replays(
        string tenant)
    {
        var oracle = E5Oracle();
        var stages = new List<E5Checkpoint>(7);
        var cells = new List<E5Cell>(91);
        string? failure = null;
        string? finalReplaySha = null;
        string otherTenant = tenant == "tenant-a" ? "tenant-b" : "tenant-a";
        string? rejectedId = null;
        string? rejectedExcludedBeforeSha = null;
        string? rejectedExcludedAfterSha = null;
        string? peerDatabase = null;
        string? peerBeforeSha = null;
        string? peerAfterSha = null;
        var replayWitnesses = new List<E5ReplayWitness>(7);
        Scene scene;
        try
        {
            // Prove the opposite physical database has zero writes, not just
            // zero E.5 command IDs.
            var peerBefore = await FullSql(otherTenant);
            peerDatabase = peerBefore.Database;
            peerBeforeSha = peerBefore.Sha256;
            using var factory = fixture.CreateFactory();
            using var setup = SqlScratchFixture.WithTenant(factory.CreateClient(), tenant);
            using var warehouse = Client(factory, tenant, WarehouseActor, "warehouse");
            using var sales = Client(factory, tenant, SalesActor, "sales");
            using var cashier = Client(factory, tenant, CashierActor, "cashier");
            scene = Seed(tenant);
            await Prepare(setup, tenant, scene, load: false);
            var b31 = new PreflightBSaleCommand(E5Producer, Id(), Id(),
                scene.Trip, scene.Sku, 31m, 15m, 465m);
            rejectedId = b31.CommandId;

            async Task Capture(int row)
            {
                var sql = ReadSql(tenant, scene);
                var facts = E5ReadFacts(tenant, scene, b31.CommandId);
                var full = await FullSql(tenant);
                var equations = E5Reconcile(row, sql, facts);
                var differences = E5Compare(row, facts.PersistedStep, facts,
                    sql, oracle.Rows[row]);
                stages.Add(new E5Checkpoint(row, facts.PersistedStep, sql, full,
                    facts, equations));
                cells.AddRange(differences);
                Assert.Equal(E5Steps[row], facts.PersistedStep);
                Assert.All(differences, c => Assert.True(c.Match,
                    $"E5 {tenant}/{row}/{c.Field}: {c.Expected} != {c.Actual}"));
                Assert.All(equations, e => Assert.True(e.Match,
                    $"E5 {tenant}/{row}/{e.Name}: {e.Expected} != {e.Actual}"));
                Assert.Equal(row >= 2 ? "REJECTED" : "NONE", facts.RejectStatus);
                Assert.Equal(row >= 2 ? "REJECTED_QUOTA" : "NONE", facts.RejectOutcome);
                Assert.Equal(row >= 2 ? "B_QUOTA_INSUFFICIENT" : "NONE",
                    facts.RejectReason);
                Assert.Equal(tenant, sql.Tenant);
                Assert.Equal(sql.Database, full.Database);
            }

            await Capture(0);
            var load = new LoadManifestCommand(E5Producer, Id(), Id(), scene.Manifest);
            await Apply(warehouse, B + "load-manifest", load, "MANIFEST_LOADED");
            await Capture(1);
            // Ignore only the newly persisted REJECTED command identity:
            // every other row in every user table must remain unchanged.
            rejectedExcludedBeforeSha = (await FullSql(tenant, E5Producer, b31.CommandId)).Sha256;
            var rejected = await Send(sales, B + "preflight-b-sale", b31);
            Assert.Equal(HttpStatusCode.OK, rejected.HttpStatus);
            Assert.Equal("REJECTED", rejected.Outcome.Status);
            Assert.Equal("REJECTED_QUOTA", rejected.Outcome.OutcomeCode);
            Assert.Equal("B_QUOTA_INSUFFICIENT", rejected.Outcome.ReasonCode);
            Assert.Null(rejected.Outcome.SourceId);
            await Capture(2);
            rejectedExcludedAfterSha = (await FullSql(tenant, E5Producer, b31.CommandId)).Sha256;
            Assert.Equal(rejectedExcludedBeforeSha, rejectedExcludedAfterSha);
            Assert.Equal(stages[1].Sql.LedgerSha256, stages[2].Sql.LedgerSha256);
            Assert.Equal(stages[1].Sql.Numbers.Cells().ToArray(),
                stages[2].Sql.Numbers.Cells().ToArray());
            Assert.NotEqual(stages[1].Full.Sha256, stages[2].Full.Sha256);

            var b20 = B20(scene, CashWitness(tenant, scene))
                with { ProducerId = E5Producer };
            await Apply(sales, C + "post-b-sale-delivery-receipt", b20,
                "B_SALE_DELIVERY_POSTED");
            await Capture(3);
            var a25 = A25(scene) with { ProducerId = E5Producer };
            await Apply(sales, C + "post-a-partial-delivery", a25,
                "A_PARTIAL_DELIVERY_POSTED");
            await Capture(4);
            var offer = new OfferCashHandoverCommand(E5Producer, Id(), Id(),
                scene.Trip, 200m);
            var offerResult = await Apply(sales, D + "offer-cash-handover",
                offer, "CASH_HANDOVER_OFFERED");
            // OFFER persists one non-posting source but must not mutate any
            // economic, physical, grant, AR or receipt dimension.
            var offerSql = ReadSql(tenant, scene);
            var economicBeforeOffer = stages[4].Sql.Numbers.Cells();
            var economicAfterOffer = offerSql.Numbers.Cells();
            Assert.Equal(economicBeforeOffer["Offers"] + 1m,
                economicAfterOffer["Offers"]);
            economicBeforeOffer.Remove("Offers");
            economicAfterOffer.Remove("Offers");
            Assert.Equal(economicBeforeOffer.ToArray(), economicAfterOffer.ToArray());
            Assert.NotEqual(stages[4].Sql.LedgerSha256, offerSql.LedgerSha256);

            string countRef = Id();
            using (var db = Open(tenant))
                db.Execute("""
                    INSERT dbo.CashierCountEvidence(TenantId,EvidenceRef,TripId,
                        CountedAmount,CountedBy,VerificationStatus,CountedAtUtc)
                    VALUES(@Tenant,@Ref,@Trip,200,@Actor,N'VERIFIED',SYSUTCDATETIME())
                    """, new { Tenant = tenant, Ref = countRef, scene.Trip,
                        Actor = CashierActor });
            var accept = new AcceptCashHandoverCommand(E5Producer, Id(), Id(),
                scene.Trip, Assert.IsType<Guid>(offerResult.SourceId), countRef);
            await Apply(cashier, D + "accept-cash-handover", accept,
                "CASH_HANDOVER_ACCEPTED");
            await Capture(5);
            var unload = new UnloadTripRemainderCommand(E5Producer, Id(), Id(),
                scene.Trip, scene.Manifest);
            await Apply(warehouse, D + "unload-trip-remainder", unload,
                "TRIP_REMAINDER_UNLOADED");
            await Capture(6);
            Assert.Equal(stages[4].Sql.Numbers.ArA, stages[6].Sql.Numbers.ArA);
            Assert.Equal(stages[4].Sql.Numbers.ArB, stages[6].Sql.Numbers.ArB);
            Assert.Equal(stages[4].Sql.Numbers.Cogs, stages[6].Sql.Numbers.Cogs);

            foreach (var (client, path, command) in new (HttpClient, string, IApp01aCommand)[]
            {
                (warehouse, B + "load-manifest", load),
                (sales, B + "preflight-b-sale", b31),
                (sales, C + "post-b-sale-delivery-receipt", b20),
                (sales, C + "post-a-partial-delivery", a25),
                (sales, D + "offer-cash-handover", offer),
                (cashier, D + "accept-cash-handover", accept),
                (warehouse, D + "unload-trip-remainder", unload)
            })
            {
                var replay = await Send(client, path, command);
                Assert.Equal(HttpStatusCode.OK, replay.HttpStatus);
                Assert.True(replay.Outcome.Replayed);
                var afterEachReplay = (await FullSql(tenant)).Sha256;
                Assert.Equal(stages[6].Full.Sha256, afterEachReplay);
                replayWitnesses.Add(new E5ReplayWitness(path, command.CommandId,
                    afterEachReplay));
            }
            var conflict = await Send(sales, D + "offer-cash-handover",
                offer with { OfferedAmount = 190m });
            Assert.Equal(HttpStatusCode.Conflict, conflict.HttpStatus);
            Assert.Equal("CONFLICT", conflict.Outcome.Status);
            Assert.Equal("IDEMPOTENCY_PAYLOAD_MISMATCH", conflict.Outcome.ReasonCode);
            finalReplaySha = (await FullSql(tenant)).Sha256;
            Assert.Equal(stages[6].Full.Sha256, finalReplaySha);
            Assert.Equal(stages[6].Sql.LedgerSha256, ReadSql(tenant, scene).LedgerSha256);
            Assert.Equal(91, cells.Count);
            Assert.Equal(7, stages.Count);
            Assert.Equal(0, cells.Count(c => !c.Match));

            // The exact command IDs/source numbers are absent in the other
            // physical tenant, independent of any tenant header or SQL cache.
            using var otherDb = Open(otherTenant);
            Assert.Equal(0, otherDb.ExecuteScalar<int>("""
                SELECT COUNT(*) FROM dbo.BusinessCommand
                WHERE TenantId=@OtherTenant AND ProducerId=@Producer
                  AND CommandId IN @Ids
                """, new { OtherTenant = otherTenant, Producer = E5Producer,
                    Ids = new[] { load.CommandId, b31.CommandId, b20.CommandId,
                        a25.CommandId, offer.CommandId, accept.CommandId, unload.CommandId } }));
            peerAfterSha = (await FullSql(otherTenant)).Sha256;
            Assert.Equal(peerBeforeSha, peerAfterSha);
        }
        catch (Exception ex)
        {
            failure = ex.ToString();
            throw;
        }
        finally
        {
            var allEquations = stages.SelectMany(x => x.Equations).ToArray();
            var report = new
            {
                Schema = "APP01E5_RECONCILIATION_V1",
                Status = failure is null && stages.Count == 7 && cells.Count == 91
                    && cells.All(x => x.Match) && allEquations.All(x => x.Match)
                    && rejectedExcludedBeforeSha is not null
                    && rejectedExcludedBeforeSha == rejectedExcludedAfterSha
                    && peerBeforeSha is not null && peerBeforeSha == peerAfterSha
                    && replayWitnesses.Count == 7
                    && replayWitnesses.All(x => x.Sha256 == finalReplaySha)
                    && finalReplaySha == stages.LastOrDefault()?.Full.Sha256
                    ? "PASS" : "FAIL",
                Failure = failure,
                TestRunId = Environment.GetEnvironmentVariable("APP01E5_TEST_RUN_ID")
                    ?? "LOCAL_UNTRACKED",
                SourceHeadSha = Environment.GetEnvironmentVariable("APP01E5_SOURCE_HEAD_SHA"),
                WorkflowSha = Environment.GetEnvironmentVariable("GITHUB_SHA"),
                TenantId = tenant,
                Database = stages.FirstOrDefault()?.Sql.Database,
                ProducerId = E5Producer,
                RejectCommandId = rejectedId,
                OracleSha256 = oracle.Sha256,
                PeerDatabase = peerDatabase,
                PeerBeforeSha256 = peerBeforeSha,
                PeerAfterSha256 = peerAfterSha,
                RejectedExcludedBeforeSha256 = rejectedExcludedBeforeSha,
                RejectedExcludedAfterSha256 = rejectedExcludedAfterSha,
                ReplayWitnesses = replayWitnesses,
                ExpectedCells = 91,
                CheckedCells = cells.Count,
                MatchedCells = cells.Count(x => x.Match),
                ExpectedEquations = 7 * 37,
                CheckedEquations = allEquations.Length,
                MatchedEquations = allEquations.Count(x => x.Match),
                ReplayFullSqlSha256 = finalReplaySha,
                Stages = stages,
                Cells = cells
            };
            var dir = Path.Combine(AppContext.BaseDirectory, "TestResults", "APP-01E5");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, $"{tenant}-reconciliation.json"),
                JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));
        }
    }
}