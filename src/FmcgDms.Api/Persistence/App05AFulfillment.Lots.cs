using Dapper;
using Microsoft.Data.SqlClient;

namespace FmcgDms.Api.Persistence;

internal static partial class App05CloseReadiness
{
    private sealed record TransferLot(Guid LotId, decimal NeverLoaded, decimal Returned);
    private sealed record TransferAllocation(decimal NeverLoaded, decimal Returned, TransferLot[] Lots);

    private static async Task<TransferAllocation?> AllocateTransferLots(SqlConnection db, SqlTransaction tx,
        string tenant, ALine line, TransferRight right, decimal quantity, DateTime businessDate, CancellationToken ct, bool closing = false)
    {
        var scope = new { Tenant = tenant, Line = line.OrderLineId, line.SkuId, right.RightId, BusinessDate = businessDate, Closing = closing };
        var tracked = await db.ExecuteScalarAsync<bool>(App02a3Sql.Cmd("""
            SELECT CONVERT(bit,CASE WHEN EXISTS(
                SELECT 1 FROM dbo.F03Unit WHERE TenantId=@Tenant AND SkuId=@SkuId
                UNION ALL
                SELECT 1 FROM dbo.AOrderLine a JOIN dbo.F03DraftQuantity q
                    ON q.TenantId=a.TenantId AND q.DraftId=a.AOrderId AND q.LineNumber=a.LineNumber
                WHERE a.TenantId=@Tenant AND a.Id=@Line AND q.LotTracked=1)
                THEN 1 ELSE 0 END);
            """, scope, tx, ct));
        if (!tracked)
        {
            decimal neverLoaded = Math.Min(right.NeverLoaded, quantity);
            return new(neverLoaded, quantity - neverLoaded, []);
        }

        var available = await db.QueryAsync<TransferLot>(App02a3Sql.Cmd("""
            SELECT h.LotId,h.Quantity-m.Loaded-x.NeverLoaded-closed.NeverLoaded AS NeverLoaded,
                u.Returned-x.Returned-closed.Returned AS Returned
            FROM dbo.F03LotHold h JOIN dbo.F03Lot l ON l.TenantId=h.TenantId AND l.Id=h.LotId
            CROSS APPLY(SELECT CONVERT(decimal(18,3),COALESCE(SUM(q.Quantity),0)) AS Loaded
                FROM dbo.F03LotLoad q JOIN dbo.TripManifestLine m
                    ON m.TenantId=q.TenantId AND m.Id=q.ManifestLineId
                WHERE q.TenantId=h.TenantId AND q.LotId=h.LotId AND m.StockRightId=h.StockRightId) m
            CROSS APPLY(SELECT CONVERT(decimal(18,3),COALESCE(SUM(q.Quantity),0)) AS Returned
                FROM dbo.App03LotUnload q JOIN dbo.D3TripUnloadLine d
                    ON d.TenantId=q.TenantId AND d.TripManifestLineId=q.ManifestLineId
                WHERE q.TenantId=h.TenantId AND q.LotId=h.LotId AND d.StockRightId=h.StockRightId) u
            CROSS APPLY(SELECT CONVERT(decimal(18,3),COALESCE(SUM(x.NeverLoaded),0)) AS NeverLoaded,CONVERT(decimal(18,3),COALESCE(SUM(x.Returned),0)) AS Returned
                FROM dbo.App05AHeldTransferLot x
                WHERE x.TenantId=h.TenantId AND x.OriginalRightId=h.StockRightId AND x.LotId=h.LotId) x
            CROSS APPLY(SELECT CONVERT(decimal(18,3),COALESCE(SUM(c.NeverLoaded),0)) AS NeverLoaded,
                CONVERT(decimal(18,3),COALESCE(SUM(c.Returned),0)) AS Returned
                FROM dbo.App05ARemainderReleaseLot c
                WHERE c.TenantId=h.TenantId AND c.StockRightId=h.StockRightId AND c.LotId=h.LotId) closed
            WHERE h.TenantId=@Tenant AND h.StockRightId=@RightId AND l.SkuId=@SkuId
                AND (@Closing=1 OR l.ExpiresOn>=@BusinessDate)
            ORDER BY l.ExpiresOn,l.Code COLLATE Latin1_General_100_BIN2,l.Id;
            """, scope, tx, ct));
        var picks = new List<TransferLot>();
        decimal remaining = quantity;
        foreach (var lot in available)
        {
            if (lot.NeverLoaded < 0 || lot.Returned < 0)
                return null;
            decimal neverLoaded = Math.Min(lot.NeverLoaded, remaining);
            decimal returned = Math.Min(lot.Returned, remaining - neverLoaded);
            if (neverLoaded + returned > 0)
                picks.Add(new(lot.LotId, neverLoaded, returned));
            remaining -= neverLoaded + returned;
            if (remaining == 0) break;
        }
        decimal pickedNever = picks.Sum(p => p.NeverLoaded), pickedReturned = picks.Sum(p => p.Returned);
        return remaining == 0 && pickedNever <= right.NeverLoaded && pickedReturned <= right.Returned
            ? new(pickedNever, pickedReturned, picks.ToArray()) : null;
    }
}
