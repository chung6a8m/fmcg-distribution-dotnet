using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using FmcgDms.Api.Persistence;
using Microsoft.Data.SqlClient;

namespace FmcgDms.Demo;

internal static partial class App03FieldEvidence
{
    private const string B = "/api/synthetic/app02d2";
    private const string BStock = "/api/synthetic/app02d1";
    private static readonly string[] BusinessEffects =
    [
        "BusinessSource", "BusinessSourceLine", "LedgerEffect", "StockPool", "StockRight", "BQuota",
        "TripManifest", "TripManifestLine", "PhysicalStockBalance", "PhysicalStockMovement",
        "SaleDelivery", "DeliveryStockIssue", "ReceivableOpenItem", "CashReceipt", "ReceiptAllocation",
        "App03BSalePricing"
    ];

    internal static async Task<Recovery> CheckRecovery(DemoRun run, App03DevHost host, CancellationToken ct)
    {
        await run.Verify("tenant-a", app03Session: true);
        await using var db = new SqlConnection(run.Connection("tenant-a"));
        var original = await db.QuerySingleAsync<Original>(new CommandDefinition("""
            SELECT s.Id AS SourceId,s.SourceNumber,c.CommandId,c.ProducerId,d.TripId,d.CustomerId,
                d.ShipToSnapshotId,d.D2SelectionId AS SelectionId,d.TripManifestLineId,d.SkuId,
                d.BaseQuantity,r.ReceivedAmount AS ReceivedCashAmount,r.VerificationEvidenceRef,
                d.StockRightId,h.StockLocationId
            FROM dbo.App03BSalePricing p
            JOIN dbo.SaleDelivery d ON d.TenantId=p.TenantId AND d.Id=p.SaleDeliveryId
            JOIN dbo.BusinessSource s ON s.TenantId=d.TenantId AND s.Id=d.BusinessSourceId
            JOIN dbo.BusinessCommand c ON c.TenantId=s.TenantId AND c.Id=s.BusinessCommandId
            JOIN dbo.CashReceipt r ON r.TenantId=d.TenantId AND r.SaleDeliveryId=d.Id
            JOIN dbo.StockRight h ON h.TenantId=d.TenantId AND h.Id=d.StockRightId
            WHERE d.TenantId='tenant-a' AND d.BaseQuantity=2;
            """, cancellationToken: ct));
        using var response = await Send(host, B + $"/sales/{original.ProducerId}/{original.CommandId}?tripId={original.TripId}", ct);
        using var found = await response.Content.ReadFromJsonAsync<JsonDocument>(ct);
        var request = new
        {
            original.SourceNumber, original.CommandId, original.ProducerId, original.TripId, original.CustomerId,
            original.ShipToSnapshotId, original.SelectionId, original.TripManifestLineId, original.SkuId, original.BaseQuantity,
            original.ReceivedCashAmount, original.VerificationEvidenceRef
        };
        using var replay = await Send(host, B + "/sales", ct, request);
        using var recovered = await replay.Content.ReadFromJsonAsync<JsonDocument>(ct);
        using var oldTrip = await Send(host, B + "/trips/" + original.TripId, ct);
        using var history = await oldTrip.Content.ReadFromJsonAsync<JsonDocument>(ct);
        using var stale = await Send(host, B + "/preview", ct, request with { CommandId = Guid.NewGuid().ToString() });
        using var rejected = await stale.Content.ReadFromJsonAsync<JsonDocument>(ct);
        using var trips = await Send(host, B + "/trips", ct);
        using var list = await trips.Content.ReadFromJsonAsync<JsonDocument>(ct);
        var before = await DevF01Evidence.Capture(run, BusinessEffects);
        string saleId = Guid.NewGuid().ToString();
        string quotaId = Guid.NewGuid().ToString("N");
        Guid loadId = Guid.NewGuid();
        var saleReason = await Reject(B + "/sales", request with { CommandId = saleId, SourceNumber = "STALE-" + saleId },
            HttpStatusCode.OK);
        var quotaReason = await Reject(BStock + "/quota", new GrantBQuotaCommand("warehouse", quotaId, "STALE-" + quotaId,
            original.TripId, original.StockLocationId, original.SkuId, 1), HttpStatusCode.UnprocessableEntity, host.ReceiverToken);
        var loadReason = await Reject(BStock + "/load", new C3LoadInput(loadId, original.TripId,
            [new(original.StockRightId, 1, 1)]), HttpStatusCode.UnprocessableEntity, host.ReceiverToken);
        var after = await DevF01Evidence.Capture(run, BusinessEffects);
        if (!response.IsSuccessStatusCode || found?.RootElement.GetProperty("sourceId").GetGuid() != original.SourceId
            || !replay.IsSuccessStatusCode || recovered?.RootElement.GetProperty("sourceId").GetGuid() != original.SourceId
            || !recovered.RootElement.GetProperty("replayed").GetBoolean()
            || history?.RootElement.GetProperty("currentDay").GetBoolean() != false
            || history.RootElement.GetProperty("lines").GetArrayLength() != 0
            || history.RootElement.GetProperty("recentSales")[0].GetProperty("sourceId").GetGuid() != original.SourceId
            || stale.StatusCode != HttpStatusCode.UnprocessableEntity
            || rejected?.RootElement.GetProperty("reasonCode").GetString() != "APP03_B_TRIP_DAY_CLOSED"
            || list?.RootElement[0].GetProperty("label").GetString() != "DEV-20261124-A"
            || !before.SequenceEqual(after))
            throw new InvalidOperationException("A B source changed on next-day resume, or a closed-day B write was offered.");
        // Fixture-only status change after checking the date gate on a still-prepared trip.
        if (await db.ExecuteAsync(new CommandDefinition("""
            UPDATE dbo.Trip SET Status='CLOSED'
            WHERE TenantId='tenant-a' AND Id=@TripId AND Status='PREPARED' AND BusinessDate='20261123';
            """, new { original.TripId }, cancellationToken: ct)) != 1)
            throw new InvalidOperationException("Could not prepare the closed-trip history fixture.");
        var closedList = await Read<JsonElement>(host, B + "/trips", ct);
        var closedTrip = closedList.EnumerateArray().Single(trip => trip.GetProperty("tripId").GetGuid() == original.TripId);
        var closedHistory = await Read<JsonElement>(host, B + "/trips/" + original.TripId, ct);
        if (closedTrip.GetProperty("canSell").GetBoolean()
            || closedHistory.GetProperty("lines").GetArrayLength() != 0
            || closedHistory.GetProperty("recentSales")[0].GetProperty("sourceId").GetGuid() != original.SourceId)
            throw new InvalidOperationException("The closed trip lost its B history or still offers new sales.");
        return new(original.SourceId, original.CommandId, "2026-11-24", saleReason, quotaReason, loadReason,
            closedTrip.GetProperty("label").GetString()!, before, after);

        async Task<string> Reject(string path, object input, HttpStatusCode status, string? token = null)
        {
            using var rejectedWrite = await Send(host, path, ct, input, token);
            var result = await rejectedWrite.Content.ReadFromJsonAsync<BusinessCommandOutcome>(cancellationToken: ct);
            if (rejectedWrite.StatusCode != status
                || result is not { Status: "REJECTED", ReasonCode: "APP03_B_TRIP_DAY_CLOSED", SourceId: null })
                throw new InvalidOperationException($"Unexpected stale write result from {path}: {rejectedWrite.StatusCode}, {result}.");
            return result.ReasonCode;
        }
    }

    private static async Task<HttpResponseMessage> Send(App03DevHost host, string path, CancellationToken ct,
        object? input = null, string? token = null)
    {
        using var request = new HttpRequestMessage(input is null ? HttpMethod.Get : HttpMethod.Post, host.WebUrl + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token ?? host.SourceToken);
        if (input is not null) request.Content = JsonContent.Create(input);
        return await host.Client.SendAsync(request, ct);
    }

    internal static async Task Review(DemoRun run, string directory, string source, Recovery recovery,
        UpgradeReview upgrade, CancellationToken ct)
    {
        var primary = await Read("tenant-a");
        var peer = await Read("tenant-b");
        if (primary != new Counts(2, 2, 5, 2, 2, 5, 40000, 30000, 1, 0, 3, 1, 1, 0, 1)
            || peer != new Counts(0, 0, 0, 0, 0, 0, 0, 0, 10, 0, 0, 0, 0, 0, 0))
            throw new InvalidOperationException($"APP-03.5 SQL totals differ: tenant-a={primary}, tenant-b={peer}.");
        await using var db = new SqlConnection(run.Connection("tenant-a"));
        var rows = (await db.QueryAsync<Source>(new CommandDefinition("""
            SELECT t.Code AS Trip,d.RightKind AS Kind,s.Id AS SourceId,d.BaseQuantity AS Quantity,
                d.PayableAmount AS Payable,d.CostAmount AS Cost,d.InvoiceEligible,i.OutstandingAmount,
                r.ConsumedBaseQuantity AS Consumed,r.GrantedBaseQuantity AS Granted,
                p.Currency,p.UnitPrice,p.TaxAmount,p.PlanId,p.QuotePolicyId
            FROM dbo.SaleDelivery d
            JOIN dbo.Trip t ON t.TenantId=d.TenantId AND t.Id=d.TripId
            JOIN dbo.StockRight r ON r.TenantId=d.TenantId AND r.Id=d.StockRightId
            JOIN dbo.BusinessSource s ON s.TenantId=d.TenantId AND s.Id=d.BusinessSourceId
            JOIN dbo.DeliveryStockIssue x ON x.TenantId=d.TenantId AND x.SaleDeliveryId=d.Id AND x.BaseQuantity=d.BaseQuantity
            JOIN dbo.ReceivableOpenItem i ON i.TenantId=d.TenantId AND i.SaleDeliveryId=d.Id
            JOIN dbo.Sku k ON k.TenantId=d.TenantId AND k.Id=d.SkuId AND k.Code='Z-FIELD-EA'
            LEFT JOIN dbo.App03BSalePricing p ON p.TenantId=d.TenantId AND p.SaleDeliveryId=d.Id
            WHERE d.TenantId=N'tenant-a'
            ORDER BY t.Code,d.RightKind,s.Id;
            """, cancellationToken: ct))).ToArray();
        if (rows.Length != 4 || rows.Select(row => row.SourceId).Distinct().Count() != 4
            || rows.Any(row => row.InvoiceEligible || row.Payable != row.Quantity * 10000 || row.Cost != row.Quantity * 4000)
            || rows.Take(2).Any(row => row is not
                { Trip: "DEV-20261123-A", Kind: "A", Quantity: 1, OutstandingAmount: 10000, Consumed: 2, Granted: 4 })
            || rows[2] is not { Trip: "DEV-20261123-A", Kind: "B", Quantity: 2, Consumed: 2, Granted: 3 }
            || rows[3] is not { Trip: "DEV-20261123-B", Kind: "B", Quantity: 1, Consumed: 1, Granted: 2 }
            || rows.Skip(2).Any(row => row is not
                { Currency: "VND", UnitPrice: 10000, TaxAmount: 0, OutstandingAmount: 0, PlanId: not null, QuotePolicyId: not null }))
            throw new InvalidOperationException("APP-03.5 source, quota, issue, VND or AR lineage differs.");
        await File.WriteAllTextAsync(Path.Combine(directory, "sql-review.json"),
            JsonSerializer.Serialize(new { source, runId = run.Id, primary, peer, rows, upgrade, recovery },
                new JsonSerializerOptions { WriteIndented = true }), ct);

        async Task<Counts> Read(string tenant)
        {
            await run.Verify(tenant, app03Session: true);
            await using var connection = new SqlConnection(run.Connection(tenant));
            return await connection.QuerySingleAsync<Counts>(new CommandDefinition("""
                SELECT
                    (SELECT COUNT(*) FROM dbo.C2OrderDraft WHERE TenantId=@Tenant) AS Drafts,
                    (SELECT COUNT(*) FROM dbo.AOrder WHERE TenantId=@Tenant) AS Orders,
                    (SELECT COUNT(*) FROM dbo.SaleDelivery WHERE TenantId=@Tenant) AS Deliveries,
                    (SELECT COUNT(*) FROM dbo.CashReceipt WHERE TenantId=@Tenant) AS Receipts,
                    (SELECT COUNT(*) FROM dbo.ReceiptAllocation WHERE TenantId=@Tenant) AS Allocations,
                    (SELECT COUNT(*) FROM dbo.ReceivableOpenItem WHERE TenantId=@Tenant) AS OpenItems,
                    (SELECT COALESCE(SUM(OutstandingAmount),0) FROM dbo.ReceivableOpenItem WHERE TenantId=@Tenant) AS Outstanding,
                    (SELECT COALESCE(SUM(AmountDelta),0) FROM dbo.LedgerEffect WHERE TenantId=@Tenant AND LedgerKind='CUSTODY') AS Custody,
                    (SELECT p.OnHandBaseQuantity FROM dbo.StockPool p JOIN dbo.Sku k ON k.TenantId=p.TenantId AND k.Id=p.SkuId
                        WHERE p.TenantId=@Tenant AND k.Code='Z-FIELD-EA') AS MainQuantity,
                    (SELECT p.AllocatedBaseQuantity FROM dbo.StockPool p JOIN dbo.Sku k ON k.TenantId=p.TenantId AND k.Id=p.SkuId
                        WHERE p.TenantId=@Tenant AND k.Code='Z-FIELD-EA') AS MainAllocated,
                    (SELECT b.OnHandBaseQuantity FROM dbo.PhysicalStockBalance b
                        JOIN dbo.Sku k ON k.TenantId=b.TenantId AND k.Id=b.SkuId
                        JOIN dbo.StockLocation l ON l.TenantId=b.TenantId AND l.Id=b.StockLocationId
                        WHERE b.TenantId=@Tenant AND k.Code='Z-FIELD-EA' AND l.Code='DEV-TRUCK-A') AS TruckA,
                    (SELECT b.OnHandBaseQuantity FROM dbo.PhysicalStockBalance b
                        JOIN dbo.Sku k ON k.TenantId=b.TenantId AND k.Id=b.SkuId
                        JOIN dbo.StockLocation l ON l.TenantId=b.TenantId AND l.Id=b.StockLocationId
                        WHERE b.TenantId=@Tenant AND k.Code='Z-FIELD-EA' AND l.Code='DEV-TRUCK-B') AS TruckB,
                    (SELECT COUNT(*) FROM dbo.F03LotIssue WHERE TenantId=@Tenant) AS LotIssues,
                    (SELECT COUNT(*) FROM dbo.BusinessSource s JOIN dbo.BusinessCommand c ON c.TenantId=s.TenantId
                        AND c.Id=s.BusinessCommandId WHERE s.TenantId=@Tenant AND c.Status='REJECTED') AS RejectedSources,
                    (SELECT COUNT(*) FROM dbo.F01Visit WHERE TenantId=@Tenant AND Outcome='NO_ORDER') AS NoOrderVisits;
                """, new { Tenant = tenant }, cancellationToken: ct));
        }
    }

    private sealed record Counts(int Drafts, int Orders, int Deliveries, int Receipts, int Allocations, int OpenItems,
        decimal Outstanding, decimal Custody, decimal MainQuantity, decimal MainAllocated, decimal TruckA, decimal TruckB,
        int LotIssues, int RejectedSources, int NoOrderVisits);

    private sealed record Source(string Trip, string Kind, Guid SourceId, decimal Quantity, decimal Payable, decimal Cost,
        bool InvoiceEligible, decimal OutstandingAmount, decimal Consumed, decimal Granted,
        string? Currency, decimal? UnitPrice, decimal? TaxAmount, Guid? PlanId, Guid? QuotePolicyId);

    private sealed record Original(Guid SourceId, string SourceNumber, string CommandId, string ProducerId, Guid TripId,
        Guid CustomerId, Guid ShipToSnapshotId, Guid SelectionId, Guid TripManifestLineId, Guid SkuId,
        decimal BaseQuantity, decimal ReceivedCashAmount, string VerificationEvidenceRef, Guid StockRightId, Guid StockLocationId);

    internal sealed record Recovery(Guid SourceId, string CommandId, string ResumedDate,
        string SaleRejection, string QuotaRejection, string LoadRejection, string ClosedTripWithHistory,
        DevF01Evidence.TableState[] BeforeStaleWrites, DevF01Evidence.TableState[] AfterStaleWrites);
}
