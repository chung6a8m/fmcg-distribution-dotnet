using System.Data;
using System.Security.Claims;
using Dapper;
using Microsoft.Data.SqlClient;

namespace FmcgDms.Api.Persistence;

internal static class App02d1GrantRoutes
{
    internal static void Map(WebApplication app, bool credit)
    {
        var routes = app.MapGroup("/api/synthetic/app02d1").RequireAuthorization();
        routes.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            return await next(context);
        });
        routes.MapPost("/quota", async (
            GrantBQuotaCommand input, ClaimsPrincipal user, App01a2CommandService service, CancellationToken ct) =>
            App02c2Routes.Response(await service.GrantD1Quota(user, input, ct)))
            .RequireAuthorization("SyntheticWarehouse");
        if (credit) routes.MapPost("/credit", async (
            GrantBCreditCommand input, ClaimsPrincipal user, App01a2CommandService service, CancellationToken ct) =>
            App02c2Routes.Response(await service.GrantD1Credit(user, input, ct)))
            .RequireAuthorization("SyntheticFinance");
        routes.MapGet("/trips/{tripId:guid}/grants/{commandId}", async (
            Guid tripId, string commandId, ClaimsPrincipal user, App01a2CommandService service, CancellationToken ct) =>
            await service.D1GrantOutcome(user, tripId, commandId, ct) is { } result
                ? Results.Ok(result) : Results.NotFound());
    }
}

public sealed partial class App01a2CommandService
{
    private static string D1Producer(ClaimsPrincipal principal) => "D1-" + C2Producer(principal);

    internal Task<BusinessCommandOutcome> GrantD1Quota(
        ClaimsPrincipal principal, GrantBQuotaCommand input, CancellationToken ct) =>
        input.TripId == Guid.Empty
            ? Task.FromResult(BusinessCommandOutcome.Rejected("INVALID_REFERENCE"))
            : GrantQuota(principal, input with { ProducerId = D1Producer(principal) }, d1: true, ct);

    internal Task<BusinessCommandOutcome> GrantD1Credit(
        ClaimsPrincipal principal, GrantBCreditCommand input, CancellationToken ct) =>
        input.TripId == Guid.Empty
            ? Task.FromResult(BusinessCommandOutcome.Rejected("INVALID_REFERENCE"))
            : GrantCredit(principal, input with { ProducerId = D1Producer(principal) }, d1: true, ct);

    private sealed record D1StoredOutcome(string Status, string OutcomeCode, string? ReasonCode, Guid? SourceId);

    internal async Task<BusinessCommandOutcome?> D1GrantOutcome(
        ClaimsPrincipal principal, Guid tripId, string commandId, CancellationToken ct)
    {
        bool warehouse = principal.HasClaim("synthetic_role", "warehouse");
        bool finance = principal.HasClaim("synthetic_role", "finance");
        if (!connections.UsesApp02d1 || (!warehouse && !finance))
            throw new UnauthorizedAccessException("D.1 grant recovery is unavailable for this actor.");
        if (tripId == Guid.Empty || !ValidKey(commandId, 128)) return null;
        string tenant = App02a3Sql.Tenant(principal);
        string actor = App02a3Sql.Actor(principal);
        await using var db = await connections.OpenApp02c2Async(principal, ct);
        await using var tx = (SqlTransaction)await db.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var result = await db.QuerySingleOrDefaultAsync<D1StoredOutcome>(Sql("""
            SELECT c.Status,c.OutcomeCode,c.ReasonCode,b.Id AS SourceId
            FROM dbo.BusinessCommand c
            JOIN dbo.C2CommandScope s ON s.TenantId=c.TenantId AND s.CommandRowId=c.Id
            JOIN dbo.App02ActorRoleGrant g WITH (HOLDLOCK)
                ON g.TenantId=c.TenantId AND g.ActorId=@Actor AND g.RoleCode=s.RoleCode AND g.IsActive=1
            LEFT JOIN dbo.BusinessSource b ON b.TenantId=c.TenantId AND b.BusinessCommandId=c.Id
            WHERE c.TenantId=@Tenant AND c.ProducerId=@Producer AND c.CommandId=@Command
                AND c.ActorId COLLATE Latin1_General_100_BIN2=@Actor AND c.Status IN ('APPLIED','REJECTED')
                AND s.D1GrantTripId=@Trip AND
                    ((@Finance=1 AND s.RoleCode=N'finance') OR
                     (@Warehouse=1 AND s.RoleCode=N'warehouse' AND EXISTS (
                         SELECT 1 FROM dbo.C3TripWriter WITH (HOLDLOCK)
                         WHERE TenantId=@Tenant AND TripId=@Trip AND ActorId=@Actor)))
            """, new { Tenant = tenant, Actor = actor, Producer = D1Producer(principal), Command = commandId,
                Trip = tripId, Warehouse = warehouse, Finance = finance }, tx, ct));
        await tx.CommitAsync(ct);
        return result is null
            ? null : new(result.Status, result.OutcomeCode, result.ReasonCode, true, result.SourceId);
    }
}
