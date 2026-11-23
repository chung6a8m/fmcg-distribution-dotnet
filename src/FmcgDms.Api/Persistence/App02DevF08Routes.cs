using System.Security.Claims;

namespace FmcgDms.Api.Persistence;

internal static class App02DevF08Routes
{
    public static void Map(WebApplication app)
    {
        var routes = app.MapGroup("/api/synthetic/dev-f08").RequireAuthorization();
        routes.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            return await next(context);
        });
        routes.MapGet("/session", async (ClaimsPrincipal user, App01a2CommandService service, CancellationToken ct) =>
            Results.Ok(await service.RefundIdentity(user, ct)));
        routes.MapGet("/deliveries", async (ClaimsPrincipal user, App01a2CommandService service, CancellationToken ct) =>
            Results.Ok(await service.RefundDeliveries(user, ct)));
        routes.MapGet("/returns", async (ClaimsPrincipal user, App01a2CommandService service, CancellationToken ct) =>
            Results.Ok(await service.RefundReturns(user, ct)));
        routes.MapGet("/credits", async (ClaimsPrincipal user, App01a2CommandService service, CancellationToken ct) =>
            Results.Ok(await service.RefundCredits(user, ct)));
        routes.MapGet("/refunds", async (ClaimsPrincipal user, App01a2CommandService service, CancellationToken ct) =>
            Results.Ok(await service.RefundRequests(user, ct)));
        routes.MapGet("/commands/{id:guid}", async (Guid id, ClaimsPrincipal user, App01a2CommandService service, CancellationToken ct) =>
            await service.RefundOutcome(user, id, ct) is { } result ? Results.Ok(result) : Results.NotFound());
        routes.MapPost("/commands", async (F08Command input, ClaimsPrincipal user, App01a2CommandService service, CancellationToken ct) =>
            App02c2Routes.Response(await service.RecordRefund(user, input, ct)));
    }
}
