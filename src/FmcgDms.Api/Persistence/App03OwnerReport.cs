using System.Data;
using System.Security.Claims;
using System.Text.Json.Serialization;
using Dapper;
using FmcgDms.Api.Tenancy;
using Microsoft.Data.SqlClient;

namespace FmcgDms.Api.Persistence;

internal sealed record OwnerSource(string Metric, string Unit, Guid? Id, Guid? RelatedId, string Label, string State,
    [property: JsonNumberHandling(JsonNumberHandling.WriteAsString)] decimal? Value,
    string? OwnerRole = null, string? NextAction = null);

internal sealed record OwnerMetric(string Metric, string Unit, int Known, int Expected,
    [property: JsonNumberHandling(JsonNumberHandling.WriteAsString)] decimal? Value, OwnerSource[] Sources);

internal static partial class App03OwnerReport
{
    private sealed record TripStock(Guid TripId, Guid SkuId, Guid? VehicleLocationId, string Label, string Unit,
        bool NeverLoaded, decimal? Quantity, decimal? VehicleQuantity, decimal? VehicleCost);

    internal static Task<string> Session(TenantConnections connections, ClaimsPrincipal user, CancellationToken ct) =>
        WithOwner(connections, user, (db, tx, _) => db.QuerySingleAsync<string>(
            App02a3Sql.Cmd("SELECT Nonce FROM dbo.App00ScratchMarker", null, tx, ct)), ct);

    internal static Task<object> Read(TenantConnections connections, ClaimsPrincipal user, CancellationToken ct) =>
        WithOwner<object>(connections, user, async (db, tx, tenant) =>
        {
            using var result = await db.QueryMultipleAsync(App02a3Sql.Cmd(ReportSql + "\n" + TripStockSql + "\n" +
                $"SELECT TODATETIMEOFFSET(SYSUTCDATETIME(),'+00:00'), TODATETIMEOFFSET({App02c6Clock.SqlUtcNow},'+00:00');",
                new { Tenant = tenant, Employee = (string?)null }, tx, ct));
            var rows = (await result.ReadAsync<OwnerSource>()).AsList();
            AddTripStock(rows, await result.ReadAsync<TripStock>());
            var clock = await result.ReadSingleAsync<(DateTimeOffset AsOf, DateTimeOffset BusinessCutoff)>();
            var metrics = rows.GroupBy(row => (row.Metric, row.Unit)).Select(group =>
            {
                int known = group.Count(row => row.Value.HasValue);
                return new OwnerMetric(group.Key.Metric, group.Key.Unit, known, group.Count(),
                    known == 0 ? null : group.Sum(row => row.Value), group.ToArray());
            }).ToArray();
            return new { TenantId = tenant, clock.AsOf, clock.BusinessCutoff, Metrics = metrics };
        }, ct);

    private static void AddTripStock(List<OwnerSource> rows, IEnumerable<TripStock> stocks)
    {
        foreach (var vehicle in stocks.GroupBy(stock => (stock.VehicleLocationId, stock.SkuId)))
        {
            var baseline = vehicle.First();
            decimal vehicleQuantity = baseline.VehicleQuantity.GetValueOrDefault();
            decimal vehicleCost = baseline.VehicleCost.GetValueOrDefault();
            bool reconciled = baseline.VehicleQuantity.HasValue && baseline.VehicleCost.HasValue
                && vehicle.All(stock => stock.Quantity is >= 0)
                && vehicle.Sum(stock => stock.Quantity.GetValueOrDefault()) == vehicleQuantity;
            decimal cumulativeQuantity = 0;
            decimal allocatedValue = 0;
            foreach (var stock in vehicle)
            {
                decimal? quantity = null;
                decimal? value = null;
                if (stock.NeverLoaded)
                    quantity = value = 0;
                else if (reconciled)
                {
                    quantity = stock.Quantity.GetValueOrDefault();
                    cumulativeQuantity += quantity.Value;
                    // Consecutive rounded totals telescope to one stored-precision vehicle allocation.
                    decimal cumulativeValue = App01dCashierUnloadService.D3CarryingCost(vehicleCost, vehicleQuantity, cumulativeQuantity);
                    value = cumulativeValue - allocatedValue;
                    allocatedValue = cumulativeValue;
                }
                rows.Add(new("trip-qty", stock.Unit, stock.TripId, stock.SkuId, stock.Label,
                    stock.NeverLoaded ? "NOT_LOADED" : quantity.HasValue ? "TRIP_LEDGER" : "RECONCILIATION_REQUIRED", quantity));
                rows.Add(new("trip-value", "VND", stock.TripId, stock.SkuId, stock.Label,
                    stock.NeverLoaded ? "NOT_LOADED" : value.HasValue ? "D3_CARRYING_COST" : "RECONCILIATION_REQUIRED", value));
            }
        }
    }

    private static async Task<T> WithOwner<T>(TenantConnections connections, ClaimsPrincipal user,
        Func<SqlConnection, SqlTransaction, string, Task<T>> read, CancellationToken ct)
    {
        if (!connections.UsesApp03Finance || !user.HasClaim("synthetic_role", "owner"))
            throw new UnauthorizedAccessException("Owner overview requires the integrated synthetic Owner role.");
        string tenant = App02a3Sql.Tenant(user);
        string actor = App02a3Sql.Actor(user);
        await using var db = await connections.OpenApp02c4Async(user, ct);
        await using var tx = (SqlTransaction)await db.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        await App02a3Sql.RequireRole(db, tx, tenant, actor, "owner", ct);
        var result = await read(db, tx, tenant);
        await tx.CommitAsync(ct);
        return result;
    }
}
