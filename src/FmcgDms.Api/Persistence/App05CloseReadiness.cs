using System.Data;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Dapper;
using FmcgDms.Api.Tenancy;
using Microsoft.Data.SqlClient;

namespace FmcgDms.Api.Persistence;

internal static partial class App05CloseReadiness
{
    private const string OpsRole = "tenant_ops";
    private const string AssigneeRole = "trip_assignee";
    private const string A = "A_REMAINDER";
    private const string D4 = "D4_RECONCILIATION";
    private const string D3 = "D3_SETTLEMENT";

    internal sealed record Trip(Guid TripId, string Code, DateTime BusinessDate, string Status, Guid? UnloadId, bool Used);
    internal sealed record ARow(Guid OrderId, Guid OrderLineId, Guid ConfirmSourceId, Guid SkuId, string SkuCode, string Unit,
        decimal Confirmed, decimal Granted, decimal Consumed, decimal Released, decimal Loaded, decimal Delivered,
        decimal Returned, bool Proven)
    {
        internal ALine ToView()
        {
            decimal onVehicle = Loaded - Delivered - Returned;
            return new(OrderId, OrderLineId, ConfirmSourceId, SkuId, SkuCode, Unit, Confirmed, Confirmed - Delivered,
                Granted - Consumed - Released - onVehicle, 0, Loaded, Delivered, 0, onVehicle);
        }
    }

    internal sealed record ALine(Guid OrderId, Guid OrderLineId, Guid ConfirmSourceId, Guid SkuId, string SkuCode, string Unit,
        decimal Confirmed, decimal Remaining, decimal Held, decimal Scheduled, decimal Loaded, decimal Delivered,
        decimal ClosedRemainder, decimal OnVehicle);
    internal sealed record FinanceRow(Guid DeliveryId, Guid SourceId, DateTimeOffset OccurredAt, Guid? ReceiptId,
        Guid? PostingId, string? ReconciliationStatus, bool ReconciliationStale, Guid? CaseId)
    {
        internal string? Pending(DateTimeOffset asOf)
        {
            if (CaseId.HasValue) return ReconciliationStatus is null or "RECONCILED_OK" ? "CASE_OPEN" : ReconciliationStatus;
            if (ReceiptId is null) return "AWAITING_RECEIPT";
            if (PostingId is null) return "AWAITING_POSTING";
            if (ReconciliationStale) return "REPORT_STALE";
            if (ReconciliationStatus == "RECONCILED_OK") return null;
            return ReconciliationStatus ?? (OccurredAt >= asOf ? "AWAITING_CUTOFF" : "AWAITING_RECONCILIATION");
        }
    }

    internal sealed record Receivable(Guid DeliveryId, Guid SourceId, decimal? Amount, DateTime? DueBusinessDate, string Unit, bool Proven);
    internal sealed record Blocker(string Code, string? SourceKind, Guid? SourceId, string Action, Guid? RelatedId = null);
    internal sealed record Source(string SourceKind, Guid SourceId, Guid? RelatedId, string Label, string Action, Guid? CaseId);
    internal sealed record Work(string SourceKind, Guid SourceId, Guid? RelatedId, string Label, string Action,
        Guid? CaseId, Assignment? FollowUp);
    internal sealed record Coverage(int Known, int Expected);
    internal sealed record Preview(Guid TripId, string Code, DateOnly BusinessDate, DateTimeOffset AsOf, string Status,
        string State, string Revision, Coverage Coverage, bool CanAssign, string[] Assignees,
        Blocker[] Blockers, ALine[] ALines, Receivable[] Receivables, Work[] Work);

    internal static void Map(WebApplication app)
    {
        var routes = app.MapGroup("/api/synthetic/app05/trip-close/trips").RequireAuthorization();
        routes.MapGet("", async (HttpContext context, TenantConnections connections, CancellationToken ct) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return Results.Ok(await Run(connections, context.User, null, false, null, async (db, tx, tenant, actor, _) =>
                (await db.QueryAsync<Trip>(App02a3Sql.Cmd(TripSql + "\n" + """
                    AND EXISTS(SELECT 1 FROM dbo.App05TripOperator scope
                        WHERE scope.TenantId=t.TenantId AND scope.TripId=t.Id AND scope.ActorId=@Actor AND scope.IsActive=1)
                    ORDER BY t.BusinessDate DESC,t.Code;
                    """, new { Tenant = tenant, Actor = actor }, tx, ct))).Select(trip =>
                    new { trip.TripId, trip.Code, BusinessDate = DateOnly.FromDateTime(trip.BusinessDate), Status = trip.Used ? trip.Status : "UNUSED" }),
                ct));
        });
        routes.MapGet("/{tripId:guid}", async (Guid tripId, HttpContext context, TenantConnections connections, CancellationToken ct) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return Results.Ok(await Run(connections, context.User, tripId, false, null,
                (db, tx, tenant, _, ops) => Read(db, tx, tenant, tripId, ops, ct), ct));
        });
        MapFollowUps(routes);
    }

    internal static async Task<bool> Session(TenantConnections connections, ClaimsPrincipal user, CancellationToken ct)
    {
        if (!connections.UsesApp05CloseReadiness || (!user.HasClaim("synthetic_role", OpsRole) && !user.HasClaim("synthetic_role", AssigneeRole)))
            return false;

        try
        {
            return await Run(connections, user, null, false, null, (db, tx, tenant, actor, _) =>
                db.ExecuteScalarAsync<bool>(App02a3Sql.Cmd("""
                    SELECT CONVERT(bit,CASE WHEN EXISTS(SELECT 1 FROM dbo.App05TripOperator
                        WHERE TenantId=@Tenant AND ActorId=@Actor AND IsActive=1) THEN 1 ELSE 0 END);
                    """, new { Tenant = tenant, Actor = actor }, tx, ct)), ct);
        }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static async Task<T> Run<T>(TenantConnections connections, ClaimsPrincipal user, Guid? trip, bool write, Guid? command,
        Func<SqlConnection, SqlTransaction, string, string, bool, Task<T>> operation, CancellationToken ct)
    {
        var ops = user.HasClaim("synthetic_role", OpsRole);
        if (!connections.UsesApp05CloseReadiness || (!ops && (write || !user.HasClaim("synthetic_role", AssigneeRole))))
            throw new UnauthorizedAccessException("Trip readiness requires its current scoped role.");

        string tenant = App02a3Sql.Tenant(user);
        string actor = App02a3Sql.Actor(user);
        await using var db = await connections.OpenApp02c4Async(user, ct);
        await using var tx = (SqlTransaction)await db.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        if (command.HasValue)
            await App02a3Sql.Lock(db, tx, App02a3Sql.IdentityLock(tenant, "APP05:" + actor, command.Value.ToString("N")), ct);
        if (trip.HasValue)
            await App02a3Sql.Lock(db, tx, "APP01C:TRIP:" + tenant + ":" + trip.Value.ToString("N"), ct);

        await App02a3Sql.RequireRole(db, tx, tenant, actor, ops ? OpsRole : AssigneeRole, ct);
        if (trip.HasValue && !await db.ExecuteScalarAsync<bool>(App02a3Sql.Cmd("""
            SELECT CONVERT(bit,CASE WHEN EXISTS(SELECT 1 FROM dbo.App05TripOperator
                WHERE TenantId=@Tenant AND TripId=@Trip AND ActorId=@Actor AND IsActive=1) THEN 1 ELSE 0 END);
            """, new { Tenant = tenant, Trip = trip, Actor = actor }, tx, ct)))
            throw new UnauthorizedAccessException("Current trip assignment required.");

        var result = await operation(db, tx, tenant, actor, ops);
        await tx.CommitAsync(ct);
        return result;
    }

    internal static string ReadinessState(bool used, int hard, int missing, int work) =>
        !used ? "UNUSED" : hard > 0 ? "BLOCKED" : missing > 0 ? "NEEDS_ASSIGNMENT" : work > 0 ? "READY_WITH_WORK" : "READY";

    private static byte[] Digest<T>(T value) => SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value));
}
