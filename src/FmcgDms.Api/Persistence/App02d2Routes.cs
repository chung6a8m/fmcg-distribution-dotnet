using System.Security.Claims;
using System.Text.Json.Serialization;

namespace FmcgDms.Api.Persistence;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record D2SaleInput(
    string ProducerId, string CommandId, string SourceNumber, Guid TripId, Guid CustomerId,
    Guid ShipToSnapshotId, Guid SelectionId, Guid? OutletId, Guid TripManifestLineId,
    Guid SkuId, decimal BaseQuantity, decimal ReceivedCashAmount, string? VerificationEvidenceRef,
    string? PendingBankEvidenceRef = null, Guid? CustomerCreditReservationId = null);

internal sealed record D2TripOption(Guid TripId, string Label);
internal sealed record D2LineOption(Guid TripManifestLineId, Guid SkuId, string SkuLabel,
    decimal AvailableQuantity, decimal UnitPrice);
internal sealed record D2SelectionOption(Guid SelectionId, Guid ShipToSnapshotId, Guid? OutletId, string Label);
internal sealed record D2CustomerOption(Guid CustomerId, string Label, D2SelectionOption[] Selections);
internal sealed record D2Workspace(Guid TripId, D2LineOption[] Lines, D2CustomerOption[] Customers);
internal sealed record D2SalePreview(decimal Payable, decimal CashReceived, decimal ReceiptApplied, decimal CreditApplied,
    decimal Receivable, decimal CreditIssued, decimal Cost, bool PendingBank);

internal static class App02d2Routes
{
    internal static void Map(WebApplication app)
    {
        var group = app.MapGroup("/api/synthetic/app02d2").RequireAuthorization("SyntheticSales");
        group.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            return await next(context);
        });
        group.MapGet("/trips", (ClaimsPrincipal user, App01c2BPostingService service, CancellationToken ct) =>
            service.ListD2Trips(user, ct));
        group.MapGet("/trips/{tripId:guid}", (Guid tripId, ClaimsPrincipal user,
            App01c2BPostingService service, CancellationToken ct) => service.GetD2Workspace(user, tripId, ct));
        group.MapPost("/preview", async (D2SaleInput input, ClaimsPrincipal user,
            App01c2BPostingService service, CancellationToken ct) =>
        {
            var (preview, outcome) = await service.PreviewD2Sale(user, input, ct);
            return preview is null ? Results.UnprocessableEntity(outcome) : Results.Ok(preview);
        });
        group.MapPost("/sales", async (D2SaleInput input, ClaimsPrincipal user,
            App01c2BPostingService service, CancellationToken ct) =>
        {
            var outcome = await service.PostD2Sale(user, input, ct);
            return outcome.Status == "CONFLICT" ? Results.Conflict(outcome) : Results.Ok(outcome);
        });
        group.MapGet("/sales/{producerId}/{commandId}", async (string producerId, string commandId, Guid tripId,
            ClaimsPrincipal user, App01c2BPostingService service, CancellationToken ct) =>
            await service.GetD2SaleOutcome(user, tripId, producerId, commandId, ct) is { } outcome
                ? Results.Ok(outcome) : Results.NotFound());
    }

    internal static void MapChannelBlocked(WebApplication app) =>
        app.MapPost("/api/synthetic/app02d2/sales", () => Results.Json(
            BusinessCommandOutcome.Rejected("B_NO_CHANNEL"), statusCode: StatusCodes.Status403Forbidden))
            .RequireAuthorization("SyntheticSales");
}
