using System.Security.Claims;

namespace FmcgDms.Api.Persistence;

internal static class App02c4Routes
{
    public static void Map(WebApplication app)
    {
        var routes = app.MapGroup("/api/synthetic/app02c4").RequireAuthorization("SyntheticSales");
        routes.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            return await next(context);
        });
        routes.MapGet("/trips", async (ClaimsPrincipal user, App01a2CommandService service, CancellationToken ct) =>
            Results.Ok(await service.DeliveryTripsC4(user, ct)));
        routes.MapGet("/trips/{trip:guid}/lines", async (Guid trip, ClaimsPrincipal user, App01a2CommandService service, CancellationToken ct) =>
            Results.Ok(await service.DeliveryLinesC4(user, trip, ct)));
        routes.MapGet("/trips/{trip:guid}/ar", async (Guid trip, ClaimsPrincipal user, App01a2CommandService service, CancellationToken ct) =>
            Results.Ok(await service.ReceivablesC4(user, trip, ct)));
        routes.MapGet("/trips/{trip:guid}/cases", async (Guid trip, ClaimsPrincipal user, App01a2CommandService service, CancellationToken ct) =>
            Results.Ok(await service.DeliveryCasesC4(user, trip, ct)));
        routes.MapGet("/trips/{trip:guid}/commands/{id:guid}",
            async (Guid trip, Guid id, ClaimsPrincipal user, App01a2CommandService service, CancellationToken ct) =>
                await service.DeliveryOutcomeC4(user, trip, id, ct) is { } outcome ? Results.Ok(outcome) : Results.NotFound());
        routes.MapPost("/deliver", async (C4DeliveryInput input, ClaimsPrincipal user, App01a2CommandService service, CancellationToken ct) =>
            App02c2Routes.Response(await service.DeliverC4(user, input, ct)));
        routes.MapPost("/amend-shipto", async (C4AmendInput input, ClaimsPrincipal user, App01a2CommandService service, CancellationToken ct) =>
            App02c2Routes.Response(await service.AmendShipToC4(user, input, ct)));
    }
}
