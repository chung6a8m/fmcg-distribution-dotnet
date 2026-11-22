using System.Security.Claims;

namespace FmcgDms.Api.Persistence;

internal static class App02d1LoadRoutes
{
    public static void Map(WebApplication app)
    {
        var routes = app.MapGroup("/api/synthetic/app02d1").RequireAuthorization();
        routes.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            return await next(context);
        });
        routes.MapGet("/catalog", async (ClaimsPrincipal user, App01a2CommandService service, CancellationToken ct) =>
            Results.Ok(await service.CatalogD1(user, ct)));

        var load = routes.MapGroup("").RequireAuthorization("SyntheticWarehouse");
        load.MapGet("/trips", async (ClaimsPrincipal user, App01a2CommandService service, CancellationToken ct) =>
            Results.Ok(await service.LoadTripsC3(user, ct)));
        load.MapGet("/trips/{tripId:guid}/load-rights",
            async (Guid tripId, ClaimsPrincipal user, App01a2CommandService service, CancellationToken ct) =>
                Results.Ok(await service.LoadRightsD1(user, tripId, ct)));
        load.MapGet("/trips/{tripId:guid}/commands/{commandId:guid}",
            async (Guid tripId, Guid commandId, ClaimsPrincipal user, App01a2CommandService service, CancellationToken ct) =>
                await service.OutcomeD1Load(user, tripId, commandId, ct) is { } outcome
                    ? Results.Ok(outcome) : Results.NotFound());
        load.MapPost("/load", async (C3LoadInput input, ClaimsPrincipal user, App01a2CommandService service,
            CancellationToken ct) => App02c2Routes.Response(await service.LoadD1(user, input, ct)));
    }
}
