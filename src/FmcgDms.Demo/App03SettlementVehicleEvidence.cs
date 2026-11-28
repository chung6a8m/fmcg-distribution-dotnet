using System.Text.Json;
using Dapper;
using FmcgDms.Api.Persistence;
using Microsoft.Data.SqlClient;

namespace FmcgDms.Demo;

internal static partial class App03SettlementEvidence
{
    private const string D1 = "/api/synthetic/app02d1";

    internal static async Task<Guid> PrepareB(App03DevHost host, string label, bool load, CancellationToken ct,
        string output, string source, string runId)
    {
        var catalog = await App03SettlementHttpWitness.Observe(output, source, runId, label,
            host.ApiPid, host.VitePid, host.Client.Timeout, ct, witnessId =>
                Send<C2Catalog>(host, ct, App03SettlementHttpWitness.Route, host.SourceToken, witnessId: witnessId));
        Guid trip = catalog.Trips.Single(option => option.Label == label).Id;
        Guid sku = catalog.Skus.Single(option => option.Label.StartsWith("Z-FIELD-EA ·", StringComparison.Ordinal)).Id;
        Guid warehouse = catalog.Warehouses.Single(option => option.Label == "C2-MAIN").Id;
        string id = Guid.NewGuid().ToString("N");
        AssertApplied(await Send<BusinessCommandOutcome>(host, ct, D1 + "/quota", host.ReceiverToken,
            new GrantBQuotaCommand("warehouse", id, id, trip, warehouse, sku, 1)));
        if (load)
        {
            var right = (await Send<C3RightView[]>(host, ct, D1 + $"/trips/{trip}/load-rights", host.ReceiverToken)).Single();
            AssertApplied(await Send<BusinessCommandOutcome>(host, ct, D1 + "/load", host.ReceiverToken,
                new C3LoadInput(Guid.NewGuid(), trip, [new(right.Id, 1, 1)])));
        }
        return trip;
    }

    internal static async Task VerifyVehicleReuse(DemoRun run, App03DevHost host, Guid older, Guid newer,
        string output, CancellationToken ct)
    {
        var oldTrip = await ReadReport(host, older, ct);
        var newTrip = await ReadReport(host, newer, ct);
        var initialStock = await ReadVehicle(run, older, ct);
        var first = await Unload(host, older, ct);
        var afterOld = await ReadVehicle(run, older, ct);
        var afterOther = await ReadReport(host, newer, ct);
        var second = await Unload(host, newer, ct);
        var finalStock = await ReadVehicle(run, newer, ct);
        if (oldTrip.TripQty != 1 || newTrip.TripQty != 1 || afterOther != newTrip ||
            initialStock != 2 || afterOld != 1 || finalStock != 0)
            throw new InvalidOperationException("Trip settlement consumed the successor trip's physical stock.");
        await Save("vehicle-reuse", new { older, newer, first, second, initialStock, afterOld, finalStock }, output, ct);
    }

    internal static async Task VerifyQuotaOnly(DemoRun run, App03DevHost host, Guid trip, string output, CancellationToken ct)
    {
        var before = await ReadReport(host, trip, ct);
        var source = await Unload(host, trip, ct);
        var after = await ReadReport(host, trip, ct);
        await run.Verify("tenant-a", app03Session: true);
        await using var db = new SqlConnection(run.Connection("tenant-a"));
        var counts = await db.QuerySingleAsync<QuotaOnly>(new CommandDefinition("""
            SELECT
                (SELECT COUNT(*) FROM dbo.D3TripUnloadLine WHERE TripUnloadId=@source) AS ReturnedLines,
                (SELECT NeverLoadedBaseQuantity FROM dbo.D3BQuotaRelease WHERE TripUnloadId=@source) AS ReleasedWithoutLoad;
            """, new { source }, cancellationToken: ct));
        if (before is not { BQuota: 1, TripQty: 0 } || after is not { BQuota: 0, TripQty: 0, TripStatus: "RECONCILING" }
            || counts != new QuotaOnly(0, 1))
            throw new InvalidOperationException("Unused quota settlement created an unexpected stock return or failed to release quota.");
        await Save("quota-only", new { trip, source, before, after, counts }, output, ct);
    }

    private static Task<TripReport> ReadReport(App03DevHost host, Guid trip, CancellationToken ct) =>
        Send<TripReport>(host, ct, D3 + $"/trips/{trip}", host.ReceiverToken);

    private static async Task<Guid> Unload(App03DevHost host, Guid trip, CancellationToken ct)
    {
        string id = Guid.NewGuid().ToString("N");
        return AssertApplied(await Send<BusinessCommandOutcome>(host, ct, D3 + "/unload", host.ReceiverToken,
            new D3UnloadInput(DevF05Evidence.Receiver, id, id, trip)));
    }

    private static Guid AssertApplied(BusinessCommandOutcome result) =>
        result is { Status: "APPLIED", SourceId: Guid id } ? id
            : throw new InvalidOperationException($"Expected APP-03.6 command source, got {result}.");

    private static async Task<decimal> ReadVehicle(DemoRun run, Guid trip, CancellationToken ct)
    {
        await run.Verify("tenant-a", app03Session: true);
        await using var db = new SqlConnection(run.Connection("tenant-a"));
        return await db.QuerySingleAsync<decimal>(new CommandDefinition("""
            SELECT b.OnHandBaseQuantity FROM dbo.Trip t
            JOIN dbo.PhysicalStockBalance b ON b.TenantId=t.TenantId AND b.StockLocationId=t.VehicleLocationId
            JOIN dbo.Sku k ON k.TenantId=b.TenantId AND k.Id=b.SkuId
            WHERE t.Id=@trip AND k.Code=N'Z-FIELD-EA';
            """, new { trip }, cancellationToken: ct));
    }

    private static Task Save(string name, object evidence, string output, CancellationToken ct) =>
        File.WriteAllTextAsync(Path.Combine(output, name + ".json"),
            JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }), ct);

    private sealed record QuotaOnly(int ReturnedLines, decimal ReleasedWithoutLoad);
}
