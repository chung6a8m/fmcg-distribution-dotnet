using System.Data;
using System.Security.Claims;
using System.Text.Json.Serialization;
using Dapper;
using Microsoft.Data.SqlClient;

namespace FmcgDms.Api.Persistence;

public sealed record C4WorkspaceLine(Guid Id, Guid OrderId, string Sku, string BaseUom, string Customer, string ShipTo,
    int ShipToVersion,
    [property: JsonNumberHandling(JsonNumberHandling.WriteAsString | JsonNumberHandling.AllowReadingFromString)] decimal Loaded,
    [property: JsonNumberHandling(JsonNumberHandling.WriteAsString | JsonNumberHandling.AllowReadingFromString)] decimal Delivered,
    [property: JsonNumberHandling(JsonNumberHandling.WriteAsString | JsonNumberHandling.AllowReadingFromString)] decimal Available,
    [property: JsonNumberHandling(JsonNumberHandling.WriteAsString | JsonNumberHandling.AllowReadingFromString)] decimal Held)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<F03LotView>? Lots { get; init; }
}
public sealed record C4ArView(Guid SourceId, string Customer, string ShipTo, DateTime BusinessDate,
    DateTime DueBusinessDate,
    [property: JsonNumberHandling(JsonNumberHandling.WriteAsString | JsonNumberHandling.AllowReadingFromString)] decimal Quantity,
    [property: JsonNumberHandling(JsonNumberHandling.WriteAsString | JsonNumberHandling.AllowReadingFromString)] decimal Amount,
    [property: JsonNumberHandling(JsonNumberHandling.WriteAsString | JsonNumberHandling.AllowReadingFromString)] decimal Outstanding,
    [property: JsonNumberHandling(JsonNumberHandling.WriteAsString | JsonNumberHandling.AllowReadingFromString)] decimal Cost)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Currency { get; init; }
}
public sealed record C4CaseView(Guid CommandId, Guid ManifestLineId, int ShipToVersion, DateTime OccurredAtUtc,
    [property: JsonNumberHandling(JsonNumberHandling.WriteAsString | JsonNumberHandling.AllowReadingFromString)] decimal Quantity,
    string? ReasonCode, bool CanRetry);
public sealed record C4TripView(Guid Id, string Code,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTime? BusinessDate);

public sealed partial class App01a2CommandService
{
    private static async Task RequireC4Authority(ClaimsPrincipal principal, SqlConnection db, SqlTransaction tx,
        string tenant, string actor, Guid trip, bool amendment, CancellationToken ct)
    {
        if (amendment)
        {
            if (!principal.HasClaim("synthetic_role", "case_checker"))
                throw new UnauthorizedAccessException("Current checker required for ShipTo amendment.");
            await App02a3Sql.RequireRole(db, tx, tenant, actor, "case_checker", ct);
            return;
        }
        if (await db.ExecuteScalarAsync<int>(Sql("""
            SELECT COUNT(*) FROM dbo.C4DeliveryWriter WITH (HOLDLOCK)
            WHERE TenantId=@Tenant AND TripId=@Trip AND ActorId=@Actor
            """, new { Tenant = tenant, Trip = trip, Actor = actor }, tx, ct)) != 1)
            throw new UnauthorizedAccessException("Current delivery writer required.");
    }

    private async Task<T> WithC4Authority<T>(ClaimsPrincipal principal, Guid? trip,
        Func<SqlConnection, SqlTransaction, string, string, Task<T>> read, CancellationToken ct)
    {
        string tenant = App02a3Sql.Tenant(principal);
        string actor = App02a3Sql.Actor(principal);
        if (!principal.HasClaim("synthetic_role", "sales"))
            throw new UnauthorizedAccessException("Current Sales role required.");
        await using var db = await connections.OpenApp02c4Async(principal, ct);
        await using var tx = (SqlTransaction)await db.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        await App02a3Sql.RequireRole(db, tx, tenant, actor, "sales", ct);
        if (trip is Guid tripId) await RequireC4Authority(principal, db, tx, tenant, actor, tripId, false, ct);
        T result = await read(db, tx, tenant, actor);
        await tx.CommitAsync(ct);
        return result;
    }

    public Task<IEnumerable<C4TripView>> DeliveryTripsC4(ClaimsPrincipal principal, CancellationToken ct) =>
        WithC4Authority(principal, null, (db, tx, tenant, actor) => db.QueryAsync<C4TripView>(Sql("""
            SELECT t.Id,t.Code,CASE WHEN @Field=1 THEN t.BusinessDate END AS BusinessDate FROM dbo.C4DeliveryWriter w
            JOIN dbo.Trip t ON t.TenantId=w.TenantId AND t.Id=w.TripId
            WHERE w.TenantId=@Tenant AND w.ActorId=@Actor ORDER BY t.Code
            """, new { Tenant = tenant, Actor = actor, Field = connections.UsesApp03Field }, tx, ct)), ct);

    public Task<IEnumerable<C4WorkspaceLine>> DeliveryLinesC4(ClaimsPrincipal principal, Guid trip, CancellationToken ct) =>
        WithC4Authority<IEnumerable<C4WorkspaceLine>>(principal, trip, async (db, tx, tenant, _) =>
        {
            var lines = (await db.QueryAsync<C4WorkspaceLine>(Sql("""
            SELECT l.Id,o.Id AS OrderId,k.Code AS Sku,p.BaseUom,c.Name AS Customer,adr.Address AS ShipTo,
                COALESCE(am.Version,1) AS ShipToVersion,l.LoadedBaseQuantity AS Loaded,d.Delivered,
                CASE WHEN @Settlement=1 AND t.Status NOT IN ('PREPARED','IN_PROGRESS') THEN 0
                    ELSE l.LoadedBaseQuantity-d.Delivered END AS Available,
                r.GrantedBaseQuantity-r.ConsumedBaseQuantity-r.ReleasedBaseQuantity AS Held
            FROM dbo.TripManifestLine l
            JOIN dbo.Trip t ON t.TenantId=l.TenantId AND t.Id=l.TripId
            JOIN dbo.PhysicalStockMovement mv ON mv.TenantId=l.TenantId AND mv.TripManifestLineId=l.Id
            JOIN dbo.StockRight r ON r.TenantId=l.TenantId AND r.Id=l.StockRightId
            JOIN dbo.AReservation a ON a.TenantId=r.TenantId AND a.StockRightId=r.Id
            JOIN dbo.AConfirmedLineSnapshot s ON s.TenantId=a.TenantId AND s.AOrderLineId=a.AOrderLineId
            JOIN dbo.C2LinePolicy p ON p.TenantId=s.TenantId AND p.OrderLineId=s.AOrderLineId
            JOIN dbo.AOrder o ON o.TenantId=s.TenantId AND o.Id=s.AOrderId AND o.TripId=l.TripId
            JOIN dbo.C2Confirmation cf ON cf.TenantId=o.TenantId AND cf.OrderId=o.Id
            JOIN dbo.Customer c ON c.TenantId=o.TenantId AND c.Id=o.CustomerId
            JOIN dbo.Sku k ON k.TenantId=l.TenantId AND k.Id=l.SkuId
            OUTER APPLY (SELECT TOP(1) Version,ShipToSnapshotId FROM dbo.C4ShipToAmendment
                WHERE TenantId=o.TenantId AND OrderId=o.Id ORDER BY Version DESC) am
            JOIN dbo.ShipToSnapshot adr ON adr.TenantId=o.TenantId AND adr.Id=COALESCE(am.ShipToSnapshotId,cf.ShipToSnapshotId)
            CROSS APPLY (SELECT CONVERT(decimal(18,3),COALESCE(SUM(BaseQuantity),0)) AS Delivered FROM dbo.SaleDelivery
                WHERE TenantId=l.TenantId AND TripManifestLineId=l.Id) d
            WHERE l.TenantId=@Tenant AND l.TripId=@Trip AND l.IsC3=1
            ORDER BY c.Name,k.Code,l.Id
            """, new { Tenant = tenant, Trip = trip, Settlement = connections.UsesApp03Settlement }, tx, ct))).AsList();
            if (!connections.UsesDevF03 || lines.Count == 0) return lines;
            var ids = lines.Select(line => line.Id).ToArray();
            var lots = (await ReadLoadedLots(db, tx, tenant, ids, null, ct)).ToLookup(lot => lot.SourceId, lot => lot.ToView());
            var tracked = await GetTrackedSources(db, tx, tenant, ids, "F03LotLoad", "ManifestLineId", ct);
            return lines.Select(line => tracked.Contains(line.Id)
                ? line with { Lots = line.Available == 0 && connections.UsesApp03Settlement ? [] : lots[line.Id].ToArray(), Available = Math.Min(line.Available, lots[line.Id].Sum(lot => lot.Quantity)) }
                : line).ToArray();
        }, ct);

    public Task<IEnumerable<C4ArView>> ReceivablesC4(ClaimsPrincipal principal, Guid trip, CancellationToken ct) =>
        WithC4Authority<IEnumerable<C4ArView>>(principal, trip, async (db, tx, tenant, _) =>
        {
            var amounts = (await db.QueryAsync<C4ArView>(Sql("""
            SELECT d.BusinessSourceId AS SourceId,c.Name AS Customer,s.Address AS ShipTo,d.BusinessDate,
                ar.DueBusinessDate,d.BaseQuantity AS Quantity,ar.OriginalAmount AS Amount,
                ar.OutstandingAmount AS Outstanding,d.CostAmount AS Cost
            FROM dbo.SaleDelivery d
            JOIN dbo.ReceivableOpenItem ar ON ar.TenantId=d.TenantId AND ar.SaleDeliveryId=d.Id
            JOIN dbo.Customer c ON c.TenantId=d.TenantId AND c.Id=d.CustomerId
            JOIN dbo.ShipToSnapshot s ON s.TenantId=d.TenantId AND s.Id=d.ShipToSnapshotId
            WHERE d.TenantId=@Tenant AND d.TripId=@Trip AND d.C4EvidenceId IS NOT NULL
            ORDER BY d.PostedAtUtc DESC,d.Id
            """, new { Tenant = tenant, Trip = trip }, tx, ct))).AsList();
            if (!connections.UsesDevF04 || amounts.Count == 0) return amounts;
            var currencies = (await ReadDeliveryCurrencies(db, tx, tenant, trip, ct))
                .ToDictionary(row => row.SourceId, row => row.Currency);
            return amounts.Select(amount => amount with { Currency = currencies.GetValueOrDefault(amount.SourceId) }).ToArray();
        }, ct);

    public Task<IEnumerable<C4CaseView>> DeliveryCasesC4(ClaimsPrincipal principal, Guid trip, CancellationToken ct) =>
        WithC4Authority(principal, trip, (db, tx, tenant, actor) => db.QueryAsync<C4CaseView>(Sql("""
            SELECT e.ClientCommandId AS CommandId,e.ManifestLineId,e.ShipToVersion,e.OccurredAtUtc,e.Quantity,b.ReasonCode,
                CONVERT(bit,IIF(e.ActorId=@Actor AND b.Status='RECEIVED',1,0)) AS CanRetry
            FROM dbo.C4DeliveryEvidence e
            JOIN dbo.BusinessCommand b ON b.TenantId=e.TenantId AND b.Id=e.Id
            WHERE e.TenantId=@Tenant AND e.TripId=@Trip
                AND NOT EXISTS(SELECT 1 FROM dbo.SaleDelivery d WHERE d.TenantId=e.TenantId AND d.C4EvidenceId=e.Id)
            ORDER BY e.OccurredAtUtc,e.Id
            """, new { Tenant = tenant, Trip = trip, Actor = actor }, tx, ct)), ct);

    public Task<BusinessCommandOutcome?> DeliveryOutcomeC4(ClaimsPrincipal principal, Guid trip, Guid id, CancellationToken ct) =>
        WithC4Authority(principal, trip, (db, tx, tenant, actor) => db.QuerySingleOrDefaultAsync<BusinessCommandOutcome>(Sql("""
            SELECT b.Status,b.OutcomeCode,b.ReasonCode,CONVERT(bit,1) AS Replayed,s.Id AS SourceId
            FROM dbo.C4DeliveryEvidence e
            JOIN dbo.BusinessCommand b ON b.TenantId=e.TenantId AND b.Id=e.Id
            JOIN dbo.C2CommandScope cs ON cs.TenantId=b.TenantId AND cs.CommandRowId=b.Id AND cs.DeliveryTripId=e.TripId
            LEFT JOIN dbo.BusinessSource s ON s.TenantId=b.TenantId AND s.BusinessCommandId=b.Id AND s.SourceKind='A_DELIVERY'
            WHERE e.TenantId=@Tenant AND e.TripId=@Trip AND e.ActorId=@Actor AND e.ClientCommandId=@Id
                AND (b.Status='REJECTED' OR s.Id IS NOT NULL)
            """, new { Tenant = tenant, Trip = trip, Actor = actor, Id = id }, tx, ct)), ct);
}
