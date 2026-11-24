using System.Globalization;
using System.Security.Claims;

namespace FmcgDms.Api.Persistence;

internal static class App02c5Routes
{
    public static void Map(WebApplication app, bool app03Workspace)
    {
        app.MapGet("/api/synthetic/app02c5/ar", async (string? asOf, Guid? customerId, Guid? orderId,
            Guid? deliveryId, DateOnly? due, bool? overdue, bool? download,
            HttpContext context, ClaimsPrincipal user, App01a2CommandService service, CancellationToken ct) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            DateTimeOffset? cutoff = null;
            if (asOf is not null)
            {
                string[] formats = ["yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'"];
                if (!DateTimeOffset.TryParseExact(asOf, formats, CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal, out var parsed) || parsed == DateTimeOffset.MinValue)
                    return Results.BadRequest(new { error = "UTC_CUTOFF_INVALID" });
                cutoff = parsed;
            }
            var report = await service.ReceivablesC5(user, cutoff, new(customerId, orderId, deliveryId, due, overdue), ct);
            if (report is null) return Results.BadRequest(new { error = "UTC_CUTOFF_IN_FUTURE" });
            if (download == true) context.Response.Headers.ContentDisposition = "attachment; filename=\"sales-a-ar.json\"";
            return Results.Ok(app03Workspace ? report with { Currency = "VND" } : report);
        }).RequireAuthorization("SyntheticSales");
    }
}
