using Dapper;
using Microsoft.Data.SqlClient;

namespace FmcgDms.Api.Persistence;

internal static class App03FieldB
{
    internal static readonly string BusinessDate = $"CAST(({App02c6Clock.SqlUtcNow} AT TIME ZONE 'UTC') AT TIME ZONE 'SE Asia Standard Time' AS date)";

    // Used only in the guarded profile where F03 is installed.
    internal const string NonGiftSku = """
        NOT EXISTS (SELECT 1 FROM dbo.F07Program WHERE TenantId=k.TenantId AND GiftSkuId=k.Id)
        """;

    internal static string EligibleSku(bool settlement) => BaseEligibleSku + (settlement ? " AND " + NonGiftSku : "");

    private const string BaseEligibleSku = """
        k.BaseUom='EA'
        AND NOT EXISTS (SELECT 1 FROM dbo.F03Lot WITH (HOLDLOCK) WHERE TenantId=k.TenantId AND SkuId=k.Id)
        AND NOT EXISTS (SELECT 1 FROM dbo.F03Unit WITH (HOLDLOCK) WHERE TenantId=k.TenantId AND SkuId=k.Id)
        """;
}

public sealed partial class App01a2CommandService
{
    internal sealed record FieldBPrice(Guid PlanId, Guid QuotePolicyId, decimal UnitPrice);

    internal static async Task<FieldBPrice?> ReadFieldBPrice(SqlConnection db, SqlTransaction tx,
        string tenant, string actor, Guid customerId, Guid skuId, CancellationToken ct)
    {
        var now = await db.ExecuteScalarAsync<DateTime>(Sql($"SELECT {App02c6Clock.SqlUtcNow}", null, tx, ct));
        var plan = await ApprovedPricePlan(db, tx, tenant, actor, customerId, now, ct);
        if (plan is not { TaxMode: "EXCLUSIVE", OrderDiscount: 0 }) return null;
        var quote = await db.QuerySingleOrDefaultAsync<C2CommercialPolicy>(Sql("""
            SELECT TOP(1) Id,Version,Decision,EffectiveFromUtc,EffectiveUntilUtc,Maker,Checker,
                ApprovedAtUtc,EvidenceRef,UnitPrice,DiscountPerUnit,TaxRate,TermsDays,CreditCap,SellerTimeZone
            FROM dbo.C2CommercialPolicy WITH (HOLDLOCK)
            WHERE TenantId=@Tenant AND CustomerId=@CustomerId AND SkuId=@SkuId AND EffectiveFromUtc<=@Now
            ORDER BY Version DESC
            """, C2ClockParameters(new { Tenant = tenant, CustomerId = customerId, SkuId = skuId }, now), tx, ct));
        if (quote is not { UnitPrice: decimal price, TaxRate: 0, DiscountPerUnit: 0 }
            || !ValidC2Policy(quote, actor, now) || price != decimal.Truncate(price)
            || !(await ApprovedPlanQuotes(db, tx, tenant, plan.Id, ct)).Contains(quote.Id))
            return null;
        return new(plan.Id, quote.Id, price);
    }
}
