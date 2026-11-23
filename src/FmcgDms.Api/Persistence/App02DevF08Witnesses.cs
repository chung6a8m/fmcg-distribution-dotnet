using Dapper;
using Microsoft.Data.SqlClient;

namespace FmcgDms.Api.Persistence;

public sealed partial class App01a2CommandService
{
    private static async Task<BusinessCommandOutcome> WitnessSalePayment(SqlConnection db, SqlTransaction tx,
        string tenant, string actor, Guid command, string number, F08Command input, CancellationToken ct)
    {
        var sale = await FindRefundableSale(db, tx, tenant, input.DeliveryId!.Value, ct);
        if (sale is null) return BusinessCommandOutcome.Rejected("F08_SOURCE_NOT_FOUND");
        long amount = input.Amount!.Value;
        if (sale.Outstanding != amount) return BusinessCommandOutcome.Rejected("F08_PAYMENT_AMOUNT_MISMATCH");
        if (await db.ExecuteScalarAsync<int>(Sql("""
            SELECT COUNT(*) FROM dbo.F08PaymentWitness WHERE TenantId=@Tenant AND DeliveryId=@Delivery
            """, new { Tenant = tenant, Delivery = sale.Id }, tx, ct)) != 0)
            return BusinessCommandOutcome.Rejected("F08_WITNESS_EXISTS");

        Guid id = await AddSource(db, tx, tenant, command, "F08_WITNESS_PAYMENT", number, actor, sale.CustomerId, sale.TripId, ct);
        await AddLine(db, tx, tenant, id, 1, sale.SkuId, null, amount, ct);
        await db.ExecuteAsync(Sql("""
            INSERT dbo.F08PaymentWitness(TenantId,Id,DeliveryId,Amount,ActorId,Evidence,Reason)
            VALUES(@Tenant,@Id,@Delivery,@Amount,@Actor,@Evidence,@Reason)
            """, new { Tenant = tenant, Id = id, Delivery = sale.Id, Amount = amount, Actor = actor,
                input.Evidence, input.Reason }, tx, ct));
        return BusinessCommandOutcome.Applied("F08_WITNESS_PAYMENT", id);
    }

    private static async Task<BusinessCommandOutcome> WitnessPayout(SqlConnection db, SqlTransaction tx,
        string tenant, string actor, Guid command, string number, F08Command input,
        F08Refund refund, F08Sale sale, CancellationToken ct)
    {
        if (refund.Checker is null || refund.Disbursed)
            return BusinessCommandOutcome.Rejected("F08_APPROVED_REFUND_REQUIRED");
        if (refund.Amount != input.Amount) return BusinessCommandOutcome.Rejected("F08_DISBURSEMENT_AMOUNT_MISMATCH");
        if (await db.ExecuteScalarAsync<int>(Sql("""
            SELECT COUNT(*) FROM dbo.F08PayoutWitness WHERE TenantId=@Tenant AND RefundId=@Refund
            """, new { Tenant = tenant, Refund = refund.Id }, tx, ct)) != 0)
            return BusinessCommandOutcome.Rejected("F08_WITNESS_EXISTS");

        long? cashAvailable = await db.QuerySingleOrDefaultAsync<long?>(Sql("""
            SELECT t.AvailableAmount-r.Reserved FROM dbo.F08Treasury t
            CROSS APPLY (SELECT COALESCE(SUM(w.Amount),0) AS Reserved
                FROM dbo.F08PayoutWitness w LEFT JOIN dbo.F08Disbursement d ON d.TenantId=w.TenantId AND d.WitnessId=w.Id
                WHERE w.TenantId=t.TenantId AND d.Id IS NULL) r
            WHERE t.TenantId=@Tenant
            """, new { Tenant = tenant }, tx, ct));
        if (cashAvailable is null || cashAvailable < refund.Amount)
            return BusinessCommandOutcome.Rejected("F08_TREASURY_UNAVAILABLE");

        Guid id = await AddSource(db, tx, tenant, command, "F08_WITNESS_PAYOUT", number, actor, sale.CustomerId, sale.TripId, ct);
        await AddLine(db, tx, tenant, id, 1, sale.SkuId, null, refund.Amount, ct);
        await db.ExecuteAsync(Sql("""
            INSERT dbo.F08PayoutWitness(TenantId,Id,RefundId,Amount,ActorId,Evidence,Reason)
            VALUES(@Tenant,@Id,@Refund,@Amount,@Actor,@Evidence,@Reason)
            """, new { Tenant = tenant, Id = id, Refund = refund.Id, refund.Amount, Actor = actor,
                input.Evidence, input.Reason }, tx, ct));
        return BusinessCommandOutcome.Applied("F08_WITNESS_PAYOUT", id);
    }
}
