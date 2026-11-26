using System.Data;
using System.Security.Claims;
using System.Text.Json.Serialization;
using Dapper;
using Microsoft.Data.SqlClient;

namespace FmcgDms.Api.Persistence;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record D3UnloadInput(string ProducerId, string CommandId, string SourceNumber,
    Guid TripId) : IApp01aCommand;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record App03CashCount(string ProducerId, string CommandId, string SourceNumber,
    Guid TripId, Guid OfferId, decimal CountedAmount) : IApp01aCommand;

internal sealed record D3Session(string TenantId, string ActorId, string SessionId, string[] Roles);
internal sealed record D3TripOption(Guid TripId, string Label, Guid? PendingOfferId, decimal? PendingOfferAmount);
internal sealed record D3TripReport(Guid TripId, string Label, string TripStatus, decimal Custody, decimal Fund,
    decimal ArA, decimal ArB, decimal MainQty, decimal MainCost, decimal TripQty, decimal TripCost,
    decimal AHold, decimal BQuota, Guid? PendingOfferId, decimal PendingOfferAmount,
    decimal CustomerCredit, decimal Advance, decimal TotalAllocated, decimal SoldQty, decimal Cogs, DateTimeOffset AsOfUtc,
    bool CanOffer, bool CanAccept, bool CanUnload)
{
    public bool CanZeroHandoff { get; init; }
}

public sealed partial class App01dCashierUnloadService
{
    internal Task<BusinessCommandOutcome> OfferD3Async(ClaimsPrincipal principal,
        OfferCashHandoverCommand request, CancellationToken ct) =>
        ExecuteAsync(principal, request, "D3_CASH_HANDOVER_OFFER", "sales", request.TripId,
            [("TripId", request.TripId.ToString("N")),
             ("OfferedAmount", CommandPayloadHash.Decimal(request.OfferedAmount))],
            (db, tx, tenant, actor, command, input, token) => OfferCore(db, tx, tenant, actor, command, input, true, token),
            ct, d3: true);

    internal Task<BusinessCommandOutcome> AcceptD3Async(ClaimsPrincipal principal,
        AcceptCashHandoverCommand request, CancellationToken ct) =>
        ExecuteAsync(principal, request, "D3_CASH_HANDOVER_ACCEPT", "cashier", request.TripId,
            [("TripId", request.TripId.ToString("N")),
             ("OfferId", request.OfferId.ToString("N")),
             ("CountEvidenceRef", request.CountEvidenceRef ?? "")],
            (db, tx, tenant, actor, command, input, token) => AcceptCore(db, tx, tenant, actor, command, input, true, token),
            ct, d3: true);

    internal Task<BusinessCommandOutcome> AcceptApp03CashAsync(ClaimsPrincipal principal,
        App03CashCount request, CancellationToken ct) =>
        ExecuteAsync(principal, request, "APP03_CASH_HANDOVER_ACCEPT", "cashier", request.TripId,
            [("TripId", request.TripId.ToString("N")), ("OfferId", request.OfferId.ToString("N")),
             ("CountedAmount", CommandPayloadHash.Decimal(request.CountedAmount))],
            (db, tx, tenant, actor, command, input, token) => AcceptCore(db, tx, tenant, actor, command,
                new AcceptCashHandoverCommand(input.ProducerId, input.CommandId, input.SourceNumber,
                    input.TripId, input.OfferId, "APP03-COUNT-" + command.ToString("N")),
                true, token, input.CountedAmount), ct, d3: true);

    private Task<SqlConnection> OpenD3(ClaimsPrincipal user, CancellationToken ct)
    {
        if (!connections.UsesApp02d3)
            throw new UnauthorizedAccessException("Guarded D.3 runtime required.");
        return connections.OpenApp02c4Async(user, ct);
    }

    private const string D3TripAssignment = """
        EXISTS (SELECT 1 FROM dbo.D3CashierTrip d
            WHERE d.TenantId=t.TenantId AND d.TripId=t.Id)
        AND (
            ('sales' IN @Roles AND EXISTS (SELECT 1 FROM dbo.C4DeliveryWriter w
                WHERE w.TenantId=t.TenantId AND w.TripId=t.Id AND w.ActorId COLLATE Latin1_General_100_BIN2=@Actor))
            OR ('warehouse' IN @Roles AND EXISTS (SELECT 1 FROM dbo.C3TripWriter w
                WHERE w.TenantId=t.TenantId AND w.TripId=t.Id AND w.ActorId COLLATE Latin1_General_100_BIN2=@Actor))
            OR ('cashier' IN @Roles AND EXISTS (SELECT 1 FROM dbo.D3CashierTrip c
                WHERE c.TenantId=t.TenantId AND c.TripId=t.Id AND c.ActorId COLLATE Latin1_General_100_BIN2=@Actor))
        )
        """;

    private static async Task RequireD3Trip(SqlConnection db, SqlTransaction tx, string tenant,
        string actor, Guid tripId, string[] roles, CancellationToken ct)
    {
        if (await db.ExecuteScalarAsync<int>(Sql($"""
            SELECT COUNT(*) FROM dbo.Trip t WITH (HOLDLOCK)
            WHERE t.TenantId=@Tenant AND t.Id=@Trip AND {D3TripAssignment};
            """, new { Tenant = tenant, Trip = tripId, Actor = actor, Roles = roles }, tx, ct)) != 1)
            throw new UnauthorizedAccessException("Current D.3 trip assignment required.");
    }

    internal static Task<bool> HasPendingD3CashOffer(SqlConnection db, SqlTransaction tx, string tenant,
        Guid tripId, CancellationToken ct) =>
        db.ExecuteScalarAsync<bool>(Sql("""
            SELECT CAST(CASE WHEN EXISTS (
                SELECT 1 FROM dbo.CashHandoverOffer o WITH (UPDLOCK,HOLDLOCK)
                WHERE o.TenantId=@Tenant AND o.TripId=@Trip
                    AND NOT EXISTS (SELECT 1 FROM dbo.CashHandoverAcceptance a WITH (HOLDLOCK)
                        WHERE a.TenantId=o.TenantId AND a.OfferId=o.Id)
            ) THEN 1 ELSE 0 END AS bit);
            """, new { Tenant = tenant, Trip = tripId }, tx, ct));

    private async Task<TResult> ReadD3<TResult>(ClaimsPrincipal user,
        Func<SqlConnection, SqlTransaction, string, string, string[], Task<TResult>> read, CancellationToken ct)
    {
        string tenant = App02a3Sql.Tenant(user);
        string actor = App02a3Sql.Actor(user);
        string[] claimedRoles = ["sales", "warehouse", "cashier"];
        string[] requested = claimedRoles.Where(role => user.HasClaim("synthetic_role", role)).ToArray();
        if (requested.Length == 0)
            throw new UnauthorizedAccessException("D.3 role required.");
        await using var db = await OpenD3(user, ct);
        await using var tx = (SqlTransaction)await db.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        string[] active = (await db.QueryAsync<string>(Sql("""
            SELECT RoleCode FROM dbo.App02ActorRoleGrant WITH (HOLDLOCK)
            WHERE TenantId=@Tenant AND ActorId COLLATE Latin1_General_100_BIN2=@Actor
                AND RoleCode IN @Roles AND IsActive=1
            ORDER BY RoleCode;
            """, new { Tenant = tenant, Actor = actor, Roles = requested }, tx, ct))).ToArray();
        if (active.Length == 0)
            throw new UnauthorizedAccessException("Current D.3 role required.");
        TResult result = await read(db, tx, tenant, actor, active);
        await tx.CommitAsync(ct);
        return result;
    }

    internal Task<D3Session> D3Identity(ClaimsPrincipal user, CancellationToken ct) =>
        ReadD3(user, async (db, tx, tenant, actor, roles) =>
            new D3Session(tenant, actor, await db.QuerySingleAsync<string>(Sql(
                "SELECT Nonce FROM dbo.App00ScratchMarker", null, tx, ct)), roles), ct);

    internal Task<D3TripOption[]> D3Trips(ClaimsPrincipal user, CancellationToken ct) =>
        ReadD3(user, async (db, tx, tenant, actor, roles) =>
            (await db.QueryAsync<D3TripOption>(Sql($"""
                SELECT t.Id AS TripId,t.Code AS Label,
                    o.Id AS PendingOfferId,o.OfferedAmount AS PendingOfferAmount FROM dbo.Trip t
                OUTER APPLY (
                    SELECT TOP(1) h.Id,h.OfferedAmount FROM dbo.CashHandoverOffer h
                    WHERE h.TenantId=t.TenantId AND h.TripId=t.Id AND NOT EXISTS
                        (SELECT 1 FROM dbo.CashHandoverAcceptance a WHERE a.TenantId=h.TenantId AND a.OfferId=h.Id)
                    ORDER BY h.OfferedAtUtc,h.Id
                ) o
                WHERE t.TenantId=@Tenant AND {D3TripAssignment}
                ORDER BY CASE WHEN o.Id IS NULL THEN 1 ELSE 0 END,t.BusinessDate DESC,t.Code,t.Id;
                """, new { Tenant = tenant, Actor = actor, Roles = roles }, tx, ct))).ToArray(), ct);

    internal Task<D3TripReport> D3Report(ClaimsPrincipal user, Guid tripId, CancellationToken ct) =>
        ReadD3(user, async (db, tx, tenant, actor, roles) =>
        {
            await LockAsync(db, tx, "APP01C:TRIP:" + tenant + ":" + tripId.ToString("N"), ct);
            await RequireD3Trip(db, tx, tenant, actor, tripId, roles, ct);
            var report = await db.QuerySingleAsync<D3TripReport>(Sql($"""
                WITH TripPool AS (
                    SELECT DISTINCT TenantId,StockLocationId,SkuId
                    FROM dbo.StockRight WHERE TenantId=@Tenant AND TripId=@Trip
                ),
                TripSku AS (SELECT DISTINCT TenantId,SkuId FROM TripPool),
                TripEffect AS (
                    SELECT e.LedgerKind,e.AmountDelta FROM dbo.LedgerEffect e
                    JOIN dbo.BusinessSourceLine l ON l.TenantId=e.TenantId AND l.Id=e.BusinessSourceLineId
                    JOIN dbo.BusinessSource s ON s.TenantId=l.TenantId AND s.Id=l.BusinessSourceId
                    WHERE s.TenantId=@Tenant AND s.TripId=@Trip
                )
                SELECT t.Id AS TripId,t.Code AS Label,t.Status AS TripStatus,
                    COALESCE((SELECT SUM(AmountDelta) FROM TripEffect WHERE LedgerKind='CUSTODY'),0) AS Custody,
                    COALESCE((SELECT SUM(AmountDelta) FROM TripEffect WHERE LedgerKind='FUND'),0) AS Fund,
                    ar.ArA,ar.ArB,m.Qty AS MainQty,m.Cost AS MainCost,v.Qty AS TripQty,v.Cost AS TripCost,
                    r.AHold,r.BQuota,o.Id AS PendingOfferId,COALESCE(o.OfferedAmount,0) AS PendingOfferAmount,
                    COALESCE((SELECT SUM(AmountDelta) FROM TripEffect WHERE LedgerKind='CUSTOMER_CREDIT'),0) AS CustomerCredit,
                    COALESCE((SELECT SUM(AmountDelta) FROM TripEffect WHERE LedgerKind='ADVANCE'),0) AS Advance,
                    COALESCE((SELECT SUM(a.AppliedAmount) FROM dbo.ReceiptAllocation a
                        JOIN dbo.CashReceipt c ON c.TenantId=a.TenantId AND c.Id=a.CashReceiptId
                        WHERE c.TenantId=t.TenantId AND c.TripId=t.Id),0) AS TotalAllocated,
                    d.SoldQty,d.Cogs,TODATETIMEOFFSET({App02c6Clock.SqlUtcNow},'+00:00') AS AsOfUtc,
                    CONVERT(bit, CASE WHEN 'sales' IN @Roles AND EXISTS (SELECT 1 FROM dbo.C4DeliveryWriter w
                        WHERE w.TenantId=t.TenantId AND w.TripId=t.Id AND w.ActorId COLLATE Latin1_General_100_BIN2=@Actor)
                        THEN 1 ELSE 0 END) AS CanOffer,
                    CONVERT(bit, CASE WHEN 'cashier' IN @Roles AND EXISTS (SELECT 1 FROM dbo.D3CashierTrip c
                        WHERE c.TenantId=t.TenantId AND c.TripId=t.Id AND c.ActorId COLLATE Latin1_General_100_BIN2=@Actor)
                        THEN 1 ELSE 0 END) AS CanAccept,
                    CONVERT(bit, CASE WHEN 'warehouse' IN @Roles AND EXISTS (SELECT 1 FROM dbo.C3TripWriter w
                        WHERE w.TenantId=t.TenantId AND w.TripId=t.Id AND w.ActorId COLLATE Latin1_General_100_BIN2=@Actor)
                        THEN 1 ELSE 0 END) AS CanUnload
                FROM dbo.Trip t
                CROSS APPLY (
                    SELECT COALESCE(SUM(b.OnHandBaseQuantity),0) Qty,COALESCE(SUM(b.OnHandCostAmount),0) Cost
                    FROM dbo.PhysicalStockBalance b JOIN TripPool p ON p.TenantId=b.TenantId
                        AND p.StockLocationId=b.StockLocationId AND p.SkuId=b.SkuId
                ) m
                CROSS APPLY (
                    SELECT COALESCE(SUM(b.OnHandBaseQuantity),0) Qty,COALESCE(SUM(b.OnHandCostAmount),0) Cost
                    FROM dbo.PhysicalStockBalance b JOIN TripSku k ON k.TenantId=b.TenantId AND k.SkuId=b.SkuId
                    WHERE b.StockLocationId=t.VehicleLocationId
                ) v
                CROSS APPLY (
                    SELECT COALESCE(SUM(CASE WHEN s.RightKind='A' THEN i.OutstandingAmount ELSE 0 END),0) ArA,
                           COALESCE(SUM(CASE WHEN s.RightKind='B' THEN i.OutstandingAmount ELSE 0 END),0) ArB
                    FROM dbo.SaleDelivery s JOIN dbo.ReceivableOpenItem i ON i.TenantId=s.TenantId AND i.SaleDeliveryId=s.Id
                    WHERE s.TenantId=t.TenantId AND s.TripId=t.Id
                ) ar
                CROSS APPLY (
                    SELECT COALESCE(SUM(CASE WHEN RightKind='A' THEN GrantedBaseQuantity-ConsumedBaseQuantity-ReleasedBaseQuantity ELSE 0 END),0) AHold,
                           COALESCE(SUM(CASE WHEN RightKind='B' THEN GrantedBaseQuantity-ConsumedBaseQuantity-ReleasedBaseQuantity ELSE 0 END),0) BQuota
                    FROM dbo.StockRight WHERE TenantId=t.TenantId AND TripId=t.Id
                ) r
                CROSS APPLY (
                    SELECT COALESCE(SUM(BaseQuantity),0) SoldQty,COALESCE(SUM(CostAmount),0) Cogs
                    FROM dbo.SaleDelivery WHERE TenantId=t.TenantId AND TripId=t.Id
                ) d
                OUTER APPLY (
                    SELECT TOP (1) h.Id,h.OfferedAmount FROM dbo.CashHandoverOffer h
                    WHERE h.TenantId=t.TenantId AND h.TripId=t.Id
                        AND NOT EXISTS (SELECT 1 FROM dbo.CashHandoverAcceptance a WHERE a.TenantId=h.TenantId AND a.OfferId=h.Id)
                    ORDER BY h.OfferedAtUtc,h.Id
                ) o
                WHERE t.TenantId=@Tenant AND t.Id=@Trip;
                """, new { Tenant = tenant, Trip = tripId, Actor = actor, Roles = roles }, tx, ct));
            if (!connections.UsesApp03Settlement) return report;
            var stock = await db.QueryAsync<D3TripStock>(Sql("""
                SELECT SUM(e.QuantityDelta) AS TripQuantity,b.OnHandBaseQuantity AS VehicleQuantity,
                    b.OnHandCostAmount AS VehicleCost
                FROM dbo.Trip t
                JOIN dbo.BusinessSource s ON s.TenantId=t.TenantId AND s.TripId=t.Id
                JOIN dbo.BusinessSourceLine l ON l.TenantId=s.TenantId AND l.BusinessSourceId=s.Id
                JOIN dbo.LedgerEffect e ON e.TenantId=l.TenantId AND e.BusinessSourceLineId=l.Id
                    AND e.StockLocationId=t.VehicleLocationId AND e.LedgerKind='PHYSICAL_STOCK'
                JOIN dbo.PhysicalStockBalance b ON b.TenantId=t.TenantId
                    AND b.StockLocationId=t.VehicleLocationId AND b.SkuId=e.SkuId
                WHERE t.TenantId=@Tenant AND t.Id=@Trip
                GROUP BY b.SkuId,b.OnHandBaseQuantity,b.OnHandCostAmount;
                """, new { Tenant = tenant, Trip = tripId }, tx, ct));
            if (connections.UsesApp05TripClosure)
            {
                if (report.TripStatus is "CLOSED" or "CLOSED_WITH_OPEN_CASES")
                    report = report with { CanOffer = false, CanAccept = false, CanUnload = false };
                else if (report.CanUnload && report.SoldQty == 0 && report.Custody == 0 && report.PendingOfferId is null)
                    report = report with { CanZeroHandoff = await PlanZeroStockHandoff(db, tx, tenant, tripId, connections.UsesApp05Successor, ct) is not null };
            }
            return report with
            {
                TripQty = stock.Sum(row => row.TripQuantity),
                TripCost = stock.Sum(row => D3CarryingCost(row.VehicleCost, row.VehicleQuantity, row.TripQuantity))
            };
        }, ct);

    private sealed record D3TripStock(decimal TripQuantity, decimal VehicleQuantity, decimal VehicleCost);

    private sealed record D3StoredOutcome(string Status, string OutcomeCode, string? ReasonCode, Guid? SourceId,
        string RoleCode);

    internal Task<BusinessCommandOutcome?> D3Outcome(ClaimsPrincipal user, Guid tripId,
        string producerId, string commandId, CancellationToken ct) =>
        ReadD3(user, async (db, tx, tenant, actor, roles) =>
        {
            await RequireD3Trip(db, tx, tenant, actor, tripId, roles, ct);
            if (!ValidKey(producerId, 128) || !ValidKey(commandId, 128)) return null;
            var row = await db.QuerySingleOrDefaultAsync<D3StoredOutcome>(Sql("""
                SELECT c.Status,c.OutcomeCode,c.ReasonCode,s.Id AS SourceId,scope.RoleCode
                FROM dbo.BusinessCommand c
                JOIN dbo.D3CommandScope scope ON scope.TenantId=c.TenantId AND scope.CommandRowId=c.Id
                LEFT JOIN dbo.BusinessSource s ON s.TenantId=c.TenantId AND s.BusinessCommandId=c.Id
                WHERE c.TenantId=@Tenant AND scope.TripId=@Trip AND c.ProducerId=@Producer AND c.CommandId=@Command
                    AND c.ActorId COLLATE Latin1_General_100_BIN2=@Actor AND c.Status IN ('APPLIED','REJECTED');
                """, new { Tenant = tenant, Trip = tripId, Producer = producerId, Command = commandId, Actor = actor }, tx, ct));
            if (row is null || !roles.Contains(row.RoleCode, StringComparer.Ordinal))
                return null;
            await RequireD3Trip(db, tx, tenant, actor, tripId, [row.RoleCode], ct);
            return new BusinessCommandOutcome(row.Status, row.OutcomeCode, row.ReasonCode, true, row.SourceId);
        }, ct);
}
