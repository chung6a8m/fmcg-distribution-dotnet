using System.Security.Claims;

namespace FmcgDms.Api.Persistence;

internal static class App02c2Routes
{
    public static void Map(WebApplication app)
    {
        var routes = app.MapGroup("/api/synthetic/app02c2").RequireAuthorization();
        routes.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            return await next(context);
        });
        routes.MapGet("/catalog", async (ClaimsPrincipal user, App01a2CommandService service, CancellationToken ct) =>
            Results.Ok(await service.CatalogC2(user, ct)));
        routes.MapGet("/session", async (ClaimsPrincipal user, App01a2CommandService service, CancellationToken ct) =>
            Results.Ok(await service.IdentityC2(user, ct)));
        routes.MapGet("/orders", async (ClaimsPrincipal user, App01a2CommandService service, CancellationToken ct) =>
            Results.Ok(await service.OrdersC2(user, ct)));
        routes.MapGet("/commands/{id:guid}", async (Guid id, ClaimsPrincipal user, App01a2CommandService service, CancellationToken ct) =>
            await service.OutcomeC2(user, id, ct) is { } result ? Results.Ok(result) : Results.NotFound());
        routes.MapPost("/drafts", async (C2DraftInput input, ClaimsPrincipal user, App01a2CommandService service, CancellationToken ct) =>
            Response(await service.DraftC2(user, input, ct))).RequireAuthorization("SyntheticSales");
        routes.MapPost("/confirm", async (C2ConfirmInput input, ClaimsPrincipal user, App01a2CommandService service, CancellationToken ct) =>
            Response(await service.ConfirmC2(user, input, ct))).RequireAuthorization("SyntheticSales");
        routes.MapPost("/reserve", async (C2ReserveInput input, ClaimsPrincipal user, App01a2CommandService service, CancellationToken ct) =>
            Response(await service.ReserveC2(user, input, ct))).RequireAuthorization("SyntheticWarehouse");
    }

    internal static IResult Response(BusinessCommandOutcome outcome) => outcome.Status switch
    {
        "APPLIED" => Results.Ok(outcome),
        "CONFLICT" => Results.Conflict(outcome),
        _ when outcome.ReasonCode is "INVALID_COMMAND_IDENTITY" or "INVALID_LOAD_SHAPE" => Results.BadRequest(outcome),
        _ => Results.UnprocessableEntity(outcome)
    };
}
