using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Dapper;

namespace FmcgDms.Api.Persistence;

internal static partial class App02d4Accounting
{
    internal const string Partner = "accounting";
    private const string Mapping = "DELIVERY";
    private const string Received = "RECEIVED";
    private const string Posted = "POSTED";
    private const string Ok = "RECONCILED_OK";
    private const string Diff = "RECONCILED_DIFF";
    private const string Pending = "PENDING";
    private const string Unknown = "UNKNOWN_SCOPE";

    internal static void Map(RouteGroupBuilder group)
    {
        var routes = group.MapGroup("/accounting");
        routes.MapPost("/{deliveryId:guid}/receive", (Guid deliveryId, TransferRequest request, HttpContext http,
            App02d4Runtime runtime, CancellationToken ct) => Transfer(false, deliveryId, request, http, runtime, ct));
        routes.MapPost("/{deliveryId:guid}/post", (Guid deliveryId, TransferRequest request, HttpContext http,
            App02d4Runtime runtime, CancellationToken ct) => Transfer(true, deliveryId, request, http, runtime, ct));
        routes.MapPost("/reconcile", Reconcile);
        routes.MapGet("/reports/{reportId:guid}", GetReport);
        routes.MapPost("/reports/{reportId:guid}/corrections", Propose);
        routes.MapPost("/corrections/{id:guid}/approve", Approve);
        routes.MapPost("/cases/{caseId:guid}/close", CloseCase);
    }

    internal static void MapStub(RouteGroupBuilder routes)
    {
        routes.MapPost("/receive", (StubTransfer message, HttpContext http, App02d4Runtime runtime,
            CancellationToken ct) => StoreTransfer(false, message, http, runtime, ct));
        routes.MapPost("/post", (StubTransfer message, HttpContext http, App02d4Runtime runtime,
            CancellationToken ct) => StoreTransfer(true, message, http, runtime, ct));
        routes.MapPost("/snapshot", Snapshot);
        routes.MapPost("/correction", StoreCorrection);
    }

    private sealed record TransferRequest(string? TransportId, string? Fault, string? Scenario, string? ExpectedHash);
    private sealed record StubTransfer(Guid DeliveryId, Guid SourceLineId, string PayloadHash, string Period,
        decimal Quantity, decimal Gross, decimal Ar, decimal Cost, string TransportId, string? Fault, string? Scenario);
    private sealed record Ack(string Status, Guid Id, Guid SourceLineId);
    private sealed record Error(string Status);
    private sealed record SnapshotRequest(DateTimeOffset AsOf);
    private sealed record StubSnapshot(List<Receipt> Receipts, List<Posting> Postings);

    private static string Period(DateTimeOffset time) =>
        time.ToOffset(TimeSpan.FromHours(7)).ToString("yyyy-MM", CultureInfo.InvariantCulture);

    private static string PayloadHash(D4Source source)
    {
        var canonical = FormattableString.Invariant(
            $"{source.SourceLineId:D}|{source.DeliveryId:D}|{source.Hash}|{Period(source.OccurredAt)}|{source.Quantity:G29}|{source.Gross:G29}|{source.Ar:G29}|{source.Cost:G29}");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static async Task<IResult> Transfer(bool post, Guid deliveryId, TransferRequest request, HttpContext http,
        App02d4Runtime runtime, CancellationToken ct)
    {
        await using var db = await runtime.Open(http.User, "accountant", ct);
        if (string.IsNullOrWhiteSpace(request.TransportId) || request.TransportId.Length > 100 ||
            request.Fault is not (null or "before-commit" or "after-commit") ||
            request.Scenario is not (null or "wrong-amount" or "missing-line" or "wrong-mapping") ||
            (!post && request.Scenario is not null))
            return Results.BadRequest(new Error("INVALID_ACCOUNTING_REQUEST"));

        D4Source? source = await runtime.ReadSource(db, deliveryId, ct);
        if (source is null) return Results.NotFound(new Error("SOURCE_NOT_FOUND"));
        string hash = PayloadHash(source);
        if (request.ExpectedHash is not null &&
            !string.Equals(request.ExpectedHash, hash, StringComparison.OrdinalIgnoreCase))
            return Results.Conflict(new Error("PAYLOAD_CONFLICT"));

        var message = new StubTransfer(source.DeliveryId, source.SourceLineId, hash, Period(source.OccurredAt),
            source.Quantity, source.Gross, source.Ar, source.Cost, request.TransportId, request.Fault, request.Scenario);
        return await Forward(http, runtime, post ? "post" : "receive", message, ct);
    }

    private static async Task<IResult> Forward(HttpContext http, App02d4Runtime runtime, string operation,
        StubTransfer payload, CancellationToken ct)
    {
        using var client = runtime.Client(http, Partner);
        try
        {
            using var response = await client.PostAsJsonAsync(operation, payload, ct);
            if (response.StatusCode == HttpStatusCode.Conflict)
                return Results.Conflict(await response.Content.ReadFromJsonAsync<Error>(ct));
            if (response.StatusCode == HttpStatusCode.NotFound)
                return Results.NotFound(await response.Content.ReadFromJsonAsync<Error>(ct));
            if (!response.IsSuccessStatusCode)
                return Results.Json(new Error("ACCOUNTING_ACK_UNKNOWN"), statusCode: StatusCodes.Status503ServiceUnavailable);
            var ack = await response.Content.ReadFromJsonAsync<Ack>(ct);
            return ack is not null && ack.Id != Guid.Empty && ack.SourceLineId == payload.SourceLineId &&
                ack.Status == (operation == "post" ? Posted : Received)
                ? Results.Ok(ack) : Results.StatusCode(StatusCodes.Status502BadGateway);
        }
        catch (HttpRequestException)
        {
            return Results.Json(new Error("ACCOUNTING_ACK_UNKNOWN"), statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Results.Json(new Error("ACCOUNTING_ACK_UNKNOWN"), statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static string? Actor(ClaimsPrincipal user) =>
        user.FindFirstValue("actor_id") ?? user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.FindFirstValue("sub");

    internal sealed class Receipt
    {
        public Guid Id { get; init; }
        public Guid DeliveryId { get; init; }
        public Guid SourceLineId { get; init; }
        public string PayloadHash { get; init; } = "";
        public string Period { get; init; } = "";
        public decimal Quantity { get; init; }
        public decimal Gross { get; init; }
        public decimal Ar { get; init; }
        public decimal Cost { get; init; }
        public DateTimeOffset ReceivedAt { get; init; }
    }

    internal sealed class Posting
    {
        public Guid Id { get; init; }
        public Guid ReceiptId { get; init; }
        public Guid SourceLineId { get; init; }
        public string Kind { get; init; } = "";
        public Guid? OriginalId { get; init; }
        public Guid? CorrectionId { get; init; }
        public Guid? ReportId { get; init; }
        public Guid? ApprovalId { get; init; }
        public string Period { get; init; } = "";
        public decimal? Quantity { get; init; }
        public decimal? Gross { get; init; }
        public decimal? Ar { get; init; }
        public decimal? Cost { get; init; }
        public string? Mapping { get; init; }
        public string Scenario { get; init; } = "";
        public DateTimeOffset PostedAt { get; init; }
    }
}
