using System.Security.Claims;

namespace FmcgDms.Api.Persistence;

internal static class App02c1Routes
{
    public static void Map(WebApplication app)
    {
        var routes = app.MapGroup("/api/synthetic/app02c1").RequireAuthorization("SyntheticSales");
        routes.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            return await next(context);
        });
        routes.MapGet("/session", async (ClaimsPrincipal actor, App02c1Service service, CancellationToken ct) =>
            Results.Ok(await service.Identity(actor, ct)));
        routes.MapGet("/customers", async (ClaimsPrincipal actor, App02c1Service service, CancellationToken ct) =>
            Results.Ok(await service.Customers(actor, ct)));
        routes.MapGet("/customers/{id:guid}", async (Guid id, ClaimsPrincipal actor, App02c1Service service, CancellationToken ct) =>
            await service.Detail(actor, id, ct) is { } detail ? Results.Ok(detail) : Results.NotFound());
        routes.MapGet("/commands/{id:guid}", async (Guid id, ClaimsPrincipal actor, App02c1Service service, CancellationToken ct) =>
            await service.Outcome(actor, id, ct) is { } result ? Results.Ok(result) : Results.NotFound());
        routes.MapPost("/customers/save", async (C1CustomerInput input, ClaimsPrincipal actor, App02c1Service service, CancellationToken ct) =>
            ToHttp(await service.SaveCustomer(actor, input, ct)));
        routes.MapPost("/outlets/save", async (C1OutletInput input, ClaimsPrincipal actor, App02c1Service service, CancellationToken ct) =>
            ToHttp(await service.SaveOutlet(actor, input, ct)));
        routes.MapPost("/registered-ship-tos/save", async (C1RegisteredInput input, ClaimsPrincipal actor, App02c1Service service, CancellationToken ct) =>
            ToHttp(await service.SaveRegistered(actor, input, ct)));
        routes.MapPost("/ship-to-selections", async (C1SelectionInput input, ClaimsPrincipal actor, App02c1Service service, CancellationToken ct) =>
            ToHttp(await service.SelectAddress(actor, input, ct)));
    }

    private static IResult ToHttp(C1Outcome result) => Results.Json(result, statusCode: result.HttpStatus);
}
