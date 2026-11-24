using System.Security.Claims;

namespace FmcgDms.Api.Persistence;

internal static class App02d3Routes
{
    internal static void Map(WebApplication app, bool settlement)
    {
        var group = app.MapGroup("/api/synthetic/app02d3").RequireAuthorization();
        group.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            return await next(context);
        });
        group.MapGet("/session", (ClaimsPrincipal user, App01dCashierUnloadService service, CancellationToken ct) =>
            service.D3Identity(user, ct));
        group.MapGet("/trips", (ClaimsPrincipal user, App01dCashierUnloadService service, CancellationToken ct) =>
            service.D3Trips(user, ct));
        group.MapGet("/trips/{tripId:guid}", (Guid tripId, ClaimsPrincipal user,
            App01dCashierUnloadService service, CancellationToken ct) => service.D3Report(user, tripId, ct));
        group.MapGet("/trips/{tripId:guid}/commands/{producerId}/{commandId}", async (Guid tripId, string producerId,
            string commandId, ClaimsPrincipal user, App01dCashierUnloadService service, CancellationToken ct) =>
            await service.D3Outcome(user, tripId, producerId, commandId, ct) is { } outcome
                ? Results.Ok(outcome) : Results.NotFound());
        group.MapPost("/offer", async (OfferCashHandoverCommand input, ClaimsPrincipal user,
            App01dCashierUnloadService service, CancellationToken ct) =>
            CommandHttp(await service.OfferD3Async(user, input, ct)))
            .RequireAuthorization("SyntheticSales");
        if (settlement)
            group.MapPost("/accept", async (App03CashCount input, ClaimsPrincipal user,
                App01dCashierUnloadService service, CancellationToken ct) =>
                CommandHttp(await service.AcceptApp03CashAsync(user, input, ct)))
                .RequireAuthorization("SyntheticCashier");
        else group.MapPost("/accept", async (AcceptCashHandoverCommand input, ClaimsPrincipal user,
            App01dCashierUnloadService service, CancellationToken ct) =>
            CommandHttp(await service.AcceptD3Async(user, input, ct)))
            .RequireAuthorization("SyntheticCashier");
        group.MapPost("/unload", async (D3UnloadInput input, ClaimsPrincipal user,
            App01dCashierUnloadService service, CancellationToken ct) =>
            CommandHttp(await service.UnloadD3Async(user, input, ct)))
            .RequireAuthorization("SyntheticWarehouse");
    }

    private static IResult CommandHttp(BusinessCommandOutcome outcome) =>
        outcome.Status == "CONFLICT" ? Results.Conflict(outcome) : Results.Ok(outcome);
}
