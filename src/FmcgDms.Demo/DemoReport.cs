using System.Data;
using System.Security.Cryptography;
using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;

namespace FmcgDms.Demo;

internal sealed record DemoCell(string Field, decimal? Expected, decimal Actual)
{
    public decimal? Difference => Actual - Expected;
}
internal sealed record DemoCheckpoint(string Step, DateTimeOffset ObservedAt, string? Outcome, DemoCell[] Cells);
internal sealed record DemoLedgerLine(string SourceNumber, string SourceKind, Guid SourceId,
    Guid LineId, Guid EffectId, string LedgerKind, Guid? StockLocationId, decimal Quantity, decimal Amount);
internal sealed record DemoOpening(string SnapshotCode, decimal Quantity, decimal Amount);
internal sealed record DemoContribution(string Label, string Kind, decimal Value, DemoLedgerLine? Line = null);
internal sealed record DemoBreakdown(string Step, DemoCell Cell, IReadOnlyList<DemoContribution> Rows);

internal sealed partial class DemoScenario
{
    public const string SeedVersion = "AC03-v1";
    private static readonly byte[] OracleBytes =
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "golden-oracles.json"));
    public static string OracleHash => Convert.ToHexString(SHA256.HashData(OracleBytes));

    private static async Task Capture(SqlConnection db, string tenant, DemoScene s, int step,
        FmcgDms.Api.Persistence.BusinessCommandOutcome? outcome)
    {
        var values = (IDictionary<string, object>)await db.QuerySingleAsync("""
            WITH entries AS (
                SELECT e.LedgerKind, e.AmountDelta, src.SourceKind
                FROM dbo.BusinessSource src
                JOIN dbo.BusinessSourceLine l ON l.TenantId=src.TenantId AND l.BusinessSourceId=src.Id
                JOIN dbo.LedgerEffect e ON e.TenantId=l.TenantId AND e.BusinessSourceLineId=l.Id
                WHERE src.TenantId=@Tenant AND src.TripId=@Trip
            )
            SELECT
              (SELECT OnHandBaseQuantity FROM dbo.PhysicalStockBalance WHERE TenantId=@Tenant AND SkuId=@Sku AND StockLocationId=@Warehouse) mainQty,
              (SELECT OnHandBaseQuantity FROM dbo.PhysicalStockBalance WHERE TenantId=@Tenant AND SkuId=@Sku AND StockLocationId=@Vehicle) tripQty,
              (SELECT COALESCE(SUM(BaseQuantity),0) FROM dbo.SaleDelivery WHERE TenantId=@Tenant AND TripId=@Trip) soldQty,
              (SELECT OnHandCostAmount FROM dbo.PhysicalStockBalance WHERE TenantId=@Tenant AND SkuId=@Sku AND StockLocationId=@Warehouse) mainCost,
              (SELECT OnHandCostAmount FROM dbo.PhysicalStockBalance WHERE TenantId=@Tenant AND SkuId=@Sku AND StockLocationId=@Vehicle) tripCost,
              (SELECT COALESCE(SUM(AmountDelta),0) FROM entries WHERE LedgerKind='COGS') cogs,
              (SELECT COALESCE(SUM(GrantedBaseQuantity-ConsumedBaseQuantity-ReleasedBaseQuantity),0)
                 FROM dbo.StockRight WHERE TenantId=@Tenant AND TripId=@Trip AND RightKind='A') aHold,
              (SELECT COALESCE(SUM(GrantedBaseQuantity-ConsumedBaseQuantity-ReleasedBaseQuantity),0)
                 FROM dbo.StockRight WHERE TenantId=@Tenant AND TripId=@Trip AND RightKind='B') bQuota,
              (SELECT COALESCE(SUM(AmountDelta),0) FROM entries WHERE LedgerKind='AR' AND SourceKind='A_DELIVERY') arA,
              (SELECT COALESCE(SUM(AmountDelta),0) FROM entries WHERE LedgerKind='AR' AND SourceKind='B_SALE_DELIVERY') arB,
              (SELECT COALESCE(SUM(AmountDelta),0) FROM entries WHERE LedgerKind='CUSTODY') custody,
              (SELECT COALESCE(SUM(AmountDelta),0) FROM entries WHERE LedgerKind='FUND') fund
            """, new { Tenant = tenant, s.Trip, s.Sku, s.Warehouse, s.Vehicle });
        using var oracle = JsonDocument.Parse(OracleBytes);
        var ac03 = oracle.RootElement.GetProperty("AC-03");
        var columns = ac03.GetProperty("cols").EnumerateArray().Skip(1).Select(x => x.GetString()!);
        var row = ac03.GetProperty("steps")[step];
        bool comparable = s.Inputs.Opening == 100 && s.Inputs.UnitCost == 10;
        var cells = columns.Select((field, i) => new DemoCell(field,
            comparable ? row[i + 1].GetDecimal() : null, (decimal)values[field])).ToArray();
        var observedAt = await db.ExecuteScalarAsync<DateTime>("SELECT SYSUTCDATETIME()");
        var checkpoint = new DemoCheckpoint(row[0].GetString()!,
            new DateTimeOffset(observedAt, TimeSpan.Zero), outcome?.OutcomeCode, cells);
        await db.ExecuteAsync("INSERT dbo.LocalDemoCheckpoint VALUES(@step,@data)",
            new { step, data = JsonSerializer.Serialize(checkpoint) });
    }

    public static async Task<DemoCheckpoint[]> Checkpoints(SqlConnection db) =>
        (await db.QueryAsync<string>("SELECT Data FROM dbo.LocalDemoCheckpoint ORDER BY Step"))
        .Select(data => JsonSerializer.Deserialize<DemoCheckpoint>(data)!).ToArray();

    public static async Task<DemoBreakdown?> Breakdown(SqlConnection db, string tenant, DemoScene scene,
        DemoCheckpoint[] points, string step, string field)
    {
        var point = points.SingleOrDefault(p => p.Step == step);
        var cell = point?.Cells.SingleOrDefault(c => c.Field == field);
        if (point is null || cell is null) return null;

        var parameters = new DynamicParameters(new { tenant, scene.Trip });
        parameters.Add("cutoff", point.ObservedAt.UtcDateTime, DbType.DateTime2);
        var entries = await db.QueryAsync<DemoLedgerLine>("""
            SELECT src.SourceNumber, src.SourceKind, src.Id AS SourceId, l.Id AS LineId, e.Id AS EffectId,
                   e.LedgerKind, e.StockLocationId, e.QuantityDelta AS Quantity, e.AmountDelta AS Amount
            FROM dbo.BusinessSource src
            JOIN dbo.BusinessSourceLine l ON l.TenantId=src.TenantId AND l.BusinessSourceId=src.Id
            JOIN dbo.LedgerEffect e ON e.TenantId=l.TenantId AND e.BusinessSourceLineId=l.Id
            WHERE src.TenantId=@tenant AND src.TripId=@Trip AND src.RecordedAtUtc<=@cutoff AND e.RecordedAtUtc<=@cutoff
            ORDER BY src.RecordedAtUtc, src.Id, l.LineNumber, e.EffectKey
            """, parameters);
        var rows = new List<DemoContribution>();
        Guid? location = field switch
        {
            "mainQty" or "mainCost" => scene.Warehouse,
            "tripQty" or "tripCost" => scene.Vehicle,
            _ => null
        };
        if (location is Guid locationId)
        {
            var opening = await db.QuerySingleOrDefaultAsync<DemoOpening>("""
                SELECT SnapshotCode, BaseQuantity AS Quantity, CostAmount AS Amount
                FROM dbo.PhysicalStockOpening
                WHERE TenantId=@tenant AND SkuId=@Sku AND StockLocationId=@locationId
                """, new { tenant, scene.Sku, locationId });
            if (opening is not null)
                rows.Add(new(opening.SnapshotCode, "Tồn đầu kỳ", field.EndsWith("Cost", StringComparison.Ordinal)
                    ? opening.Amount : opening.Quantity));
        }
        foreach (var entry in entries)
        {
            decimal? impact = field switch
            {
                "mainQty" or "tripQty" when entry.LedgerKind == "PHYSICAL_STOCK" && entry.StockLocationId == location
                    => entry.Quantity,
                "mainCost" or "tripCost" when entry.LedgerKind == "PHYSICAL_STOCK" && entry.StockLocationId == location
                    => entry.Amount,
                "soldQty" when entry.LedgerKind == "PHYSICAL_STOCK" && entry.SourceKind is "A_DELIVERY" or "B_SALE_DELIVERY"
                    => -entry.Quantity,
                "aHold" when entry.LedgerKind is "RIGHT_A" or "RIGHT_A_CONSUMED" => entry.Quantity,
                "bQuota" when entry.LedgerKind is "RIGHT_B" or "RIGHT_B_CONSUMED" or "RIGHT_B_RELEASED"
                    => entry.Quantity,
                "arA" when entry.LedgerKind == "AR" && entry.SourceKind == "A_DELIVERY" => entry.Amount,
                "arB" when entry.LedgerKind == "AR" && entry.SourceKind == "B_SALE_DELIVERY" => entry.Amount,
                "cogs" when entry.LedgerKind == "COGS" => entry.Amount,
                "custody" when entry.LedgerKind == "CUSTODY" => entry.Amount,
                "fund" when entry.LedgerKind == "FUND" => entry.Amount,
                _ => null
            };
            if (impact is decimal value) rows.Add(new(entry.SourceNumber, entry.LedgerKind, value, entry));
        }
        return new(point.Step, cell, rows);
    }

    public static Task<IEnumerable<dynamic>> Sources(SqlConnection db, DemoScene s) => db.QueryAsync("""
        SELECT src.SourceNumber, src.SourceKind, src.Id AS SourceId, l.Id AS LineId,
               e.Id AS EffectId, e.LedgerKind, e.QuantityDelta, e.AmountDelta,
               COALESCE(e.RecordedAtUtc, src.RecordedAtUtc) AS RecordedAtUtc
        FROM dbo.BusinessSource src
        LEFT JOIN dbo.BusinessSourceLine l ON l.TenantId=src.TenantId AND l.BusinessSourceId=src.Id
        LEFT JOIN dbo.LedgerEffect e ON e.TenantId=l.TenantId AND e.BusinessSourceLineId=l.Id
        WHERE src.TripId=@Trip AND src.TenantId=(SELECT TenantId FROM dbo.AppTenantBinding WHERE Slot=1)
        ORDER BY src.RecordedAtUtc, src.Id, l.LineNumber, e.EffectKey
        """, new { s.Trip });
}
