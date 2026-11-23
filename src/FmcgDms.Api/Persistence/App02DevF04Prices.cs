using Dapper;
using Microsoft.Data.SqlClient;

namespace FmcgDms.Api.Persistence;

public sealed record F04PriceView(string Mode, string Currency, long Base, long Discount, long Net, long Tax,
    long Gross, int Version);

public sealed partial class App01a2CommandService
{
    private const string SyntheticVnd = "VND";
    private sealed record F04Plan(Guid Id, int Version, string TaxMode, long OrderDiscount, DateTime EffectiveUntilUtc,
        string Maker, string Checker, DateTime ApprovedAtUtc, string EvidenceRef);
    private sealed record F04Snapshot(Guid OrderLineId, string Mode, string Currency, long Base, long Discount, long Net,
        long Tax, long Gross, int Version, decimal QuoteUnitPrice, decimal TaxRate, Guid QuotePolicyId)
    {
        internal F04PriceView ToView() => new(Mode, Currency, Base, Discount, Net, Tax, Gross, Version);
    }

    private static async Task<F04Plan?> ApprovedPricePlan(SqlConnection db, SqlTransaction tx, string tenant,
        string actor, Guid customerId, DateTime now, CancellationToken ct)
    {
        var plan = await db.QuerySingleOrDefaultAsync<F04Plan>(Sql("""
            SELECT TOP(1) Id,Version,TaxMode,OrderDiscount,EffectiveUntilUtc,Maker,Checker,ApprovedAtUtc,EvidenceRef
            FROM dbo.F04PricePlan WITH (HOLDLOCK)
            WHERE TenantId=@Tenant AND CustomerId=@CustomerId AND EffectiveFromUtc<=@Now
            ORDER BY Version DESC
            """, C2ClockParameters(new { Tenant = tenant, CustomerId = customerId }, now), tx, ct));
        return plan is not null && now < plan.EffectiveUntilUtc && plan.ApprovedAtUtc <= now
            && !string.IsNullOrWhiteSpace(plan.Maker) && !string.IsNullOrWhiteSpace(plan.Checker)
            && !string.IsNullOrWhiteSpace(plan.EvidenceRef)
            && !string.Equals(plan.Maker.Trim(), plan.Checker.Trim(), StringComparison.OrdinalIgnoreCase)
            && !string.Equals(actor, plan.Checker.Trim(), StringComparison.OrdinalIgnoreCase)
            ? plan : null;
    }

    private static async Task<HashSet<Guid>> ApprovedPlanQuotes(SqlConnection db, SqlTransaction tx, string tenant,
        Guid plan, CancellationToken ct) =>
        (await db.QueryAsync<Guid>(Sql("""
            SELECT PolicyId FROM dbo.F04PlanQuote WHERE TenantId=@Tenant AND PlanId=@PlanId
            """, new { Tenant = tenant, PlanId = plan }, tx, ct))).ToHashSet();

    private static Task<int> RecordPriceLine(SqlConnection db, SqlTransaction tx, string tenant, Guid lineId,
        F04Plan plan, F04PricedLine price, C2CommercialPolicy quote, CancellationToken ct) => db.ExecuteAsync(Sql("""
            INSERT dbo.F04PriceLine(TenantId,OrderLineId,PlanId,QuotePolicyId,SourceBase,AllocatedDiscount,
                NetAmount,TaxAmount,GrossAmount,QuoteUnitPrice,TaxRate)
            VALUES(@Tenant,@LineId,@PlanId,@QuotePolicyId,@Base,@Discount,@Net,@Tax,@Gross,@UnitPrice,@TaxRate)
            """, new { Tenant = tenant, LineId = lineId, PlanId = plan.Id, QuotePolicyId = quote.Id, price.Base, price.Discount,
                price.Net, price.Tax, price.Gross, quote.UnitPrice, quote.TaxRate }, tx, ct));

    private static Task<IEnumerable<F04Snapshot>> ReadPrices(SqlConnection db, SqlTransaction tx, string tenant,
        Guid[] lines, CancellationToken ct) => db.QueryAsync<F04Snapshot>(Sql("""
            SELECT l.OrderLineId,p.TaxMode AS Mode,l.Currency,l.SourceBase AS Base,l.AllocatedDiscount AS Discount,
                   l.NetAmount AS Net,l.TaxAmount AS Tax,l.GrossAmount AS Gross,p.Version,l.QuoteUnitPrice,l.TaxRate,l.QuotePolicyId
            FROM dbo.F04PriceLine l JOIN dbo.F04PricePlan p ON p.TenantId=l.TenantId AND p.Id=l.PlanId
            WHERE l.TenantId=@Tenant AND l.OrderLineId IN @Lines
            """, new { Tenant = tenant, Lines = lines }, tx, ct));

    private sealed record F04DeliveryCurrency(Guid SourceId, string Currency);

    private static Task<IEnumerable<F04DeliveryCurrency>> ReadDeliveryCurrencies(SqlConnection db, SqlTransaction tx,
        string tenant, Guid trip, CancellationToken ct) => db.QueryAsync<F04DeliveryCurrency>(Sql("""
            SELECT d.BusinessSourceId AS SourceId,p.Currency
            FROM dbo.F04DeliveryPricing p JOIN dbo.SaleDelivery d ON d.TenantId=p.TenantId AND d.Id=p.DeliveryId
            WHERE d.TenantId=@Tenant AND d.TripId=@Trip
            """, new { Tenant = tenant, Trip = trip }, tx, ct));

    private static Task<int> RecordDeliveryPrice(SqlConnection db, SqlTransaction tx, string tenant,
        Guid sourceId, Guid orderLineId, F04PriceAmount amount, CancellationToken ct) => db.ExecuteAsync(Sql("""
            INSERT dbo.F04DeliveryPricing(TenantId,DeliveryId,OrderLineId,NetAmount,TaxAmount,GrossAmount,Currency)
            SELECT d.TenantId,d.Id,d.AOrderLineId,@Net,@Tax,@Gross,@Currency
            FROM dbo.SaleDelivery d WHERE d.TenantId=@Tenant AND d.BusinessSourceId=@SourceId AND d.AOrderLineId=@OrderLineId
            """, new { Tenant = tenant, SourceId = sourceId, OrderLineId = orderLineId,
                amount.Net, amount.Tax, amount.Gross, Currency = SyntheticVnd }, tx, ct));
}
