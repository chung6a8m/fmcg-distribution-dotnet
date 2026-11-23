using System.Security.Claims;

namespace FmcgDms.Api.Persistence;

internal static class App02DevF05Routes
{
    public static void Map(WebApplication app)
    {
        var routes = app.MapGroup("/api/synthetic/dev-f05").RequireAuthorization();
        routes.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            return await next(context);
        });
        routes.MapGet("/session", async (ClaimsPrincipal actor, App01a2CommandService service, CancellationToken ct) =>
            Results.Ok(await service.ReturnIdentity(actor, ct)));
        routes.MapGet("/deliveries", async (ClaimsPrincipal actor, App01a2CommandService service, CancellationToken ct) =>
            Results.Ok(await service.ReturnableDeliveries(actor, ct)));
        routes.MapGet("/cases", async (ClaimsPrincipal actor, App01a2CommandService service, CancellationToken ct) =>
            Results.Ok(await service.ReadReturnCases(actor, ct)));
        routes.MapGet("/commands/{id:guid}", async (Guid id, ClaimsPrincipal actor, App01a2CommandService service, CancellationToken ct) =>
            await service.ReturnOutcome(actor, id, ct) is { } result ? Results.Ok(result) : Results.NotFound());
        routes.MapPost("/commands", async (F05Command input, ClaimsPrincipal actor, App01a2CommandService service, CancellationToken ct) =>
            App02c2Routes.Response(await service.RecordReturn(actor, input, ct)));
    }
}
