using System.Data;
using System.Security.Claims;
using Dapper;
using FmcgDms.Api.Authentication;
using FmcgDms.Api.Tenancy;
using Microsoft.Data.SqlClient;

namespace FmcgDms.Api.Persistence;

/// <summary>
/// Logical UTC as-of view of committed synthetic source/rights events. This is
/// not SQL temporal history or a historical snapshot of mutable pool counters.
/// </summary>
public sealed class App01a3AuditService(TenantConnections connections)
{
    // Reconcile B posting's immutable source-backed records/effects as part of
    // audit-as-of, without assuming mutable current right/grant counters are
    // historical snapshots at an earlier cutoff.
    private sealed class BPostingEvidence
    {
        public Guid DeliveryId { get; set; }
        public Guid BusinessSourceLineId { get; set; }
        public decimal BaseQuantity { get; set; }
        public decimal PayableAmount { get; set; }
        public decimal CostAmount { get; set; }
        public decimal? IssuedQuantity { get; set; }
        public decimal? IssuedCost { get; set; }
        public decimal? ArOriginal { get; set; }
        public decimal? ArApplied { get; set; }
        public decimal? ArOutstanding { get; set; }
        public decimal? CashReceived { get; set; }
        public decimal? CashApplied { get; set; }
        public decimal ReceiptAllocated { get; set; }
        public decimal CreditUsed { get; set; }
        public decimal CustomerCreditApplied { get; set; }
        public decimal CustomerCreditIssued { get; set; }
        public bool PendingBank { get; set; }
    }

    private sealed class APostingEvidence
    {
        public Guid DeliveryId { get; set; }
        public Guid BusinessSourceLineId { get; set; }
        public decimal BaseQuantity { get; set; }
        public decimal PayableAmount { get; set; }
        public decimal CostAmount { get; set; }
        public DateTime BusinessDate { get; set; }
        public decimal ConfirmedBaseQuantity { get; set; }
        public decimal UnitPriceSnapshot { get; set; }
        public int TermsDaysSnapshot { get; set; }
        public decimal? IssuedQuantity { get; set; }
        public decimal? IssuedCost { get; set; }
        public decimal? ArOriginal { get; set; }
        public decimal? ArApplied { get; set; }
        public decimal? ArOutstanding { get; set; }
        public DateTime? DueBusinessDate { get; set; }
        public int Receipts { get; set; }
        public int BGrantConsumptions { get; set; }
    }

    public async Task<App01aAuditSnapshot> QueryAsync(
        ClaimsPrincipal principal, DateTimeOffset asOf, CancellationToken ct)
    {
        if (!principal.HasClaim("synthetic_role", "auditor"))
            throw new UnauthorizedAccessException("Synthetic audit role required.");
        string tenant = SyntheticAuth.GetTenantId(principal);
        if (asOf == default || asOf > DateTimeOffset.UtcNow.AddMinutes(1))
            throw new ArgumentOutOfRangeException(nameof(asOf), "Invalid UTC audit cutoff.");

        // No tenant selector is accepted. One SERIALIZABLE read transaction keeps
        // commands, source lines, effects, approvals and aggregates consistent.
        await using SqlConnection db = await connections.OpenSyntheticScratchAsync(principal, ct);
        await using SqlTransaction tx =
            (SqlTransaction)await db.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        try
        {
            using var multi = await db.QueryMultipleAsync(new CommandDefinition("""
                SELECT Id,ProducerId,CommandId,ActorId,SubmittedAtUtc,CompletedAtUtc,
                    CASE WHEN CompletedAtUtc IS NOT NULL AND CompletedAtUtc > @AsOf
                        THEN N'PENDING_AS_OF' ELSE Status END AS Status,
                    CASE WHEN CompletedAtUtc > @AsOf THEN NULL ELSE OutcomeCode END AS OutcomeCode,
                    CASE WHEN CompletedAtUtc > @AsOf THEN NULL ELSE ReasonCode END AS ReasonCode
                FROM dbo.BusinessCommand
                WHERE TenantId=@Tenant AND SubmittedAtUtc<=@AsOf
                ORDER BY SubmittedAtUtc,Id;

                SELECT s.Id,s.BusinessCommandId,s.SourceKind,s.SourceNumber,s.ActorId,
                    s.CustomerId,s.TripId,s.RecordedAtUtc,
                    (SELECT COUNT_BIG(*) FROM dbo.BusinessSourceLine l
                        WHERE l.TenantId=s.TenantId AND l.BusinessSourceId=s.Id) AS LineCount,
                    (SELECT COUNT_BIG(*) FROM dbo.BusinessSourceLine l
                        JOIN dbo.LedgerEffect e ON e.TenantId=l.TenantId
                            AND e.BusinessSourceLineId=l.Id
                        WHERE l.TenantId=s.TenantId AND l.BusinessSourceId=s.Id
                            AND e.RecordedAtUtc<=@AsOf) AS EffectCount
                FROM dbo.BusinessSource s
                JOIN dbo.BusinessCommand c ON c.TenantId=s.TenantId
                    AND c.Id=s.BusinessCommandId
                WHERE s.TenantId=@Tenant AND s.RecordedAtUtc<=@AsOf
                    AND c.CompletedAtUtc<=@AsOf AND c.Status=N'APPLIED'
                ORDER BY s.RecordedAtUtc,s.Id;

                SELECT l.Id,l.BusinessSourceId AS SourceId,l.LineNumber,
                    l.SkuId,l.BaseQuantity,l.Amount
                FROM dbo.BusinessSourceLine l
                JOIN dbo.BusinessSource s ON s.TenantId=l.TenantId
                    AND s.Id=l.BusinessSourceId
                JOIN dbo.BusinessCommand c ON c.TenantId=s.TenantId
                    AND c.Id=s.BusinessCommandId
                WHERE l.TenantId=@Tenant AND s.RecordedAtUtc<=@AsOf
                    AND c.CompletedAtUtc<=@AsOf AND c.Status=N'APPLIED'
                ORDER BY s.RecordedAtUtc,s.Id,l.LineNumber,l.Id;

                SELECT e.Id,e.BusinessSourceLineId,s.Id AS SourceId,e.EffectKey,
                    e.LedgerKind,e.CustomerId,e.SkuId,e.StockLocationId,
                    e.QuantityDelta,e.AmountDelta,e.RecordedAtUtc
                FROM dbo.LedgerEffect e
                JOIN dbo.BusinessSourceLine l ON l.TenantId=e.TenantId
                    AND l.Id=e.BusinessSourceLineId
                JOIN dbo.BusinessSource s ON s.TenantId=l.TenantId
                    AND s.Id=l.BusinessSourceId
                JOIN dbo.BusinessCommand c ON c.TenantId=s.TenantId
                    AND c.Id=s.BusinessCommandId
                WHERE e.TenantId=@Tenant AND e.RecordedAtUtc<=@AsOf
                    AND s.RecordedAtUtc<=@AsOf
                    AND c.CompletedAtUtc<=@AsOf AND c.Status=N'APPLIED'
                ORDER BY e.RecordedAtUtc,e.Id;

                SELECT g.Id,g.BusinessSourceLineId,g.ApprovalId,p.CaseNumber,p.PolicyVersion,
                    p.ProposedBy,p.CheckedBy,p.Decision,p.EvidenceRef,
                    p.Reason AS ApprovalReason,p.ApprovedLimit,
                    p.EffectiveFromUtc,p.EffectiveUntilUtc,p.ReviewedAtUtc,
                    g.TripId,g.CustomerId,g.GrantedAmount,s.Id AS SourceId
                FROM dbo.BCreditGrant g
                JOIN dbo.BusinessSourceLine l ON l.TenantId=g.TenantId
                    AND l.Id=g.BusinessSourceLineId
                JOIN dbo.BusinessSource s ON s.TenantId=l.TenantId
                    AND s.Id=l.BusinessSourceId
                JOIN dbo.BusinessCommand c ON c.TenantId=s.TenantId
                    AND c.Id=s.BusinessCommandId
                LEFT JOIN dbo.CreditPolicyApproval p ON p.TenantId=g.TenantId
                    AND p.Id=g.ApprovalId
                WHERE g.TenantId=@Tenant AND s.RecordedAtUtc<=@AsOf
                    AND c.CompletedAtUtc<=@AsOf AND c.Status=N'APPLIED'
                ORDER BY s.RecordedAtUtc,g.Id;

                SELECT r.Id,r.BusinessSourceLineId,r.RightKind,
                    r.StockLocationId,r.SkuId,r.GrantedBaseQuantity
                FROM dbo.StockRight r
                JOIN dbo.BusinessSourceLine l ON l.TenantId=r.TenantId
                    AND l.Id=r.BusinessSourceLineId
                JOIN dbo.BusinessSource s ON s.TenantId=l.TenantId
                    AND s.Id=l.BusinessSourceId
                JOIN dbo.BusinessCommand c ON c.TenantId=s.TenantId
                    AND c.Id=s.BusinessCommandId
                WHERE r.TenantId=@Tenant AND s.RecordedAtUtc<=@AsOf
                    AND c.CompletedAtUtc<=@AsOf AND c.Status=N'APPLIED'
                ORDER BY s.RecordedAtUtc,r.Id;

                WITH RightsAndEffects AS (
                    SELECT r.StockLocationId,r.SkuId,
                        r.GrantedBaseQuantity AS RightGranted,
                        CONVERT(decimal(18,3),0) AS EffectGranted
                    FROM dbo.StockRight r
                    JOIN dbo.BusinessSourceLine l ON l.TenantId=r.TenantId
                        AND l.Id=r.BusinessSourceLineId
                    JOIN dbo.BusinessSource s ON s.TenantId=l.TenantId
                        AND s.Id=l.BusinessSourceId
                    JOIN dbo.BusinessCommand c ON c.TenantId=s.TenantId
                        AND c.Id=s.BusinessCommandId
                    WHERE r.TenantId=@Tenant AND s.RecordedAtUtc<=@AsOf
                        AND c.CompletedAtUtc<=@AsOf AND c.Status=N'APPLIED'
                    UNION ALL
                    SELECT e.StockLocationId,e.SkuId,
                        CONVERT(decimal(18,3),0),e.QuantityDelta
                    FROM dbo.LedgerEffect e
                    JOIN dbo.BusinessSourceLine l ON l.TenantId=e.TenantId
                        AND l.Id=e.BusinessSourceLineId
                    JOIN dbo.BusinessSource s ON s.TenantId=l.TenantId
                        AND s.Id=l.BusinessSourceId
                    JOIN dbo.BusinessCommand c ON c.TenantId=s.TenantId
                        AND c.Id=s.BusinessCommandId
                    WHERE e.TenantId=@Tenant AND e.RecordedAtUtc<=@AsOf
                        AND s.RecordedAtUtc<=@AsOf
                        AND c.CompletedAtUtc<=@AsOf AND c.Status=N'APPLIED'
                        AND e.LedgerKind IN (N'RIGHT_A',N'RIGHT_B')
                )
                SELECT StockLocationId,SkuId,SUM(RightGranted) AS RightGranted,
                    SUM(EffectGranted) AS EffectGranted
                FROM RightsAndEffects
                GROUP BY StockLocationId,SkuId
                ORDER BY StockLocationId,SkuId;

                -- APP-01C.2: source-bound B posting reconciliation is independent
                -- of the earlier A/B grant-allocation audit. Remove subsequent
                -- standalone allocations to recover the delivery's original counters.
                SELECT d.Id AS DeliveryId,d.BusinessSourceLineId,
                    d.BaseQuantity,d.PayableAmount,d.CostAmount,
                    i.BaseQuantity AS IssuedQuantity,i.CostAmount AS IssuedCost,
                    ar.OriginalAmount AS ArOriginal,
                    ar.AppliedAmount-COALESCE(laterAr.Applied,0) AS ArApplied,
                    ar.OutstandingAmount+COALESCE(laterAr.Applied,0) AS ArOutstanding,
                    receipt.ReceivedAmount AS CashReceived,
                    receipt.AppliedAmount-COALESCE(laterCash.Applied,0) AS CashApplied,
                    COALESCE(payment.CreditApplied,0) AS CustomerCreditApplied,
                    COALESCE(payment.CreditIssued,0) AS CustomerCreditIssued,
                    CAST(CASE WHEN payment.PendingBankEvidenceRef IS NULL THEN 0 ELSE 1 END AS bit) AS PendingBank,
                    (SELECT COALESCE(SUM(a.AppliedAmount),0)
                        FROM dbo.ReceiptAllocation a
                        WHERE a.TenantId=d.TenantId
                            AND a.CashReceiptId=receipt.Id
                            AND a.ReceivableOpenItemId=ar.Id
                            AND a.AllocationSourceLineId IS NULL) AS ReceiptAllocated,
                    (SELECT COALESCE(SUM(x.ConsumedAmount),0)
                        FROM dbo.BGrantConsumption x
                        WHERE x.TenantId=d.TenantId
                            AND x.SaleDeliveryId=d.Id) AS CreditUsed
                FROM dbo.SaleDelivery d
                JOIN dbo.BusinessSource s ON s.TenantId=d.TenantId
                    AND s.Id=d.BusinessSourceId AND s.SourceKind=N'B_SALE_DELIVERY'
                JOIN dbo.BusinessCommand c ON c.TenantId=s.TenantId
                    AND c.Id=s.BusinessCommandId AND c.Status=N'APPLIED'
                LEFT JOIN dbo.DeliveryStockIssue i ON i.TenantId=d.TenantId
                    AND i.SaleDeliveryId=d.Id
                LEFT JOIN dbo.ReceivableOpenItem ar ON ar.TenantId=d.TenantId
                    AND ar.SaleDeliveryId=d.Id
                LEFT JOIN dbo.CashReceipt receipt ON receipt.TenantId=d.TenantId
                    AND receipt.SaleDeliveryId=d.Id
                LEFT JOIN dbo.BLocalSalePayment payment ON payment.TenantId=d.TenantId AND payment.SaleDeliveryId=d.Id
                OUTER APPLY (SELECT CONVERT(decimal(19,4),SUM(a.AppliedAmount)) AS Applied FROM dbo.ReceiptAllocation a
                    WHERE a.TenantId=ar.TenantId AND a.ReceivableOpenItemId=ar.Id
                      AND a.AllocationSourceLineId IS NOT NULL) laterAr
                OUTER APPLY (SELECT CONVERT(decimal(19,4),SUM(a.AppliedAmount)) AS Applied FROM dbo.ReceiptAllocation a
                    WHERE a.TenantId=receipt.TenantId AND a.CashReceiptId=receipt.Id
                      AND a.AllocationSourceLineId IS NOT NULL) laterCash
                WHERE d.TenantId=@Tenant
                    AND s.RecordedAtUtc<=@AsOf AND c.CompletedAtUtc<=@AsOf
                ORDER BY s.RecordedAtUtc,d.Id;

                -- APP-01C.3: frozen confirmation is the only price/terms
                -- authority for a posted A partial delivery. All four legs
                -- must belong to exactly this immutable delivery source.
                SELECT d.Id AS DeliveryId,d.BusinessSourceLineId,
                    d.BaseQuantity,d.PayableAmount,d.CostAmount,d.BusinessDate,
                    snap.ConfirmedBaseQuantity,snap.UnitPriceSnapshot,
                    snap.TermsDaysSnapshot,
                    i.BaseQuantity AS IssuedQuantity,i.CostAmount AS IssuedCost,
                    ar.OriginalAmount AS ArOriginal,
                    ar.AppliedAmount-COALESCE(laterAr.Applied,0) AS ArApplied,
                    ar.OutstandingAmount+COALESCE(laterAr.Applied,0) AS ArOutstanding,ar.DueBusinessDate,
                    (SELECT COUNT(*) FROM dbo.CashReceipt r
                     WHERE r.TenantId=d.TenantId AND r.SaleDeliveryId=d.Id) AS Receipts,
                    (SELECT COUNT(*) FROM dbo.BGrantConsumption g
                     WHERE g.TenantId=d.TenantId AND g.SaleDeliveryId=d.Id) AS BGrantConsumptions
                FROM dbo.SaleDelivery d
                JOIN dbo.BusinessSource src ON src.TenantId=d.TenantId
                    AND src.Id=d.BusinessSourceId AND src.SourceKind=N'A_DELIVERY'
                JOIN dbo.BusinessCommand c ON c.TenantId=src.TenantId
                    AND c.Id=src.BusinessCommandId AND c.Status=N'APPLIED'
                JOIN dbo.AConfirmedLineSnapshot snap
                    ON snap.TenantId=d.TenantId AND snap.AOrderLineId=d.AOrderLineId
                    AND snap.AOrderId=d.AOrderId AND snap.SkuId=d.SkuId
                LEFT JOIN dbo.DeliveryStockIssue i ON i.TenantId=d.TenantId
                    AND i.SaleDeliveryId=d.Id
                LEFT JOIN dbo.ReceivableOpenItem ar ON ar.TenantId=d.TenantId
                    AND ar.SaleDeliveryId=d.Id
                OUTER APPLY (SELECT CONVERT(decimal(19,4),SUM(a.AppliedAmount)) AS Applied FROM dbo.ReceiptAllocation a
                    WHERE a.TenantId=ar.TenantId AND a.ReceivableOpenItemId=ar.Id
                      AND a.AllocationSourceLineId IS NOT NULL) laterAr
                WHERE d.TenantId=@Tenant
                    AND src.RecordedAtUtc<=@AsOf AND c.CompletedAtUtc<=@AsOf
                ORDER BY src.RecordedAtUtc,d.Id;
                """, new { Tenant = tenant, AsOf = asOf.UtcDateTime },
                tx, cancellationToken: ct));

            var commands = (await multi.ReadAsync<App01aAuditCommand>()).AsList();
            var sources = (await multi.ReadAsync<App01aAuditSource>()).AsList();
            var sourceLines = (await multi.ReadAsync<App01aAuditSourceLine>()).AsList();
            var effects = (await multi.ReadAsync<App01aAuditEffect>()).AsList();
            var credits = (await multi.ReadAsync<App01aAuditCredit>()).AsList();
            var rights = (await multi.ReadAsync<App01aAuditRight>()).AsList();
            var stock = (await multi.ReadAsync<App01aAuditStock>()).AsList();
            var bPostings = (await multi.ReadAsync<BPostingEvidence>()).AsList();
            var aPostings = (await multi.ReadAsync<APostingEvidence>()).AsList();

            // Reconciliation issues are evidence, never silently repaired.
            // Per-line checks prevent offsetting ledger errors from hiding behind a
            // matching (warehouse, SKU) total. A_CONFIRM lines have no rights/effects.
            var issues = new List<string>();
            var sourceIds = sources.Select(s => s.Id).ToHashSet();
            var linesById = sourceLines.ToDictionary(l => l.Id);
            foreach (var c in commands.Where(c => c.Status == "APPLIED"))
                if (!sources.Any(s => s.BusinessCommandId == c.Id))
                    issues.Add($"APPLIED_WITHOUT_SOURCE:{c.Id:N}");
            foreach (var s in sources)
                if (s.LineCount < 1 || !sourceLines.Any(l => l.SourceId == s.Id))
                    issues.Add($"SOURCE_WITHOUT_LINE:{s.Id:N}");
            foreach (var e in effects)
                if (!sourceIds.Contains(e.SourceId) || !linesById.ContainsKey(e.BusinessSourceLineId))
                    issues.Add($"EFFECT_WITHOUT_SOURCE_LINE:{e.Id:N}");
            foreach (var right in rights)
            {
                string effectKind = right.RightKind == "A" ? "RIGHT_A" : "RIGHT_B";
                var matches = effects.Where(e => e.BusinessSourceLineId == right.BusinessSourceLineId
                    && e.LedgerKind == effectKind).ToArray();
                if (!linesById.TryGetValue(right.BusinessSourceLineId, out var line)
                    || line.SkuId != right.SkuId
                    || line.BaseQuantity != right.GrantedBaseQuantity
                    || matches.Length != 1
                    || matches[0].SkuId != right.SkuId
                    || matches[0].StockLocationId != right.StockLocationId
                    || matches[0].QuantityDelta != right.GrantedBaseQuantity)
                    issues.Add($"STOCK_RIGHT_LINE_EFFECT_MISMATCH:{right.BusinessSourceLineId:N}");
            }
            foreach (var e in effects.Where(e => e.LedgerKind is "RIGHT_A" or "RIGHT_B"))
                if (!rights.Any(r => r.BusinessSourceLineId == e.BusinessSourceLineId
                    && e.LedgerKind == (r.RightKind == "A" ? "RIGHT_A" : "RIGHT_B")))
                    issues.Add($"STOCK_EFFECT_WITHOUT_RIGHT_LINE:{e.Id:N}");
            foreach (var g in credits)
            {
                if (g.ApprovalId is null || g.Decision != "APPROVED"
                    || string.IsNullOrWhiteSpace(g.EvidenceRef))
                    issues.Add($"CREDIT_WITHOUT_APPROVAL:{g.Id:N}");
                var matches = effects.Where(e => e.BusinessSourceLineId == g.BusinessSourceLineId
                    && e.LedgerKind == "RIGHT_CREDIT_B").ToArray();
                if (!linesById.TryGetValue(g.BusinessSourceLineId, out var line)
                    || line.Amount != g.GrantedAmount
                    || matches.Length != 1
                    || matches[0].AmountDelta != g.GrantedAmount
                    || matches[0].CustomerId != g.CustomerId)
                    issues.Add($"CREDIT_LINE_EFFECT_MISMATCH:{g.BusinessSourceLineId:N}");
            }
            foreach (var e in effects.Where(e => e.LedgerKind == "RIGHT_CREDIT_B"))
                if (!credits.Any(g => g.BusinessSourceLineId == e.BusinessSourceLineId))
                    issues.Add($"CREDIT_EFFECT_WITHOUT_GRANT_LINE:{e.Id:N}");
            foreach (var bucket in stock)
                if (bucket.RightGranted != bucket.EffectGranted)
                    issues.Add($"STOCK_RIGHT_EFFECT_MISMATCH:{bucket.StockLocationId:N}:{bucket.SkuId:N}");

            // A valid posted B sale cannot be a partial posting: every
            // immutable source line has exactly the physical/COGS, AR, cash,
            // right and credit effects that its delivery requires.
            var bSourceLineIds = bPostings.Select(p => p.BusinessSourceLineId).ToHashSet();
            foreach (var p in bPostings)
            {
                var legs = effects.Where(e => e.BusinessSourceLineId == p.BusinessSourceLineId)
                    .ToArray();
                bool Leg(string key, string kind, decimal qty, decimal amount) =>
                    legs.Count(e => e.EffectKey == key && e.LedgerKind == kind
                        && e.QuantityDelta == qty && e.AmountDelta == amount) == 1;
                decimal cashApplied = p.CashApplied ?? 0m;
                decimal totalApplied = cashApplied + p.CustomerCreditApplied;
                int expectedLegs = 4 + (p.PendingBank ? 0 : 2) + (p.CreditUsed > 0 ? 1 : 0)
                    + (p.CustomerCreditApplied > 0 ? 2 : 0) + (p.CustomerCreditIssued > 0 ? 1 : 0);
                bool receiptValid = p.PendingBank
                    ? p.CashReceived is null && p.CashApplied is null
                    : p.CashReceived is > 0 && cashApplied > 0
                        && p.CashReceived == cashApplied + p.CustomerCreditIssued
                        && Leg("AR-CASH-ALLOC", "AR", 0m, -cashApplied)
                        && Leg("CASH-CUSTODY", "CUSTODY", 0m, p.CashReceived.Value);
                if (p.IssuedQuantity != p.BaseQuantity || p.IssuedCost != p.CostAmount
                    || p.ArOriginal != p.PayableAmount || !receiptValid
                    || p.ArApplied != totalApplied
                    || p.ArOutstanding != p.PayableAmount - totalApplied
                    || p.ReceiptAllocated != cashApplied
                    || p.CreditUsed != p.PayableAmount - totalApplied
                    || !Leg("PHYSICAL-OUT", "PHYSICAL_STOCK", -p.BaseQuantity, -p.CostAmount)
                    || !Leg("COGS", "COGS", 0m, p.CostAmount)
                    || !Leg("AR-GROSS", "AR", 0m, p.PayableAmount)
                    || (p.CustomerCreditApplied > 0 && (!Leg("AR-CREDIT-ALLOC", "AR", 0m, -p.CustomerCreditApplied)
                        || !Leg("CUSTOMER-CREDIT-USED", "CUSTOMER_CREDIT", 0m, -p.CustomerCreditApplied)))
                    || (p.CustomerCreditIssued > 0
                        && !Leg("CUSTOMER-CREDIT-ISSUED", "CUSTOMER_CREDIT", 0m, p.CustomerCreditIssued))
                    || !Leg("RIGHT-B-USED", "RIGHT_B_CONSUMED", -p.BaseQuantity, 0m)
                    || (p.CreditUsed > 0
                        && !Leg("CREDIT-B-USED", "RIGHT_CREDIT_B_CONSUMED", 0m, -p.CreditUsed))
                    || legs.Length != expectedLegs)
                    issues.Add($"B_POSTING_SOURCE_LEDGER_MISMATCH:{p.DeliveryId:N}");
            }
            foreach (var leg in effects.Where(e => e.LedgerKind is
                "RIGHT_B_CONSUMED" or "RIGHT_CREDIT_B_CONSUMED"))
                if (!bSourceLineIds.Contains(leg.BusinessSourceLineId))
                    issues.Add($"B_CONSUMPTION_WITHOUT_DELIVERY:{leg.Id:N}");

            // As-of A posting ledger is source-reconciled, without interpreting
            // mutable current on-hand as a historical snapshot. A confirmation
            // by itself must not generate AR, COGS, receipt, or custody.
            var aSourceLineIds = aPostings.Select(p => p.BusinessSourceLineId).ToHashSet();
            foreach (var p in aPostings)
            {
                var legs = effects.Where(e => e.BusinessSourceLineId == p.BusinessSourceLineId)
                    .ToArray();
                bool Leg(string key, string kind, decimal qty, decimal amount) =>
                    legs.Count(e => e.EffectKey == key && e.LedgerKind == kind
                        && e.QuantityDelta == qty && e.AmountDelta == amount) == 1;
                bool dateValid = p.DueBusinessDate.HasValue
                    && p.TermsDaysSnapshot is >= 1 and <= 365
                    && p.BusinessDate.Date.AddDays(p.TermsDaysSnapshot)
                        == p.DueBusinessDate.Value.Date;
                if (p.BaseQuantity <= 0 || p.BaseQuantity > p.ConfirmedBaseQuantity
                    || p.UnitPriceSnapshot <= 0
                    || p.PayableAmount != p.BaseQuantity * p.UnitPriceSnapshot
                    || p.IssuedQuantity != p.BaseQuantity || p.IssuedCost != p.CostAmount
                    || p.ArOriginal != p.PayableAmount || p.ArApplied != 0
                    || p.ArOutstanding != p.PayableAmount || !dateValid
                    || p.Receipts != 0 || p.BGrantConsumptions != 0
                    || !Leg("PHYSICAL-OUT", "PHYSICAL_STOCK",
                        -p.BaseQuantity, -p.CostAmount)
                    || !Leg("COGS", "COGS", 0m, p.CostAmount)
                    || !Leg("AR-GROSS", "AR", 0m, p.PayableAmount)
                    || !Leg("RIGHT-A-USED", "RIGHT_A_CONSUMED", -p.BaseQuantity, 0m)
                    || legs.Length != 4)
                    issues.Add($"A_POSTING_SOURCE_LEDGER_MISMATCH:{p.DeliveryId:N}");
            }
            foreach (var leg in effects.Where(e => e.LedgerKind == "RIGHT_A_CONSUMED"))
                if (!aSourceLineIds.Contains(leg.BusinessSourceLineId))
                    issues.Add($"A_CONSUMPTION_WITHOUT_DELIVERY:{leg.Id:N}");

            await tx.CommitAsync(ct);
            return new App01aAuditSnapshot(tenant, asOf.ToUniversalTime(),
                commands, sources, sourceLines, effects, credits, rights, stock,
                commands.Where(c => c.Status is "PENDING" or "RECEIVED" or "PENDING_AS_OF").ToArray(),
                issues);
        }
        catch
        {
            try { await tx.RollbackAsync(CancellationToken.None); }
            catch (InvalidOperationException) { /* SQL may already have ended the transaction. */ }
            throw;
        }
    }
}

public sealed record App01aAuditSnapshot(
    string TenantId, DateTimeOffset AsOfUtc,
    IReadOnlyList<App01aAuditCommand> Commands,
    IReadOnlyList<App01aAuditSource> Sources,
    IReadOnlyList<App01aAuditSourceLine> SourceLines,
    IReadOnlyList<App01aAuditEffect> Effects,
    IReadOnlyList<App01aAuditCredit> CreditGrants,
    IReadOnlyList<App01aAuditRight> StockRights,
    IReadOnlyList<App01aAuditStock> StockAllocations,
    IReadOnlyList<App01aAuditCommand> Pending,
    IReadOnlyList<string> ReconciliationIssues);

public sealed class App01aAuditCommand
{
    public Guid Id { get; set; }
    public string ProducerId { get; set; } = "";
    public string CommandId { get; set; } = "";
    public string ActorId { get; set; } = "";
    public DateTime SubmittedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public string Status { get; set; } = "";
    public string? OutcomeCode { get; set; }
    public string? ReasonCode { get; set; }
}

public sealed class App01aAuditSource
{
    public Guid Id { get; set; }
    public Guid BusinessCommandId { get; set; }
    public string SourceKind { get; set; } = "";
    public string SourceNumber { get; set; } = "";
    public string ActorId { get; set; } = "";
    public Guid? CustomerId { get; set; }
    public Guid? TripId { get; set; }
    public DateTime RecordedAtUtc { get; set; }
    public long LineCount { get; set; }
    public long EffectCount { get; set; }
}

public sealed class App01aAuditSourceLine
{
    public Guid Id { get; set; }
    public Guid SourceId { get; set; }
    public int LineNumber { get; set; }
    public Guid? SkuId { get; set; }
    public decimal? BaseQuantity { get; set; }
    public decimal? Amount { get; set; }
}

public sealed class App01aAuditEffect
{
    public Guid Id { get; set; }
    public Guid BusinessSourceLineId { get; set; }
    public Guid SourceId { get; set; }
    public string EffectKey { get; set; } = "";
    public string LedgerKind { get; set; } = "";
    public Guid? CustomerId { get; set; }
    public Guid? SkuId { get; set; }
    public Guid? StockLocationId { get; set; }
    public decimal QuantityDelta { get; set; }
    public decimal AmountDelta { get; set; }
    public DateTime RecordedAtUtc { get; set; }
}

public sealed class App01aAuditCredit
{
    public Guid Id { get; set; }
    public Guid BusinessSourceLineId { get; set; }
    public Guid? ApprovalId { get; set; }
    public string? CaseNumber { get; set; }
    public string? PolicyVersion { get; set; }
    public string? ProposedBy { get; set; }
    public string? CheckedBy { get; set; }
    public string? Decision { get; set; }
    public string? EvidenceRef { get; set; }
    public string? ApprovalReason { get; set; }
    public decimal? ApprovedLimit { get; set; }
    public DateTime? EffectiveFromUtc { get; set; }
    public DateTime? EffectiveUntilUtc { get; set; }
    public DateTime? ReviewedAtUtc { get; set; }
    public Guid TripId { get; set; }
    public Guid CustomerId { get; set; }
    public decimal GrantedAmount { get; set; }
    public Guid SourceId { get; set; }
}

public sealed class App01aAuditRight
{
    public Guid Id { get; set; }
    public Guid BusinessSourceLineId { get; set; }
    public string RightKind { get; set; } = "";
    public Guid StockLocationId { get; set; }
    public Guid SkuId { get; set; }
    public decimal GrantedBaseQuantity { get; set; }
}

public sealed class App01aAuditStock
{
    public Guid StockLocationId { get; set; }
    public Guid SkuId { get; set; }
    public decimal RightGranted { get; set; }
    public decimal EffectGranted { get; set; }
}
