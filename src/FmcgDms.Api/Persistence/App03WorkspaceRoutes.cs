using System.Globalization;
using System.Security.Claims;

namespace FmcgDms.Api.Persistence;

internal static class App03WorkspaceRoutes
{
    internal static void Map(WebApplication app, string clock, bool field, bool settlement, bool finance)
    {
        var routes = app.MapGroup("/api/synthetic/app03");
        if (finance)
            routes.MapGet("/owner-overview", async (HttpContext context, Tenancy.TenantConnections connections, CancellationToken ct) =>
            {
                context.Response.Headers.CacheControl = "no-store";
                return Results.Ok(await App03OwnerReport.Read(connections, context.User, ct));
            }).RequireAuthorization();
        routes.MapGet("/handoff", async (HttpContext context, App01a2CommandService service, CancellationToken ct) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return Results.Ok(await service.OrdersC2(context.User, ct, handoff: true));
        }).RequireAuthorization();
        routes.MapGet("/session", async (HttpContext context, App01a2CommandService service,
            App01dCashierUnloadService handoffService, App02c1Service advancesService, App02d4Runtime accountingService,
            Tenancy.TenantConnections connections, CancellationToken ct) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            ClaimsPrincipal user = context.User;
            if (!long.TryParse(user.FindFirstValue("exp"), NumberStyles.None, CultureInfo.InvariantCulture, out long expiresAt)
                || expiresAt <= DateTimeOffset.UtcNow.ToUnixTimeSeconds())
                return Results.Unauthorized();

            var orderAccess = WhenGranted(() => service.IdentityC2(user, ct));
            var returnAccess = WhenGranted(() => service.ReturnIdentity(user, ct));
            var handoffAccess = settlement ? WhenGranted(() => handoffService.D3Identity(user, ct)) : Task.FromResult<D3Session?>(null);
            var advanceAccess = settlement ? WhenGranted(() => advancesService.AdvanceIdentity(user, ct)) : Task.FromResult<F02Session?>(null);
            var giftAccess = settlement ? WhenGranted(() => service.GiftIdentity(user, ct)) : Task.FromResult<F05Identity?>(null);
            var adjustmentAccess = finance ? WhenGranted(() => service.AdjustmentIdentity(user, ct)) : Task.FromResult<F05Identity?>(null);
            var refundAccess = finance ? WhenGranted(() => service.RefundIdentity(user, ct)) : Task.FromResult<F05Identity?>(null);
            var accountingAccess = finance ? WhenGranted(() => accountingService.FinanceRoles(user, ct)) : Task.FromResult<string[]?>(null);
            var ownerAccess = finance && user.HasClaim("synthetic_role", "owner")
                ? WhenGranted(() => App03OwnerReport.Session(connections, user, ct)) : Task.FromResult<string?>(null);
            var closeReadinessAccess = App05CloseReadiness.Session(connections, user, ct);
            await Task.WhenAll(closeReadinessAccess, orderAccess, returnAccess, handoffAccess, advanceAccess, giftAccess, adjustmentAccess, refundAccess, accountingAccess, ownerAccess);
            var orders = await orderAccess;
            var returns = await returnAccess;
            var handoff = await handoffAccess;
            var advance = await advanceAccess;
            var gifts = await giftAccess;
            var adjustment = await adjustmentAccess;
            var refund = await refundAccess;
            var accounting = await accountingAccess;
            var owner = await ownerAccess;
            if (orders is null && returns is null && handoff is null && advance is null && gifts is null
                && adjustment is null && refund is null && accounting is null && owner is null && !await closeReadinessAccess)
                return Results.Forbid();

            return Results.Ok(new
            {
                TenantId = App02a3Sql.Tenant(user),
                ActorId = App02a3Sql.Actor(user),
                SessionId = orders?.SessionId ?? returns?.SessionId ?? handoff?.SessionId ?? advance?.SessionId ?? gifts?.SessionId
                    ?? adjustment?.SessionId ?? refund?.SessionId ?? owner ?? app.Configuration["App02D4:Nonce"],
                Sales = orders?.Sales ?? false,
                Warehouse = orders?.Warehouse ?? false,
                FieldB = field,
                HandoffRoles = handoff?.Roles ?? [],
                AdvanceRole = advance?.Role,
                GiftRole = gifts?.Role,
                ReturnRole = returns?.Role,
                AdjustmentRole = adjustment?.Role,
                RefundRole = refund?.Role,
                AccountingRoles = accounting ?? [],
                Owner = owner is not null,
                TripClose = await closeReadinessAccess,
                TripClosure = connections.UsesApp05TripClosure && await closeReadinessAccess,
                ASuccessor = connections.UsesApp05Successor && await closeReadinessAccess,
                ASuccessorReserve = connections.UsesApp05Successor && orders?.Warehouse == true,
                BusinessDate = DateOnly.FromDateTime(App02c6Clock.Read(clock).AddHours(7)),
                ExpiresAt = expiresAt
            });
        }).RequireAuthorization();
    }

    private static async Task<T?> WhenGranted<T>(Func<Task<T>> read) where T : class
    {
        try { return await read(); }
        catch (UnauthorizedAccessException) { return null; }
    }
}
