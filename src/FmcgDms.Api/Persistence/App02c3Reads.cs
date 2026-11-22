using System.Data;
using System.Security.Claims;
using Dapper;
using Microsoft.Data.SqlClient;

namespace FmcgDms.Api.Persistence;

public sealed partial class App01a2CommandService
{
    private static async Task RequireC3Writer(SqlConnection db, SqlTransaction tx, string tenant,
        string actor, Guid trip, CancellationToken ct)
    {
        if (await db.ExecuteScalarAsync<int>(Sql("""
            SELECT COUNT(*) FROM dbo.C3TripWriter WITH (HOLDLOCK)
            WHERE TenantId=@Tenant AND TripId=@Trip AND ActorId=@Actor
            """, new { Tenant = tenant, Trip = trip, Actor = actor }, tx, ct)) != 1)
            throw new UnauthorizedAccessException("Current trip writer required.");
    }

    public Task<C3TripView[]> LoadTripsC3(ClaimsPrincipal principal, CancellationToken ct) =>
        ReadC3(principal, null, async (db, tx, tenant, actor) =>
            (await db.QueryAsync<C3TripView>(Sql("""
                SELECT t.Id,t.Code FROM dbo.C3TripWriter w
                JOIN dbo.Trip t ON t.TenantId=w.TenantId AND t.Id=w.TripId
                JOIN dbo.StockLocation v ON v.TenantId=t.TenantId AND v.Id=t.VehicleLocationId
                    AND v.Kind='VEHICLE' AND v.IsActive=1
                WHERE w.TenantId=@Tenant AND w.ActorId=@Actor AND t.Status='PREPARED'
                    AND NOT EXISTS(SELECT 1 FROM dbo.TripManifest m
                        WHERE m.TenantId=t.TenantId AND m.TripId=t.Id AND m.IsC3=0)
                ORDER BY t.Code
                """, new { Tenant = tenant, Actor = actor }, tx, ct))).ToArray(), ct);

    public Task<C3RightView[]> LoadRightsC3(ClaimsPrincipal principal, Guid trip, CancellationToken ct) =>
        ReadC3(principal, trip, async (db, tx, tenant, _) =>
        {
            if (!IsPreparedTrip(await FindTrip(db, tx, tenant, trip, ct))
                || await HasLegacyManifest(db, tx, tenant, trip, ct)) return [];
            return (await EligibleC3Rights(db, tx, tenant, trip, null, ct)).ToArray();
        }, ct);

    public Task<BusinessCommandOutcome?> OutcomeC3(ClaimsPrincipal principal, Guid trip, Guid id, CancellationToken ct) =>
        OutcomePartialLoad(principal, trip, id, false, ct);

    public Task<BusinessCommandOutcome?> OutcomeD1Load(ClaimsPrincipal principal, Guid trip, Guid id,
        CancellationToken ct)
    {
        RequireD1Mode();
        return OutcomePartialLoad(principal, trip, id, true, ct);
    }

    private Task<BusinessCommandOutcome?> OutcomePartialLoad(ClaimsPrincipal principal, Guid trip, Guid id,
        bool d1Load, CancellationToken ct) =>
        ReadC3<BusinessCommandOutcome?>(principal, trip, async (db, tx, tenant, actor) =>
        {
            var row = await db.QuerySingleOrDefaultAsync<ExistingCommand>(Sql("""
                SELECT c.Id,c.Status,c.OutcomeCode,c.ReasonCode FROM dbo.BusinessCommand c
                JOIN dbo.C2CommandScope s ON s.TenantId=c.TenantId AND s.CommandRowId=c.Id
                WHERE c.TenantId=@Tenant AND c.ProducerId=@Producer AND c.CommandId=@Command
                    AND c.ActorId COLLATE Latin1_General_100_BIN2=@Actor AND c.Status IN ('APPLIED','REJECTED')
                    AND s.LoadTripId=@Trip AND s.RoleCode='warehouse' AND s.IsD1Load=@D1Load
                """, new { Tenant = tenant, Producer = C2Producer(principal), Command = id.ToString("N"), Actor = actor,
                    Trip = trip, D1Load = d1Load }, tx, ct));
            if (row is null) return null;
            Guid? source = await db.QuerySingleOrDefaultAsync<Guid?>(Sql("""
                SELECT Id FROM dbo.BusinessSource WHERE TenantId=@Tenant AND BusinessCommandId=@Id
                """, new { Tenant = tenant, row.Id }, tx, ct));
            return new BusinessCommandOutcome(row.Status, row.OutcomeCode, row.ReasonCode, true, source);
        }, ct);

    private async Task<T> ReadC3<T>(ClaimsPrincipal principal, Guid? trip,
        Func<SqlConnection, SqlTransaction, string, string, Task<T>> read, CancellationToken ct)
    {
        string tenant = App02a3Sql.Tenant(principal);
        string actor = App02a3Sql.Actor(principal);
        if (!principal.HasClaim("synthetic_role", "warehouse"))
            throw new UnauthorizedAccessException("Warehouse role required.");
        await using var db = await connections.OpenApp02c3Async(principal, ct);
        await using var tx = (SqlTransaction)await db.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        await App02a3Sql.RequireRole(db, tx, tenant, actor, "warehouse", ct);
        if (trip is Guid tripId) await RequireC3Writer(db, tx, tenant, actor, tripId, ct);
        T result = await read(db, tx, tenant, actor);
        await tx.CommitAsync(ct);
        return result;
    }
}
