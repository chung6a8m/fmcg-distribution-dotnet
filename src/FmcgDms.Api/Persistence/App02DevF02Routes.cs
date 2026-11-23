using System.Security.Claims;

namespace FmcgDms.Api.Persistence;

internal static class App02DevF02Routes
{
    public static void Map(WebApplication app)
    {
        var routes = app.MapGroup("/api/synthetic/dev-f02").RequireAuthorization();
        routes.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            return await next(context);
        });
        routes.MapGet("/session", async (ClaimsPrincipal actor, App02c1Service service, CancellationToken ct) =>
            Results.Ok(await service.AdvanceIdentity(actor, ct)));
        routes.MapGet("/advances", async (ClaimsPrincipal actor, App02c1Service service, CancellationToken ct) =>
            Results.Ok(await service.Advances(actor, ct)));
        routes.MapGet("/employees", async (ClaimsPrincipal actor, App02c1Service service, CancellationToken ct) =>
            Results.Ok(await service.AdvanceEmployees(actor, ct)));
        routes.MapGet("/advances/{id:guid}", async (Guid id, ClaimsPrincipal actor, App02c1Service service, CancellationToken ct) =>
            await service.AdvanceDetail(actor, id, ct) is { } detail ? Results.Ok(detail) : Results.NotFound());
        routes.MapGet("/commands/{id:guid}", async (Guid id, ClaimsPrincipal actor, App02c1Service service, CancellationToken ct) =>
            await service.AdvanceOutcome(actor, id, ct) is { } result ? Results.Ok(result) : Results.NotFound());
        routes.MapPost("/commands", async (F02Command input, ClaimsPrincipal actor, App02c1Service service, CancellationToken ct) =>
        {
            var result = await service.RecordAdvance(actor, input, ct);
            return Results.Json(result, statusCode: result.HttpStatus);
        });
    }
}
