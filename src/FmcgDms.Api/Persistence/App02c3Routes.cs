using System.Security.Claims;

namespace FmcgDms.Api.Persistence;

internal static class App02c3Routes
{
    public static void Map(WebApplication app)
    {
        var routes = app.MapGroup("/api/synthetic/app02c3").RequireAuthorization("SyntheticWarehouse");
        routes.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            return await next(context);
        });
        routes.MapGet("/trips", async (ClaimsPrincipal user, App01a2CommandService service, CancellationToken ct) =>
            Results.Ok(await service.LoadTripsC3(user, ct)));
        routes.MapGet("/trips/{tripId:guid}/rights",
            async (Guid tripId, ClaimsPrincipal user, App01a2CommandService service, CancellationToken ct) =>
                Results.Ok(await service.LoadRightsC3(user, tripId, ct)));
        routes.MapGet("/trips/{tripId:guid}/commands/{commandId:guid}",
            async (Guid tripId, Guid commandId, ClaimsPrincipal user, App01a2CommandService service, CancellationToken ct) =>
                await service.OutcomeC3(user, tripId, commandId, ct) is { } outcome ? Results.Ok(outcome) : Results.NotFound());
        routes.MapPost("/load", async (C3LoadInput input, ClaimsPrincipal user, App01a2CommandService service, CancellationToken ct) =>
            App02c2Routes.Response(await service.LoadC3(user, input, ct)));
    }
}
