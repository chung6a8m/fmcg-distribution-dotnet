using System.Security.Claims;

namespace FmcgDms.Api.Persistence;

internal static class App02DevF07Routes
{
    public static void Map(WebApplication app)
    {
        var routes = app.MapGroup("/api/synthetic/dev-f07").RequireAuthorization();
        routes.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            return await next(context);
        });
        routes.MapGet("/session", async (ClaimsPrincipal user, App01a2CommandService service, CancellationToken ct) =>
            Results.Ok(await service.GiftIdentity(user, ct)));
        routes.MapGet("/positions", async (ClaimsPrincipal user, App01a2CommandService service, CancellationToken ct) =>
            Results.Ok(await service.GiftPositions(user, ct)));
        routes.MapGet("/issues", async (ClaimsPrincipal user, App01a2CommandService service, CancellationToken ct) =>
            Results.Ok(await service.GiftIssues(user, ct)));
        routes.MapGet("/waivers", async (ClaimsPrincipal user, App01a2CommandService service, CancellationToken ct) =>
            Results.Ok(await service.GiftWaivers(user, ct)));
        routes.MapGet("/commands/{id:guid}", async (Guid id, ClaimsPrincipal user, App01a2CommandService service, CancellationToken ct) =>
            await service.GiftOutcome(user, id, ct) is { } result ? Results.Ok(result) : Results.NotFound());
        routes.MapPost("/commands", async (F07Command input, ClaimsPrincipal user, App01a2CommandService service, CancellationToken ct) =>
            App02c2Routes.Response(await service.RecordGift(user, input, ct)));
    }
}
