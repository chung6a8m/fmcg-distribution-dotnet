using System.Globalization;
using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;

namespace FmcgDms.Demo;

internal sealed class App03AcceptanceOracle(DemoRun run, string folder, CancellationToken ct)
{
    private readonly List<Comparison> comparisons = [];
    private readonly List<SqlSnapshot> snapshots = [];
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    internal async Task<SqlSnapshot> Capture(string stage, string tenant)
    {
        await run.Verify(tenant, app03Session: true);
        await using var connection = new SqlConnection(run.Connection(tenant));
        using var rows = await connection.QueryMultipleAsync(new CommandDefinition(Sql,
            new { tenant, DayOne = App03DayBootstrap.FirstDay.ToDateTime(TimeOnly.MinValue) }, cancellationToken: ct));
        var snapshot = new SqlSnapshot(stage, tenant, (await rows.ReadAsync<Measure>()).ToArray(),
            await rows.ReadSingleAsync<History>());
        snapshots.Add(snapshot);
        return snapshot;
    }

    internal void Expected(SqlSnapshot snapshot, string metric, string unit, decimal expected) =>
        Check(snapshot.Stage, snapshot.Tenant, metric, unit, expected, snapshot.Value(metric, unit));

    internal void SameMeasures(SqlSnapshot before, SqlSnapshot after, params string[] except)
    {
        foreach (var row in before.Measures.Where(row => !except.Contains(row.Metric)))
            Check(after.Stage, after.Tenant, row.Metric, row.Unit, row.Value, after.Value(row.Metric, row.Unit));
        SameHistory(before, after);
    }

    internal void SameHistory(SqlSnapshot before, SqlSnapshot after)
    {
        Check(after.Stage, after.Tenant, "opening digest", "SHA-256", before.History.Openings, after.History.Openings);
        Check(after.Stage, after.Tenant, "price, terms and delivery snapshot digest", "SHA-256",
            before.History.DayOneSources, after.History.DayOneSources);
    }

    internal async Task LinkedSources(SqlSnapshot snapshot)
    {
        var sources = await ReadJson("day-one-sources.json");
        var owner = await ReadJson("owner.json");
        var refund = owner.GetProperty("refund");
        var cash = sources.GetProperty("cash").GetProperty("sourceId").GetGuid();
        var sale = sources.GetProperty("delivery").GetGuid();
        var saleSource = sources.GetProperty("deliverySource").GetGuid();
        var offer = sources.GetProperty("offer").GetGuid();
        var order = sources.GetProperty("order").GetGuid();
        var trip = sources.GetProperty("trip").GetGuid();
        var pendingOrder = sources.GetProperty("pendingOrder").GetGuid();
        var pendingTrip = sources.GetProperty("pendingTrip").GetGuid();
        var visit = sources.GetProperty("visit").GetGuid();
        var cashCommand = sources.GetProperty("command").GetProperty("commandId").GetString();
        var saleB = sources.GetProperty("saleB").GetGuid();
        var caseId = refund.GetProperty("caseId").GetGuid();
        var creditId = refund.GetProperty("creditId").GetGuid();
        var refundId = refund.GetProperty("refundId").GetGuid();

        await run.Verify(snapshot.Tenant, app03Session: true);
        await using var db = new SqlConnection(run.Connection(snapshot.Tenant));
        var links = await db.QueryAsync<Measure>(new CommandDefinition("""
            SELECT 'A delivery / order / trip' Metric,'rows' Unit,CONVERT(decimal(19,4),COUNT(*)) Value
            FROM dbo.SaleDelivery WHERE Id=@sale AND BusinessSourceId=@saleSource AND TripId=@trip AND AOrderId=@order AND BaseQuantity=2 AND InvoiceEligible=0
            UNION ALL
            SELECT 'B sale / delivery / trip','rows',COUNT(*) FROM dbo.SaleDelivery
            WHERE TripId=@trip AND BusinessSourceId=@saleB AND RightKind='B' AND BaseQuantity=2 AND InvoiceEligible=0
            UNION ALL
            SELECT 'pending order / original reservation / resumed load','rows',COUNT(*)
            FROM dbo.StockRight r JOIN dbo.AReservation ar ON ar.StockRightId=r.Id
            JOIN dbo.AOrderLine ol ON ol.Id=ar.AOrderLineId
            JOIN dbo.TripManifestLine m ON m.StockRightId=r.Id AND m.TripId=r.TripId
            WHERE ol.AOrderId=@pendingOrder AND r.TripId=@pendingTrip AND r.GrantedBaseQuantity=2 AND m.IsC3=1
            UNION ALL
            SELECT 'original visit','rows',COUNT(*) FROM dbo.F01Visit WHERE Id=@visit AND Outcome='NO_ORDER'
            UNION ALL
            SELECT 'cash acceptance / command / offer','rows',COUNT(*)
            FROM dbo.CashHandoverAcceptance a JOIN dbo.CashHandoverOffer o ON o.Id=a.OfferId
            JOIN dbo.BusinessSource s ON s.Id=a.Id
            JOIN dbo.BusinessCommand c ON c.Id=s.BusinessCommandId
            WHERE a.Id=@cash AND a.OfferId=@offer AND a.TripId=@trip AND a.AcceptedAmount=20000
              AND c.CommandId=@cashCommand AND a.AcceptedBy<>o.OfferedBy
            UNION ALL
            SELECT 'return / QA / credit / payout','rows',COUNT(*)
            FROM dbo.F05ReturnCase x JOIN dbo.F05ReturnStage q ON q.CaseId=x.Id AND q.Stage='RELEASE'
            JOIN dbo.F08ReturnCredit c ON c.CaseId=x.Id AND c.DeliveryId=x.DeliveryId
            JOIN dbo.F08Refund r ON r.CreditId=c.Id
            JOIN dbo.F08RefundApproval a ON a.RefundId=r.Id
            JOIN dbo.F08PayoutWitness w ON w.RefundId=r.Id
            JOIN dbo.F08Disbursement p ON p.RefundId=r.Id
            WHERE x.Id=@caseId AND x.DeliveryId=@sale AND x.Quantity=1 AND c.Id=@creditId AND c.GrossAmount=10000
              AND r.Id=@refundId AND p.Amount=4000
              AND (SELECT COUNT(DISTINCT actor.Id) FROM (VALUES(r.ActorId),(a.ActorId),(w.ActorId),(p.ActorId)) actor(Id))=4;
            """, new { sale, saleSource, order, trip, pendingOrder, pendingTrip, saleB, visit, cash, offer, cashCommand, caseId, creditId, refundId }, cancellationToken: ct));
        foreach (var row in links) Check(snapshot.Stage, snapshot.Tenant, row.Metric, row.Unit, 1m, row.Value);
        const string rejected = "SELECT COUNT(*) FROM dbo.BusinessCommand WHERE ProducerId='app03-cashier' AND CommandId IN @Ids";
        var peerWriteCommand = owner.GetProperty("peerWriteCommand").GetString();
        var foreignWrite = await db.ExecuteScalarAsync<int>(new CommandDefinition(rejected, new { Ids = new[] { peerWriteCommand } }, cancellationToken: ct));
        Check(snapshot.Stage, snapshot.Tenant, "foreign write command rows", "rows", 0, foreignWrite);
        await run.Verify("tenant-b", app03Session: true);
        await using var peer = new SqlConnection(run.Connection("tenant-b"));
        var peerCommands = await peer.ExecuteScalarAsync<int>(new CommandDefinition(rejected,
            new { Ids = new[] { cashCommand, peerWriteCommand } }, cancellationToken: ct));
        Check(snapshot.Stage, "tenant-b", "foreign write/replay command rows", "rows", 0, peerCommands);
    }

    internal async Task DayTwoSource(SqlSnapshot snapshot)
    {
        var ids = await ReadJson("day-two-sources.json");
        await run.Verify(snapshot.Tenant, app03Session: true);
        await using var db = new SqlConnection(run.Connection(snapshot.Tenant));
        var count = await db.ExecuteScalarAsync<int>(new CommandDefinition("""
            SELECT COUNT(*)
            FROM dbo.Trip t JOIN dbo.AOrder a ON a.TripId=t.Id
            JOIN dbo.AOrderLine l ON l.AOrderId=a.Id
            JOIN dbo.F04PriceLine p ON p.OrderLineId=l.Id
            JOIN dbo.C2CommercialPolicy q ON q.Id=p.QuotePolicyId
            WHERE t.TenantId=@Tenant AND t.Id=@Trip AND a.Id=@Order AND t.BusinessDate=@Day
                AND q.Version=2 AND q.TermsDays=10 AND l.BaseQuantity=1 AND p.GrossAmount=12000 AND p.Currency='VND';
            """, new
            {
                snapshot.Tenant, Trip = ids.GetProperty("trip").GetGuid(), Order = ids.GetProperty("order").GetGuid(),
                Day = App03DayBootstrap.FirstDay.AddDays(1).ToDateTime(TimeOnly.MinValue)
            }, cancellationToken: ct));
        Check(snapshot.Stage, snapshot.Tenant, "day-two order / trip / price and terms", "rows", 1, count);
    }

    internal async Task Owner(SqlSnapshot primary, SqlSnapshot peer)
    {
        var artifact = await ReadJson("owner.json");
        Compare(artifact.GetProperty("report"), primary, ("main-qty", "EA"), ("main-value", "VND"),
            ("trip-qty", "EA"), ("trip-value", "VND"), ("ar-a", "VND"), ("ar-b", "VND"), ("custody", "VND"), ("fund", "VND"), ("advance", "VND"),
            ("return-credit", "VND"), ("refund-paid", "VND"), ("refund-treasury", "VND"));
        Compare(artifact.GetProperty("peer"), peer, ("main-qty", "EA"), ("main-value", "VND"), ("refund-treasury", "VND"));

        void Compare(JsonElement report, SqlSnapshot snapshot, params (string Metric, string Unit)[] metrics)
        {
            Check("owner", snapshot.Tenant, "tenant", "identity", snapshot.Tenant, report.GetProperty("tenantId").GetString()!);
            foreach (var (metric, unit) in metrics)
            {
                var row = report.GetProperty("metrics").EnumerateArray()
                    .Single(value => value.GetProperty("metric").GetString() == metric && value.GetProperty("unit").GetString() == unit);
                var value = decimal.Parse(row.GetProperty("value").GetString()!, CultureInfo.InvariantCulture);
                Check("owner", snapshot.Tenant, metric, unit, snapshot.Value(metric, unit), value);
            }
            var main = report.GetProperty("metrics").EnumerateArray().Single(row => row.GetProperty("metric").GetString() == "main-qty");
            Check("owner", snapshot.Tenant, "MAIN source count", "rows", snapshot.Value("main-rows", "rows"),
                (decimal)main.GetProperty("sources").GetArrayLength());
        }
    }

    internal async Task DirectVisit(string reason, SqlSnapshot before)
    {
        await run.Verify(before.Tenant, app03Session: true);
        await using var db = new SqlConnection(run.Connection(before.Tenant));
        DirectReceipt? visit;
        do
        {
            visit = await db.QuerySingleOrDefaultAsync<DirectReceipt>(new CommandDefinition("""
                SELECT Id,CommandId,ActorId,Outcome,Reason FROM dbo.F01Visit
                WHERE TenantId=@Tenant AND ActorId=@Actor AND Outcome='NO_ORDER' AND Reason=@reason;
                """, new { before.Tenant, App03DayBootstrap.Actor, reason }, cancellationToken: ct));
            if (visit is null) await Task.Delay(TimeSpan.FromSeconds(1), ct);
        } while (visit is null);
        var after = await Capture("direct-browser", before.Tenant);
        SameMeasures(before, after, "visits");
        Expected(after, "visits", "rows", before.Value("visits", "rows") + 1);
        await File.WriteAllTextAsync(Path.Combine(folder, "direct-visit.json"), JsonSerializer.Serialize(visit, Json), ct);
    }

    internal async Task Write(string source)
    {
        await File.WriteAllTextAsync(Path.Combine(folder, "sql-review.json"),
            JsonSerializer.Serialize(new { source, runId = run.Id, snapshots, comparisons }, Json), CancellationToken.None);
        var failed = comparisons.Where(row => !row.Match).ToArray();
        if (failed.Length != 0)
            throw new InvalidOperationException("APP-03.9 SQL differences: " + JsonSerializer.Serialize(failed, Json));
        Console.WriteLine($"APP-03.9 SQL matched {comparisons.Count} comparisons.");
    }

    private async Task<JsonElement> ReadJson(string name) =>
        JsonSerializer.Deserialize<JsonElement>(await File.ReadAllTextAsync(Path.Combine(folder, name), ct));

    private void Check<T>(string stage, string tenant, string metric, string unit, T expected, T actual)
    {
        decimal? diff = actual is decimal a && expected is decimal e ? a - e : null;
        comparisons.Add(new(stage, tenant, metric, unit, expected!, actual!, diff, EqualityComparer<T>.Default.Equals(expected, actual)));
    }

    internal sealed record SqlSnapshot(string Stage, string Tenant, Measure[] Measures, History History)
    {
        internal decimal Value(string metric, string unit) =>
            Measures.Single(row => row.Metric == metric && row.Unit == unit).Value;
    }

    internal sealed record Measure(string Metric, string Unit, decimal Value);
    internal sealed record History(string Openings, string DayOneSources);
    private sealed record DirectReceipt(Guid Id, Guid CommandId, string ActorId, string Outcome, string Reason);
    private sealed record Comparison(string Stage, string Tenant, string Metric, string Unit, object Expected,
        object Actual, decimal? Diff, bool Match);

    private const string Sql = """
        WITH Stock AS (
            SELECT l.Kind,l.Code LocationCode,s.Code Sku,b.OnHandBaseQuantity Qty,b.OnHandCostAmount Cost
            FROM dbo.PhysicalStockBalance b
            JOIN dbo.StockLocation l ON l.Id=b.StockLocationId AND l.TenantId=b.TenantId
            JOIN dbo.Sku s ON s.Id=b.SkuId AND s.TenantId=b.TenantId
            WHERE b.TenantId=@tenant
        ), Cash AS (
            SELECT e.LedgerKind,e.AmountDelta
            FROM dbo.LedgerEffect e JOIN dbo.BusinessSourceLine l ON l.Id=e.BusinessSourceLineId
            JOIN dbo.BusinessSource s ON s.Id=l.BusinessSourceId
            WHERE s.TenantId=@tenant AND s.TripId IS NOT NULL
        )
        SELECT 'main-qty' Metric,'EA' Unit,SUM(Qty) Value FROM Stock WHERE Kind='WAREHOUSE'
        UNION ALL SELECT 'main-value','VND',SUM(Cost) FROM Stock WHERE Kind='WAREHOUSE'
        UNION ALL SELECT 'main-rows','rows',COUNT(*) FROM Stock WHERE Kind='WAREHOUSE'
        UNION ALL SELECT 'trip-qty','EA',SUM(Qty) FROM Stock WHERE Kind='VEHICLE'
        UNION ALL SELECT 'trip-value','VND',SUM(Cost) FROM Stock WHERE Kind='VEHICLE'
        UNION ALL SELECT 'refund-sku-total','EA',SUM(Qty) FROM Stock WHERE Sku='Z-REFUND-EA'
        UNION ALL SELECT 'refund-sku-truck-a','EA',SUM(Qty) FROM Stock WHERE LocationCode='DEV-TRUCK-A' AND Sku='Z-REFUND-EA'
        UNION ALL SELECT 'refund-sku-qa','EA',SUM(Qty) FROM Stock WHERE Kind='QUARANTINE' AND Sku='Z-REFUND-EA'
        UNION ALL SELECT 'refund-sku-main','EA',SUM(Qty) FROM Stock WHERE Kind='WAREHOUSE' AND Sku='Z-REFUND-EA'
        UNION ALL SELECT 'refund-sku-main-value','VND',SUM(Cost) FROM Stock WHERE Kind='WAREHOUSE' AND Sku='Z-REFUND-EA'
        UNION ALL SELECT 'refund-sku-truck-b','EA',SUM(Qty) FROM Stock WHERE LocationCode='DEV-TRUCK-B' AND Sku='Z-REFUND-EA'
        UNION ALL SELECT 'refund-sku-b-reservation','EA',
            COALESCE(SUM(r.GrantedBaseQuantity-r.ConsumedBaseQuantity-r.ReleasedBaseQuantity),0)
            FROM dbo.StockRight r JOIN dbo.Trip t ON t.Id=r.TripId JOIN dbo.Sku s ON s.Id=r.SkuId
            WHERE r.TenantId=@tenant AND r.RightKind='A' AND t.Code='DEV-20261123-B' AND s.Code='Z-REFUND-EA'
        UNION ALL SELECT 'ar-a','VND',COALESCE(SUM(OutstandingAmount),0) FROM dbo.ReceivableOpenItem WHERE TenantId=@tenant AND RightKind='A'
        UNION ALL SELECT 'ar-b','VND',COALESCE(SUM(OutstandingAmount),0) FROM dbo.ReceivableOpenItem WHERE TenantId=@tenant AND RightKind='B'
        UNION ALL SELECT 'custody','VND',COALESCE(SUM(AmountDelta),0) FROM Cash WHERE LedgerKind='CUSTODY'
        UNION ALL SELECT 'fund','VND',COALESCE(SUM(AmountDelta),0) FROM Cash WHERE LedgerKind='FUND'
        UNION ALL SELECT 'advance','VND',COALESCE(SUM(Issued),0)-
            (SELECT COALESCE(SUM(Amount),0) FROM dbo.F02AdvanceEntry WHERE TenantId=@tenant AND Kind IN ('RETURN_ACCEPT','EXPENSE_APPROVE'))
            FROM dbo.F02Advance WHERE TenantId=@tenant
        UNION ALL SELECT 'return-credit','VND',COALESCE(SUM(GrossAmount),0)-
            (SELECT COALESCE(SUM(Amount),0) FROM dbo.F08Disbursement WHERE TenantId=@tenant)
            FROM dbo.F08ReturnCredit WHERE TenantId=@tenant
        UNION ALL SELECT 'refund-paid','VND',COALESCE(SUM(Amount),0) FROM dbo.F08Disbursement WHERE TenantId=@tenant
        UNION ALL SELECT 'refund-treasury','VND',AvailableAmount FROM dbo.F08Treasury WHERE TenantId=@tenant
        UNION ALL SELECT 'acceptances','rows',COUNT(*) FROM dbo.CashHandoverAcceptance WHERE TenantId=@tenant
        UNION ALL SELECT 'cash-counts','rows',COUNT(*) FROM dbo.CashierCountEvidence WHERE TenantId=@tenant
        UNION ALL SELECT 'orders','rows',COUNT(*) FROM dbo.AOrder WHERE TenantId=@tenant
        UNION ALL SELECT 'deliveries','rows',COUNT(*) FROM dbo.SaleDelivery WHERE TenantId=@tenant
        UNION ALL SELECT 'visits','rows',COUNT(*) FROM dbo.F01Visit WHERE TenantId=@tenant
        UNION ALL SELECT 'effects','rows',COUNT(*) FROM dbo.LedgerEffect WHERE TenantId=@tenant
        UNION ALL SELECT 'trips','rows',COUNT(*) FROM dbo.Trip WHERE TenantId=@tenant
        UNION ALL SELECT 'day-two-price','VND',COALESCE(SUM(p.GrossAmount),0)
            FROM dbo.F04PriceLine p JOIN dbo.AOrderLine l ON l.Id=p.OrderLineId
            JOIN dbo.AOrder a ON a.Id=l.AOrderId JOIN dbo.Trip t ON t.Id=a.TripId
            WHERE p.TenantId=@tenant AND t.BusinessDate=DATEADD(day,1,@DayOne);

        SELECT
            CONVERT(varchar(64),HASHBYTES('SHA2_256',(
                SELECT o.* FROM dbo.PhysicalStockOpening o WHERE o.TenantId=@tenant
                ORDER BY o.StockLocationId,o.SkuId FOR JSON PATH
            )),2) AS Openings,
            CONVERT(varchar(64),HASHBYTES('SHA2_256',CONCAT(
                (SELECT p.*,q.UnitPrice,q.TermsDays,q.Version FROM dbo.F04PriceLine p
                    JOIN dbo.C2CommercialPolicy q ON q.Id=p.QuotePolicyId
                    JOIN dbo.AOrderLine l ON l.Id=p.OrderLineId JOIN dbo.AOrder a ON a.Id=l.AOrderId
                    JOIN dbo.Trip t ON t.Id=a.TripId WHERE p.TenantId=@tenant AND t.BusinessDate=@DayOne
                    ORDER BY p.OrderLineId FOR JSON PATH),
                (SELECT d.Id,d.ShipToSnapshotId,d.PayableAmount,d.BaseQuantity,d.CostAmount,d.BusinessSourceLineId,d.BusinessDate
                    FROM dbo.SaleDelivery d WHERE d.TenantId=@tenant AND d.BusinessDate=@DayOne
                    ORDER BY d.Id FOR JSON PATH),
                (SELECT r.SaleDeliveryId,r.DueBusinessDate,r.OriginalAmount FROM dbo.ReceivableOpenItem r
                    JOIN dbo.SaleDelivery d ON d.Id=r.SaleDeliveryId WHERE r.TenantId=@tenant AND d.BusinessDate=@DayOne
                    ORDER BY d.Id FOR JSON PATH)
            )),2) AS DayOneSources;
        """;
}
