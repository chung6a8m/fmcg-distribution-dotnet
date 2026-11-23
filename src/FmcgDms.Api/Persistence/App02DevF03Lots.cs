using System.Text.Json.Serialization;
using Dapper;
using Microsoft.Data.SqlClient;

namespace FmcgDms.Api.Persistence;

public sealed record F03UnitView(Guid SkuId, string UomCode, decimal Factor, decimal Step);
public sealed record F03LotView(string Code, DateTime ExpiresOn,
    [property: JsonNumberHandling(JsonNumberHandling.WriteAsString)] decimal Quantity);

public sealed partial class App01a2CommandService
{
    private const string LotUnavailable = "F03_LOT_STOCK_INSUFFICIENT";
    private static readonly string LotBusinessDate =
        $"CAST(({App02c6Clock.SqlUtcNow} AT TIME ZONE 'UTC') AT TIME ZONE 'SE Asia Standard Time' AS date)";

    private sealed record F03Quantity(int LineNumber, string UomCode, decimal EnteredQuantity, decimal Factor,
        decimal Step, decimal BaseStep, bool LotTracked)
    {
        public decimal BaseQuantity => EnteredQuantity * Factor;
    }

    private sealed record F03Pick(Guid LotId, decimal Quantity);
    private sealed record F03LotAvailability(Guid SourceId, Guid LotId, string Code, DateTime ExpiresOn, decimal Quantity)
    {
        public F03LotView ToView() => new(Code, ExpiresOn, Quantity);
        public F03Pick ToPick() => new(LotId, Quantity);
    }

    private static async Task<F03Quantity?> DraftQuantity(SqlConnection db, SqlTransaction tx, string tenant,
        C2DraftLine line, int lineNumber, string baseUom, CancellationToken ct)
    {
        var units = (await db.QueryAsync<F03UnitView>(Sql("""
            SELECT SkuId,UomCode,Factor,Step FROM dbo.F03Unit
            WHERE TenantId=@Tenant AND SkuId=@SkuId
            """, new { Tenant = tenant, line.SkuId }, tx, ct))).AsList();
        if (units.Count == 0 && line.UomCode is null)
            return new F03Quantity(lineNumber, baseUom, line.Quantity, 1, 0.001m, 0.001m, false);
        var unit = units.SingleOrDefault(row => row.UomCode == (line.UomCode ?? baseUom));
        var baseUnit = units.SingleOrDefault(row => row.UomCode == baseUom && row.Factor == 1);
        if (unit is null || baseUnit is null || line.Quantity % unit.Step != 0
            || line.Quantity > 999999999999999.999m / unit.Factor)
            return null;
        decimal converted = line.Quantity * unit.Factor;
        return ValidQuantity(converted) && converted % baseUnit.Step == 0
            ? new F03Quantity(lineNumber, unit.UomCode, line.Quantity, unit.Factor, unit.Step, baseUnit.Step, true)
            : null;
    }

    private static Task<bool> IsLotTracked(SqlConnection db, SqlTransaction tx, string tenant, Guid id,
        bool byRight, CancellationToken ct) => db.ExecuteScalarAsync<bool>(Sql($"""
            SELECT CAST(CASE WHEN EXISTS (
                SELECT 1 FROM dbo.AOrderLine a
                LEFT JOIN dbo.F03DraftQuantity q ON q.TenantId=a.TenantId AND q.DraftId=a.AOrderId AND q.LineNumber=a.LineNumber
                WHERE a.TenantId=@Tenant AND a.Id={(byRight
                    ? "(SELECT AOrderLineId FROM dbo.AReservation WHERE TenantId=@Tenant AND StockRightId=@Id)" : "@Id")}
                    AND (q.LotTracked=1 OR EXISTS (
                        SELECT 1 FROM dbo.F03Unit u WHERE u.TenantId=a.TenantId AND u.SkuId=a.SkuId))
            ) THEN 1 ELSE 0 END AS bit)
            """, new { Tenant = tenant, Id = id }, tx, ct));

    private static async Task<IReadOnlyList<F03Pick>> PlanLotHold(SqlConnection db, SqlTransaction tx,
        string tenant, Guid orderLine, Guid sku, Guid location, decimal quantity, CancellationToken ct)
    {
        await App02a3Sql.Lock(db, tx, $"DEVF03:RESERVE:{sku:N}:{location:N}", ct);
        decimal? step = await db.QuerySingleOrDefaultAsync<decimal?>(Sql("""
            SELECT q.BaseStep FROM dbo.AOrderLine a JOIN dbo.F03DraftQuantity q
                ON q.TenantId=a.TenantId AND q.DraftId=a.AOrderId AND q.LineNumber=a.LineNumber
            WHERE a.TenantId=@Tenant AND a.Id=@OrderLine
            """, new { Tenant = tenant, OrderLine = orderLine }, tx, ct));
        if (step is null || quantity % step.Value != 0) return [];
        var eligible = await db.QueryAsync<F03Pick>(Sql($"""
            SELECT o.LotId,o.Quantity-used.Quantity AS Quantity
            FROM dbo.F03LotOpening o
            JOIN dbo.F03Lot l ON l.TenantId=o.TenantId AND l.Id=o.LotId
            JOIN dbo.StockLocation loc ON loc.TenantId=o.TenantId AND loc.Id=o.LocationId
            CROSS APPLY (
                SELECT COALESCE(SUM(h.Quantity),0) AS Quantity FROM dbo.F03LotHold h
                JOIN dbo.StockRight r ON r.TenantId=h.TenantId AND r.Id=h.StockRightId
                WHERE h.TenantId=o.TenantId AND h.LotId=o.LotId AND r.StockLocationId=o.LocationId
            ) used
            WHERE o.TenantId=@Tenant AND o.LocationId=@Location AND l.SkuId=@Sku
                AND loc.Kind='WAREHOUSE' AND loc.IsActive=1 AND l.ExpiresOn>={LotBusinessDate}
                AND o.Quantity>used.Quantity
            ORDER BY l.ExpiresOn,l.Code COLLATE Latin1_General_100_BIN2,l.Id
            """, new { Tenant = tenant, Location = location, Sku = sku }, tx, ct));
        return PickLots(eligible, quantity);
    }

    private static async Task<IReadOnlyList<F03Pick>> PlanLotLoad(SqlConnection db, SqlTransaction tx,
        string tenant, Guid right, decimal quantity, CancellationToken ct)
    {
        if (!await MeetsLotStep(db, tx, tenant, right, quantity, ct)) return [];
        var eligible = await ReadHeldLots(db, tx, tenant, [right], ct);
        return PickLots(eligible.Select(lot => lot.ToPick()), quantity);
    }

    private static Task<IEnumerable<F03LotAvailability>> ReadHeldLots(SqlConnection db, SqlTransaction tx,
        string tenant, Guid[] rights, CancellationToken ct) =>
        db.QueryAsync<F03LotAvailability>(Sql($"""
            SELECT h.StockRightId AS SourceId,h.LotId,l.Code,l.ExpiresOn,h.Quantity-used.Quantity AS Quantity
            FROM dbo.F03LotHold h JOIN dbo.F03Lot l ON l.TenantId=h.TenantId AND l.Id=h.LotId
            CROSS APPLY (
                SELECT COALESCE(SUM(p.Quantity),0) AS Quantity FROM dbo.F03LotLoad p
                JOIN dbo.TripManifestLine m ON m.TenantId=p.TenantId AND m.Id=p.ManifestLineId
                WHERE p.TenantId=h.TenantId AND p.LotId=h.LotId AND m.StockRightId=h.StockRightId
            ) used
            WHERE h.TenantId=@Tenant AND h.StockRightId IN @Rights AND l.ExpiresOn>={LotBusinessDate}
                AND h.Quantity>used.Quantity
            ORDER BY l.ExpiresOn,l.Code COLLATE Latin1_General_100_BIN2,l.Id
            """, new { Tenant = tenant, Rights = rights }, tx, ct));

    private static async Task<IReadOnlyList<F03Pick>?> PlanLotIssue(SqlConnection db, SqlTransaction tx,
        string tenant, Guid right, Guid trip, Guid orderLine, Guid manifestLine, decimal quantity,
        DateTime businessDate, CancellationToken ct)
    {
        if (!await MeetsLotStep(db, tx, tenant, right, quantity, ct)) return [];
        var manifestLines = (await db.QueryAsync<Guid>(Sql("""
            SELECT m.Id FROM dbo.TripManifestLine m
            JOIN dbo.AReservation a ON a.TenantId=m.TenantId AND a.StockRightId=m.StockRightId
            WHERE m.TenantId=@Tenant AND a.AOrderLineId=@OrderLine AND m.TripId=@Trip AND m.IsC3=1
            """, new { Tenant = tenant, OrderLine = orderLine, Trip = trip }, tx, ct))).ToArray();
        var eligible = (await ReadLoadedLots(db, tx, tenant, manifestLines, businessDate, ct)).AsList();
        var selected = eligible.Where(lot => lot.SourceId == manifestLine).ToArray();
        var picks = PickLots(selected.Select(lot => lot.ToPick()), quantity);
        if (picks.Count == 0) return picks;
        var last = selected[picks.Count - 1];
        return eligible.Any(lot => lot.SourceId != manifestLine &&
            (lot.ExpiresOn < last.ExpiresOn || (lot.ExpiresOn == last.ExpiresOn
                && StringComparer.Ordinal.Compare(lot.Code, last.Code) < 0))) ? null : picks;
    }

    private static Task<IEnumerable<F03LotAvailability>> ReadLoadedLots(SqlConnection db, SqlTransaction tx,
        string tenant, Guid[] manifestLines, DateTime? businessDate, CancellationToken ct) =>
        db.QueryAsync<F03LotAvailability>(Sql($"""
            SELECT p.ManifestLineId AS SourceId,p.LotId,l.Code,l.ExpiresOn,p.Quantity-used.Quantity AS Quantity
            FROM dbo.F03LotLoad p JOIN dbo.F03Lot l ON l.TenantId=p.TenantId AND l.Id=p.LotId
            CROSS APPLY (
                SELECT COALESCE(SUM(i.Quantity),0) AS Quantity FROM dbo.F03LotIssue i
                JOIN dbo.SaleDelivery d ON d.TenantId=i.TenantId AND d.Id=i.DeliveryId
                WHERE i.TenantId=p.TenantId AND i.LotId=p.LotId AND d.TripManifestLineId=p.ManifestLineId
            ) used
            WHERE p.TenantId=@Tenant AND p.ManifestLineId IN @ManifestLines
                AND l.ExpiresOn>=COALESCE(@BusinessDate,{LotBusinessDate}) AND p.Quantity>used.Quantity
            ORDER BY l.ExpiresOn,l.Code COLLATE Latin1_General_100_BIN2,l.Id
            """, new { Tenant = tenant, ManifestLines = manifestLines, BusinessDate = businessDate }, tx, ct));

    private static async Task<bool> MeetsLotStep(SqlConnection db, SqlTransaction tx, string tenant,
        Guid right, decimal quantity, CancellationToken ct)
    {
        decimal? step = await db.QuerySingleOrDefaultAsync<decimal?>(Sql("""
            SELECT q.BaseStep FROM dbo.AReservation r JOIN dbo.AOrderLine a ON a.TenantId=r.TenantId AND a.Id=r.AOrderLineId
            JOIN dbo.F03DraftQuantity q ON q.TenantId=a.TenantId AND q.DraftId=a.AOrderId AND q.LineNumber=a.LineNumber
            WHERE r.TenantId=@Tenant AND r.StockRightId=@Right
            """, new { Tenant = tenant, Right = right }, tx, ct));
        return step is not null && quantity % step.Value == 0;
    }

    private static IReadOnlyList<F03Pick> PickLots(IEnumerable<F03Pick> eligible, decimal quantity)
    {
        var picks = new List<F03Pick>();
        foreach (var lot in eligible)
        {
            decimal picked = Math.Min(lot.Quantity, quantity);
            picks.Add(lot with { Quantity = picked });
            quantity -= picked;
            if (quantity == 0) return picks;
        }
        return [];
    }

    private static async Task<HashSet<Guid>> GetTrackedSources(SqlConnection db, SqlTransaction tx, string tenant,
        Guid[] ids, string table, string sourceColumn, CancellationToken ct) =>
        (await db.QueryAsync<Guid>(Sql($"""
            SELECT DISTINCT [{sourceColumn}]
            FROM dbo.[{table}] WHERE TenantId=@Tenant AND [{sourceColumn}] IN @Ids
            """, new { Tenant = tenant, Ids = ids }, tx, ct))).ToHashSet();

    private static async Task RecordLotPicks(SqlConnection db, SqlTransaction tx, string tenant, Guid id,
        IReadOnlyList<F03Pick> picks, string table, string sourceColumn, CancellationToken ct)
    {
        foreach (var pick in picks)
            await db.ExecuteAsync(Sql($"""
                INSERT dbo.[{table}](TenantId,[{sourceColumn}],LotId,Quantity) VALUES(@Tenant,@Id,@LotId,@Quantity)
                """, new { Tenant = tenant, Id = id, pick.LotId, pick.Quantity }, tx, ct));
    }
}
