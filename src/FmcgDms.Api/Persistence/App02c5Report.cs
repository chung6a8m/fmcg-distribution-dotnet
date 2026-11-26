using System.Data;
using System.Security.Claims;
using System.Text.Json.Serialization;
using Dapper;

namespace FmcgDms.Api.Persistence;

public sealed record C5ArSource(Guid CommandId, Guid OrderId, Guid OrderLineId, Guid CustomerId, string Customer,
    string OriginTrip, string Trip,
    string Owner, DateTimeOffset OccurredAtUtc, Guid? SourceId, Guid? DeliveryId, Guid? LoadSourceId,
    Guid? ConfirmSourceId, Guid? ShipToSnapshotId, int ShipToVersion, string? ShipTo,
    int? PolicyVersion, int? TermsDays, DateTime? BusinessDate, DateTime? DueBusinessDate,
    string Status, string? Reason, bool? Overdue,
    [property: JsonNumberHandling(JsonNumberHandling.WriteAsString)] decimal Quantity,
    [property: JsonNumberHandling(JsonNumberHandling.WriteAsString)] decimal? Amount,
    [property: JsonNumberHandling(JsonNumberHandling.WriteAsString)] decimal? Applied,
    [property: JsonNumberHandling(JsonNumberHandling.WriteAsString)] decimal? Balance,
    [property: JsonNumberHandling(JsonNumberHandling.WriteAsString)] decimal? Cost);

public sealed record C5ArCustomer(Guid CustomerId, string Customer, int Reconciled, int Expected,
    [property: JsonNumberHandling(JsonNumberHandling.WriteAsString)] decimal? KnownBalance,
    [property: JsonNumberHandling(JsonNumberHandling.WriteAsString)] decimal? KnownOverdue);

public sealed record C5ArFilter(Guid? CustomerId, Guid? OrderId, Guid? DeliveryId, DateOnly? Due, bool? Overdue,
    Guid? OrderLineId = null);

public sealed record C5ArReport(DateTime AsOf, C5ArFilter Filter,
    int Reconciled, int? Expected, IReadOnlyList<C5ArCustomer> Customers, IReadOnlyList<C5ArSource> Sources,
    [property: JsonNumberHandling(JsonNumberHandling.WriteAsString)] decimal? KnownBalance,
    [property: JsonNumberHandling(JsonNumberHandling.WriteAsString)] decimal? KnownOverdue)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Currency { get; init; }
}

public sealed partial class App01a2CommandService
{
    public Task<C5ArReport?> ReceivablesC5(ClaimsPrincipal principal, DateTimeOffset? requestedCutoff,
        C5ArFilter filter, CancellationToken ct) =>
        WithC4Authority<C5ArReport?>(principal, null, async (db, tx, tenant, actor) =>
        {
            var now = new DateTimeOffset(await db.QuerySingleAsync<DateTime>(Sql(
                $"SELECT {App02c6Clock.SqlUtcNow}", null, tx, ct)), TimeSpan.Zero);
            if (requestedCutoff > now) return null;
            var cutoff = requestedCutoff ?? now;
            var parameters = new DynamicParameters(new
            {
                Tenant = tenant, Actor = actor, filter.CustomerId, filter.OrderId, filter.DeliveryId, filter.OrderLineId
            });
            parameters.Add("Due", filter.Due?.ToDateTime(TimeOnly.MinValue), DbType.Date);
            parameters.Add("Overdue", filter.Overdue, DbType.Boolean);
            parameters.Add("AsOf", cutoff.UtcDateTime, DbType.DateTime2, scale: 7);
            var rows = (await db.QueryAsync<C5ArSource>(Sql(C5ReportSql(connections.UsesApp05Successor), parameters, tx, ct))).AsList();
            var customers = rows.GroupBy(row => (row.CustomerId, row.Customer)).Select(group =>
                new C5ArCustomer(group.Key.CustomerId, group.Key.Customer, group.Count(r => r.Balance.HasValue),
                    group.Count(), KnownSum(group, false), KnownSum(group, true))).ToArray();
            return new C5ArReport(cutoff.UtcDateTime, filter, rows.Count(r => r.Balance.HasValue),
                rows.Count == 0 ? null : rows.Count, customers, rows,
                KnownSum(rows, false), KnownSum(rows, true));
        }, ct);

    private static decimal? KnownSum(IEnumerable<C5ArSource> rows, bool overdueOnly)
    {
        decimal? sum = null;
        foreach (var row in rows)
            if (row.Balance is decimal balance)
                sum = sum.GetValueOrDefault() + (overdueOnly && row.Overdue != true ? 0 : balance);
        return sum;
    }

    private static string C5ReportSql(bool successor) => $"""
        SELECT e.ClientCommandId AS CommandId, o.Id AS OrderId, s.AOrderLineId AS OrderLineId, c.Id AS CustomerId,
            c.Code + N' · ' + c.Name AS Customer, origin.Code AS OriginTrip, t.Code AS Trip,
            e.ActorId AS Owner, TODATETIMEOFFSET(e.OccurredAtUtc,'+00:00') AS OccurredAtUtc, src.Id AS SourceId, d.Id AS DeliveryId,
            ml.LoadBusinessSourceId AS LoadSourceId, cf.ConfirmSourceId, d.ShipToSnapshotId,
            e.ShipToVersion, adr.Address AS ShipTo, p.PolicyVersion, s.TermsDaysSnapshot AS TermsDays,
            d.BusinessDate, ar.DueBusinessDate,
            CASE WHEN cmd.CompletedAtUtc IS NULL OR cmd.CompletedAtUtc>@AsOf THEN 'PENDING'
                WHEN cmd.Status='REJECTED' THEN 'REJECTED'
                WHEN reconciled.Ok=1 THEN 'RECONCILED' ELSE 'RECONCILIATION_REQUIRED' END AS Status,
            CASE WHEN cmd.CompletedAtUtc<=@AsOf THEN cmd.ReasonCode END AS Reason,
            aging.Overdue,
            e.Quantity,
            CASE WHEN reconciled.Ok=1 THEN ar.OriginalAmount END AS Amount,
            CASE WHEN reconciled.Ok=1 THEN alloc.Applied END AS Applied,
            CASE WHEN reconciled.Ok=1 THEN ar.OriginalAmount-alloc.Applied END AS Balance,
            CASE WHEN reconciled.Ok=1 THEN d.CostAmount END AS Cost
        FROM dbo.C4DeliveryEvidence e
        JOIN dbo.C4DeliveryWriter w ON w.TenantId=e.TenantId AND w.TripId=e.TripId AND w.ActorId=@Actor
        JOIN dbo.Trip t ON t.TenantId=e.TenantId AND t.Id=e.TripId
        JOIN dbo.TripManifestLine ml ON ml.TenantId=e.TenantId AND ml.Id=e.ManifestLineId
        JOIN dbo.StockRight r ON r.TenantId=ml.TenantId AND r.Id=ml.StockRightId
        JOIN dbo.AReservation rs ON rs.TenantId=r.TenantId AND rs.StockRightId=r.Id
        JOIN dbo.AConfirmedLineSnapshot s ON s.TenantId=rs.TenantId AND s.AOrderLineId=rs.AOrderLineId
        {(successor ? SuccessorExecutionRightJoin : "")}
        JOIN dbo.AOrder o ON o.TenantId=s.TenantId AND o.Id=s.AOrderId
            AND (o.TripId=e.TripId{(successor ? " OR (successorRight.BindingId IS NOT NULL AND successorRight.DestinationTripId=e.TripId)" : "")})
        JOIN dbo.Trip origin ON origin.TenantId=s.TenantId AND origin.Id=s.TripId
        JOIN dbo.Customer c ON c.TenantId=o.TenantId AND c.Id=o.CustomerId
        JOIN dbo.C2Confirmation cf ON cf.TenantId=o.TenantId AND cf.OrderId=o.Id
        JOIN dbo.C2LinePolicy p ON p.TenantId=s.TenantId AND p.OrderLineId=s.AOrderLineId
        JOIN dbo.BusinessCommand cmd ON cmd.TenantId=e.TenantId AND cmd.Id=e.Id
        LEFT JOIN dbo.BusinessSource src ON src.TenantId=e.TenantId AND src.BusinessCommandId=e.Id
            AND src.SourceKind='A_DELIVERY' AND src.RecordedAtUtc<=@AsOf
            AND cmd.Status='APPLIED' AND cmd.CompletedAtUtc<=@AsOf
        LEFT JOIN dbo.SaleDelivery d ON d.TenantId=e.TenantId AND d.C4EvidenceId=e.Id AND d.BusinessSourceId=src.Id
        LEFT JOIN dbo.ShipToSnapshot adr ON adr.TenantId=d.TenantId AND adr.Id=d.ShipToSnapshotId
        LEFT JOIN dbo.ReceivableOpenItem ar ON ar.TenantId=d.TenantId AND ar.SaleDeliveryId=d.Id
        LEFT JOIN dbo.DeliveryStockIssue issue ON issue.TenantId=d.TenantId AND issue.SaleDeliveryId=d.Id
        OUTER APPLY (
            SELECT CONVERT(decimal(19,4),COALESCE(SUM(CASE WHEN a.RecordedAtUtc<=@AsOf
                AND allocSource.RecordedAtUtc<=@AsOf AND allocCmd.CompletedAtUtc<=@AsOf
                AND allocLedger.RecordedAtUtc<=@AsOf THEN a.AppliedAmount END),0)) AS Applied,
                COALESCE(SUM(a.AppliedAmount),0) AS LifetimeApplied
            FROM dbo.ReceiptAllocation a
            JOIN dbo.BusinessSourceLine allocLine ON allocLine.TenantId=a.TenantId
                AND allocLine.Id=a.AllocationSourceLineId AND allocLine.Amount=a.AppliedAmount
            JOIN dbo.BusinessSource allocSource ON allocSource.TenantId=allocLine.TenantId
                AND allocSource.Id=allocLine.BusinessSourceId AND allocSource.CustomerId=a.CustomerId
            JOIN dbo.BusinessCommand allocCmd ON allocCmd.TenantId=allocSource.TenantId
                AND allocCmd.Id=allocSource.BusinessCommandId AND allocCmd.Status='APPLIED' AND allocCmd.CompletedAtUtc IS NOT NULL
            JOIN dbo.LedgerEffect allocLedger ON allocLedger.TenantId=allocLine.TenantId
                AND allocLedger.BusinessSourceLineId=allocLine.Id AND allocLedger.EffectKey='AR-CASH-ALLOC'
                AND allocLedger.CustomerId=a.CustomerId AND allocLedger.LedgerKind='AR'
                AND allocLedger.QuantityDelta=0 AND allocLedger.AmountDelta=-a.AppliedAmount
            WHERE a.TenantId=ar.TenantId AND a.ReceivableOpenItemId=ar.Id
        ) alloc
        CROSS APPLY (
            SELECT COUNT(*) AS MatchingEffects FROM dbo.LedgerEffect le
            WHERE le.TenantId=d.TenantId AND le.BusinessSourceLineId=d.BusinessSourceLineId AND le.RecordedAtUtc<=@AsOf
                AND ((le.EffectKey='AR-GROSS' AND le.AmountDelta=d.PayableAmount AND le.QuantityDelta=0)
                OR (le.EffectKey='COGS' AND le.AmountDelta=d.CostAmount AND le.QuantityDelta=0)
                OR (le.EffectKey='PHYSICAL-OUT' AND le.AmountDelta=-d.CostAmount AND le.QuantityDelta=-d.BaseQuantity)
                OR (le.EffectKey='RIGHT-A-USED' AND le.AmountDelta=0 AND le.QuantityDelta=-d.BaseQuantity))
        ) ledger
        CROSS APPLY (
            SELECT IIF(ar.DueBusinessDate=DATEADD(day,s.TermsDaysSnapshot,d.BusinessDate)
                AND alloc.LifetimeApplied=ar.AppliedAmount AND issue.BaseQuantity=d.BaseQuantity
                AND issue.CostAmount=d.CostAmount AND adr.Id IS NOT NULL AND ledger.MatchingEffects=4,1,0) AS Ok
        ) reconciled
        CROSS APPLY (
            SELECT CASE WHEN reconciled.Ok=1 THEN CONVERT(bit,IIF(ar.OriginalAmount>alloc.Applied
                AND ar.DueBusinessDate<CAST((@AsOf AT TIME ZONE 'UTC') AT TIME ZONE 'SE Asia Standard Time' AS date),1,0))
            END AS Overdue
        ) aging
        WHERE e.TenantId=@Tenant AND e.OccurredAtUtc<=@AsOf
            AND (@CustomerId IS NULL OR c.Id=@CustomerId) AND (@OrderId IS NULL OR o.Id=@OrderId)
            AND (@OrderLineId IS NULL OR s.AOrderLineId=@OrderLineId)
            AND (@DeliveryId IS NULL OR d.Id=@DeliveryId)
            AND (reconciled.Ok=0 OR ((@Due IS NULL OR ar.DueBusinessDate=@Due)
                AND (@Overdue IS NULL OR aging.Overdue=@Overdue)))
        ORDER BY c.Code,e.OccurredAtUtc,e.Id;
        """;
}
