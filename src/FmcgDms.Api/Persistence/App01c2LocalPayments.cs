using Dapper;
using Microsoft.Data.SqlClient;

namespace FmcgDms.Api.Persistence;

public sealed partial class App01c2BPostingService
{
    private sealed record LocalPayment(decimal CashReceived, decimal CashApplied,
        decimal CreditApplied, decimal CreditIssued, DateTime? CashVerifiedAt, bool PendingBank = false)
    {
        public decimal TotalApplied => CashApplied + CreditApplied;
        public bool IsExtended => PendingBank || CreditApplied > 0 || CreditIssued > 0;
    }

    private static async Task<(LocalPayment? Payment, string? Rejection)> ResolvePaymentAsync(
        SqlConnection db, SqlTransaction tx, string tenant, string actor,
        PostBSaleDeliveryReceiptCommand request, decimal payable, DateTime? now, CancellationToken ct)
    {
        var scope = new
        {
            Tenant = tenant, Trip = request.TripId, Customer = request.CustomerId,
            ShipTo = request.ShipToSnapshotId, Evidence = request.VerificationEvidenceRef,
            Bank = request.PendingBankEvidenceRef, Reservation = request.CustomerCreditReservationId
        };
        if (request.PendingBankEvidenceRef is not null)
        {
            int matched = await db.ExecuteScalarAsync<int>(Sql("""
                SELECT COUNT(*) FROM dbo.BLocalPendingBankEvidence WITH (UPDLOCK,HOLDLOCK)
                WHERE TenantId=@Tenant AND EvidenceRef=@Bank AND TripId=@Trip
                    AND CustomerId=@Customer AND ShipToSnapshotId=@ShipTo;
                """, scope, tx, ct));
            if (matched != 1)
                return (null, "BANK_PENDING_EVIDENCE_INVALID");
            int used = await db.ExecuteScalarAsync<int>(Sql("""
                SELECT COUNT(*) FROM dbo.BLocalSalePayment WITH (UPDLOCK,HOLDLOCK)
                WHERE TenantId=@Tenant AND PendingBankEvidenceRef=@Bank;
                """, scope, tx, ct));
            return used == 0 ? (new LocalPayment(0, 0, 0, 0, null, PendingBank: true), null)
                : (null, "BANK_PENDING_EVIDENCE_ALREADY_USED");
        }

        var witness = await db.QuerySingleOrDefaultAsync<VerifiedCash>(Sql("""
            SELECT TripId,CustomerId,ShipToSnapshotId,VerifiedAmount,
                PaymentMethod,VerificationStatus,VerifiedBy,VerifiedAtUtc
            FROM dbo.VerifiedCashEvidence WITH (UPDLOCK,HOLDLOCK)
            WHERE TenantId=@Tenant AND EvidenceRef=@Evidence
            """, scope, tx, ct));
        if (witness is null || witness.TripId != request.TripId
            || witness.CustomerId != request.CustomerId
            || witness.ShipToSnapshotId != request.ShipToSnapshotId
            || witness.VerifiedAmount != request.ReceivedCashAmount
            || witness.PaymentMethod != "CASH" || witness.VerificationStatus != "VERIFIED"
            || witness.VerifiedAtUtc is null
            || witness.VerifiedAtUtc.Value > (now ?? DateTime.UtcNow)
            || string.IsNullOrWhiteSpace(witness.VerifiedBy)
            || string.Equals(witness.VerifiedBy?.Trim(), actor, StringComparison.OrdinalIgnoreCase))
            return (null, "CASH_EVIDENCE_NOT_VERIFIED");
        bool evidenceUsed = await db.ExecuteScalarAsync<bool>(Sql("""
            SELECT CAST(CASE WHEN EXISTS (
                SELECT 1 FROM dbo.CashReceipt WITH (UPDLOCK,HOLDLOCK)
                WHERE TenantId=@Tenant AND VerificationEvidenceRef=@Evidence
            ) THEN 1 ELSE 0 END AS bit)
            """, scope, tx, ct));
        if (evidenceUsed)
            return (null, "CASH_EVIDENCE_ALREADY_USED");

        decimal cashApplied = Math.Min(payable, request.ReceivedCashAmount);
        decimal customerCredit = 0m;
        if (request.CustomerCreditReservationId is not null)
        {
            decimal? reserved = await db.QuerySingleOrDefaultAsync<decimal?>(Sql("""
                SELECT h.Amount
                FROM dbo.BLocalCreditReservation h WITH (UPDLOCK,HOLDLOCK)
                JOIN dbo.BLocalSalePayment origin WITH (UPDLOCK,HOLDLOCK)
                  ON origin.TenantId=h.TenantId AND origin.SaleDeliveryId=h.CreditSaleId
                JOIN dbo.CashReceipt r WITH (UPDLOCK,HOLDLOCK)
                  ON r.TenantId=origin.TenantId AND r.SaleDeliveryId=origin.SaleDeliveryId
                  AND r.CustomerId=h.CustomerId
                WHERE h.TenantId=@Tenant AND h.Id=@Reservation AND h.CustomerId=@Customer
                  AND h.TripId=@Trip AND h.ShipToSnapshotId=@ShipTo
                  AND r.ReceivedAmount-r.AppliedAmount=origin.CreditIssued
                  AND origin.CreditIssued >= (SELECT SUM(x.Amount)
                      FROM dbo.BLocalCreditReservation x WITH (HOLDLOCK)
                      WHERE x.TenantId=h.TenantId AND x.CreditSaleId=h.CreditSaleId)
                  AND NOT EXISTS (SELECT 1 FROM dbo.BLocalSalePayment used WITH (UPDLOCK,HOLDLOCK)
                      WHERE used.TenantId=h.TenantId AND used.CreditReservationId=h.Id);
                """, scope, tx, ct));
            if (reserved is not > 0 || reserved > payable - cashApplied)
                return (null, "CUSTOMER_CREDIT_RESERVATION_INVALID");
            customerCredit = reserved.Value;
        }
        return (new LocalPayment(request.ReceivedCashAmount, cashApplied, customerCredit,
            request.ReceivedCashAmount - cashApplied, witness.VerifiedAtUtc), null);
    }
}
