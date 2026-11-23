using System.Security.Claims;

namespace FmcgDms.Api.Persistence;

internal static class App02DevF06Routes
{
    public static void Map(WebApplication app)
    {
        var routes = app.MapGroup("/api/synthetic/dev-f06").RequireAuthorization();
        routes.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            return await next(context);
        });
        routes.MapGet("/session", async (ClaimsPrincipal actor, App01a2CommandService service, CancellationToken ct) =>
            Results.Ok(await service.AdjustmentIdentity(actor, ct)));
        routes.MapGet("/sources", async (ClaimsPrincipal actor, App01a2CommandService service, CancellationToken ct) =>
            Results.Ok(await service.AdjustmentSources(actor, ct)));
        routes.MapGet("/proposals", async (ClaimsPrincipal actor, App01a2CommandService service, CancellationToken ct) =>
            Results.Ok(await service.AdjustmentProposals(actor, ct)));
        routes.MapGet("/commands/{id:guid}", async (Guid id, ClaimsPrincipal actor, App01a2CommandService service, CancellationToken ct) =>
            await service.AdjustmentOutcome(actor, id, ct) is { } result ? Results.Ok(result) : Results.NotFound());
        routes.MapPost("/commands", async (F06Command input, ClaimsPrincipal actor, App01a2CommandService service, CancellationToken ct) =>
            App02c2Routes.Response(await service.RecordAdjustment(actor, input, ct)));
    }
}
