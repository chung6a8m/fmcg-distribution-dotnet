using System.Security.Claims;
using Dapper;
using Microsoft.Data.SqlClient;

namespace FmcgDms.Api.Persistence;

public sealed partial class App01dCashierUnloadService
{
    private const string AllocationKind = "CASH_RECEIPT_ALLOCATION";

    private sealed record AllocationScope(Guid CustomerId, decimal Received, decimal ReceiptApplied,
        decimal ReceiptRecorded, decimal Outstanding, decimal ItemApplied, decimal ItemRecorded);

    public Task<BusinessCommandOutcome> AllocateCashReceiptAsync(
        ClaimsPrincipal principal, AllocateCashReceiptCommand request, CancellationToken ct) =>
        ExecuteAsync(principal, request, AllocationKind, "sales", request.TripId,
            [("TripId", request.TripId.ToString("N")),
             ("CashReceiptId", request.CashReceiptId.ToString("N")),
             ("ReceivableOpenItemId", request.ReceivableOpenItemId.ToString("N")),
             ("Amount", CommandPayloadHash.Decimal(request.Amount))],
            AllocateAsync, ct);

    private static async Task<BusinessCommandOutcome> AllocateAsync(
        SqlConnection db, SqlTransaction tx, string tenant, string actor,
        Guid commandId, AllocateCashReceiptCommand request, CancellationToken ct)
    {
        if (request.CashReceiptId == Guid.Empty || request.ReceivableOpenItemId == Guid.Empty
            || !ValidAmount(request.Amount) || decimal.Round(request.Amount, 4) != request.Amount)
            return BusinessCommandOutcome.Rejected("ALLOCATION_INPUT_INVALID");
        if (await SourceUsed(db, tx, tenant, AllocationKind, request.SourceNumber, ct))
            return BusinessCommandOutcome.Rejected("SOURCE_ALREADY_USED");

        var scope = await db.QuerySingleOrDefaultAsync<AllocationScope>(Sql("""
            SELECT r.CustomerId,r.ReceivedAmount AS Received,r.AppliedAmount AS ReceiptApplied,
                (SELECT COALESCE(SUM(a.AppliedAmount),0) FROM dbo.ReceiptAllocation a
                 WHERE a.TenantId=r.TenantId AND a.CashReceiptId=r.Id) AS ReceiptRecorded,
                i.OutstandingAmount AS Outstanding,i.AppliedAmount AS ItemApplied,
                (SELECT COALESCE(SUM(a.AppliedAmount),0) FROM dbo.ReceiptAllocation a
                 WHERE a.TenantId=i.TenantId AND a.ReceivableOpenItemId=i.Id)
                     + COALESCE(targetPayment.CreditApplied,0) AS ItemRecorded
            FROM dbo.CashReceipt r WITH (UPDLOCK,HOLDLOCK)
            JOIN dbo.ReceivableOpenItem i WITH (UPDLOCK,HOLDLOCK)
              ON i.TenantId=r.TenantId AND i.CustomerId=r.CustomerId AND i.Id=@ReceivableOpenItemId
            JOIN dbo.SaleDelivery d ON d.TenantId=i.TenantId AND d.Id=i.SaleDeliveryId AND d.TripId=r.TripId
            LEFT JOIN dbo.BLocalSalePayment targetPayment
              ON targetPayment.TenantId=i.TenantId AND targetPayment.SaleDeliveryId=i.SaleDeliveryId
            WHERE r.TenantId=@Tenant AND r.TripId=@TripId AND r.Id=@CashReceiptId
                AND r.PaymentMethod=N'CASH' AND r.VerificationStatus=N'VERIFIED'
                AND NOT EXISTS (SELECT 1 FROM dbo.BLocalSalePayment origin
                    WHERE origin.TenantId=r.TenantId AND origin.SaleDeliveryId=r.SaleDeliveryId AND origin.CreditIssued>0);
            """, new { Tenant = tenant, request.TripId, request.CashReceiptId, request.ReceivableOpenItemId }, tx, ct));
        if (scope is null)
            return BusinessCommandOutcome.Rejected("ALLOCATION_SCOPE_INVALID");
        if (scope.ReceiptApplied != scope.ReceiptRecorded || scope.ItemApplied != scope.ItemRecorded)
            return BusinessCommandOutcome.Rejected("ALLOCATION_SOURCE_DRIFT");
        if (scope.Received - scope.ReceiptApplied < request.Amount)
            return BusinessCommandOutcome.Rejected("RECEIPT_UNAPPLIED_INSUFFICIENT");
        if (scope.Outstanding < request.Amount)
            return BusinessCommandOutcome.Rejected("RECEIVABLE_OUTSTANDING_INSUFFICIENT");

        Guid source = Guid.NewGuid(), line = Guid.NewGuid();
        await db.ExecuteAsync(Sql($"""
            INSERT dbo.BusinessSource(Id,TenantId,BusinessCommandId,SourceKind,SourceNumber,ActorId,
                CustomerId,TripId,OccurredAtUtc,RecordedAtUtc)
            VALUES(@Source,@Tenant,@Command,@Kind,@Number,@Actor,@Customer,@TripId,{App02c6Clock.SqlUtcNow},{App02c6Clock.SqlUtcNow});
            INSERT dbo.BusinessSourceLine(Id,TenantId,BusinessSourceId,LineNumber,Amount)
            VALUES(@Line,@Tenant,@Source,1,@Amount);
            INSERT dbo.ReceiptAllocation(Id,TenantId,CashReceiptId,ReceivableOpenItemId,
                CustomerId,AppliedAmount,AllocationSourceLineId,RecordedAtUtc)
            VALUES(NEWID(),@Tenant,@CashReceiptId,@ReceivableOpenItemId,@Customer,@Amount,@Line,{App02c6Clock.SqlUtcNow});

            UPDATE dbo.CashReceipt SET AppliedAmount=AppliedAmount+@Amount
            WHERE TenantId=@Tenant AND Id=@CashReceiptId AND ReceivedAmount-AppliedAmount>=@Amount;
            IF @@ROWCOUNT <> 1 THROW 51090, 'Receipt allocation counter changed', 1;
            UPDATE dbo.ReceivableOpenItem SET AppliedAmount=AppliedAmount+@Amount
            WHERE TenantId=@Tenant AND Id=@ReceivableOpenItemId AND OutstandingAmount>=@Amount;
            IF @@ROWCOUNT <> 1 THROW 51091, 'Receivable allocation counter changed', 1;

            INSERT dbo.LedgerEffect(Id,TenantId,BusinessSourceLineId,EffectKey,LedgerKind,CustomerId,
                QuantityDelta,AmountDelta,RecordedAtUtc)
            VALUES(NEWID(),@Tenant,@Line,N'AR-CASH-ALLOC',N'AR',@Customer,0,-@Amount,{App02c6Clock.SqlUtcNow});
            """, new { Source = source, Line = line, Tenant = tenant, Command = commandId,
                Kind = AllocationKind, Number = request.SourceNumber, Actor = actor,
                Customer = scope.CustomerId, request.TripId, request.CashReceiptId,
                request.ReceivableOpenItemId, request.Amount }, tx, ct));
        return BusinessCommandOutcome.Applied("CASH_RECEIPT_ALLOCATED", source);
    }
}
