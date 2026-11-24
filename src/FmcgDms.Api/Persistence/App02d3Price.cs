using Dapper;
using Microsoft.Data.SqlClient;

namespace FmcgDms.Api.Persistence;

public sealed partial class App01c2BPostingService
{
    private sealed record D3Price(int PolicyVersion, decimal UnitPrice, string Maker, string Checker, string EvidenceRef);

    private async Task<bool> IsD3Trip(SqlConnection db, SqlTransaction tx, string tenant,
        Guid trip, CancellationToken ct)
    {
        if (!connections.UsesApp02d3 || connections.UsesApp03Settlement) return false;
        return await db.ExecuteScalarAsync<int>(Sql("""
            SELECT COUNT(*) FROM dbo.D3CashierTrip WITH (HOLDLOCK)
            WHERE TenantId=@Tenant AND TripId=@Trip;
            """, new { Tenant = tenant, Trip = trip }, tx, ct)) == 1;
    }

    private static async Task<D3Price?> EffectiveD3Price(SqlConnection db, SqlTransaction tx, string tenant,
        Guid trip, Guid sku, CancellationToken ct)
    {
        var profiles = (await db.QueryAsync<D3Price>(Sql($"""
            DECLARE @Now datetime2(7)={App02c6Clock.SqlUtcNow};
            SELECT TOP (2) PolicyVersion,UnitPrice,Maker,Checker,EvidenceRef
            FROM dbo.D3PriceProfile WITH (HOLDLOCK)
            WHERE TenantId=@Tenant AND TripId=@Trip AND SkuId=@Sku AND Decision='APPROVED'
                AND ApprovedAtUtc<=@Now AND EffectiveFromUtc<=@Now AND EffectiveUntilUtc>@Now
            ORDER BY PolicyVersion;
            """, new { Tenant = tenant, Trip = trip, Sku = sku }, tx, ct))).AsList();
        if (profiles.Count != 1) return null;
        var profile = profiles[0];
        return profile.UnitPrice == 15 && ValidKey(profile.Maker, 128)
            && ValidKey(profile.Checker, 128) && ValidKey(profile.EvidenceRef, 128)
            && !string.Equals(profile.Maker, profile.Checker, StringComparison.OrdinalIgnoreCase)
            ? profile : null;
    }

    private static Task<int> RecordD3Price(SqlConnection db, SqlTransaction tx, string tenant,
        Guid delivery, PostBSaleDeliveryReceiptCommand request, D3Price price, CancellationToken ct) =>
        db.ExecuteAsync(Sql("""
            INSERT dbo.D3SalePricing(TenantId,SaleDeliveryId,TripId,SkuId,PolicyVersion,UnitPrice)
            VALUES(@Tenant,@Delivery,@Trip,@Sku,@PolicyVersion,@UnitPrice);
            """, new { Tenant = tenant, Delivery = delivery, Trip = request.TripId, Sku = request.SkuId,
                price.PolicyVersion, price.UnitPrice }, tx, ct));
}
