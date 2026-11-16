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

                SELECT g.Id,g.ApprovalId,p.CaseNumber,p.PolicyVersion,
                    p.ProposedBy,p.CheckedBy,p.Decision,p.EvidenceRef,
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
                """, new { Tenant = tenant, AsOf = asOf.UtcDateTime },
                tx, cancellationToken: ct));

            var commands = (await multi.ReadAsync<App01aAuditCommand>()).AsList();
            var sources = (await multi.ReadAsync<App01aAuditSource>()).AsList();
            var effects = (await multi.ReadAsync<App01aAuditEffect>()).AsList();
            var credits = (await multi.ReadAsync<App01aAuditCredit>()).AsList();
            var stock = (await multi.ReadAsync<App01aAuditStock>()).AsList();

            // Reconciliation issues are evidence, never silently repaired.
            var issues = new List<string>();
            var sourceIds = sources.Select(s => s.Id).ToHashSet();
            foreach (var c in commands.Where(c => c.Status == "APPLIED"))
                if (!sources.Any(s => s.BusinessCommandId == c.Id))
                    issues.Add($"APPLIED_WITHOUT_SOURCE:{c.Id:N}");
            foreach (var s in sources)
                if (s.LineCount < 1)
                    issues.Add($"SOURCE_WITHOUT_LINE:{s.Id:N}");
            foreach (var e in effects)
                if (!sourceIds.Contains(e.SourceId))
                    issues.Add($"EFFECT_WITHOUT_SOURCE:{e.Id:N}");
            foreach (var g in credits)
                if (g.ApprovalId is null || g.Decision != "APPROVED"
                    || string.IsNullOrWhiteSpace(g.EvidenceRef))
                    issues.Add($"CREDIT_WITHOUT_APPROVAL:{g.Id:N}");
            foreach (var bucket in stock)
                if (bucket.RightGranted != bucket.EffectGranted)
                    issues.Add($"STOCK_RIGHT_EFFECT_MISMATCH:{bucket.StockLocationId:N}:{bucket.SkuId:N}");

            await tx.CommitAsync(ct);
            return new App01aAuditSnapshot(tenant, asOf.ToUniversalTime(),
                commands, sources, effects, credits, stock,
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
    IReadOnlyList<App01aAuditEffect> Effects,
    IReadOnlyList<App01aAuditCredit> CreditGrants,
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
    public Guid? ApprovalId { get; set; }
    public string? CaseNumber { get; set; }
    public string? PolicyVersion { get; set; }
    public string? ProposedBy { get; set; }
    public string? CheckedBy { get; set; }
    public string? Decision { get; set; }
    public string? EvidenceRef { get; set; }
    public Guid TripId { get; set; }
    public Guid CustomerId { get; set; }
    public decimal GrantedAmount { get; set; }
    public Guid SourceId { get; set; }
}

public sealed class App01aAuditStock
{
    public Guid StockLocationId { get; set; }
    public Guid SkuId { get; set; }
    public decimal RightGranted { get; set; }
    public decimal EffectGranted { get; set; }
}
