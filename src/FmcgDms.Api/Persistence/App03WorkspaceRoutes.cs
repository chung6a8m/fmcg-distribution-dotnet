using System.Globalization;
using System.Security.Claims;

namespace FmcgDms.Api.Persistence;

internal static class App03WorkspaceRoutes
{
    internal static void Map(WebApplication app, string clock, bool field)
    {
        var routes = app.MapGroup("/api/synthetic/app03");
        routes.MapGet("/handoff", async (HttpContext context, App01a2CommandService service, CancellationToken ct) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return Results.Ok(await service.OrdersC2(context.User, ct, handoff: true));
        }).RequireAuthorization();
        routes.MapGet("/session", async (HttpContext context, App01a2CommandService service, CancellationToken ct) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            ClaimsPrincipal user = context.User;
            if (!long.TryParse(user.FindFirstValue("exp"), NumberStyles.None, CultureInfo.InvariantCulture, out long expiresAt)
                || expiresAt <= DateTimeOffset.UtcNow.ToUnixTimeSeconds())
                return Results.Unauthorized();

            var orderAccess = WhenGranted(() => service.IdentityC2(user, ct));
            var returnAccess = WhenGranted(() => service.ReturnIdentity(user, ct));
            await Task.WhenAll(orderAccess, returnAccess);
            var orders = await orderAccess;
            var returns = await returnAccess;
            if (orders is null && returns is null)
                return Results.Forbid();

            return Results.Ok(new
            {
                TenantId = orders?.TenantId ?? returns!.TenantId,
                ActorId = orders?.ActorId ?? returns!.ActorId,
                SessionId = orders?.SessionId ?? returns!.SessionId,
                Sales = orders?.Sales ?? false,
                Warehouse = orders?.Warehouse ?? false,
                FieldB = field,
                ReturnRole = returns?.Role,
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
