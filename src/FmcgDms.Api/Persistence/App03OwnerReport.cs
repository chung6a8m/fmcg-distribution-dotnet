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
    private sealed record TripStock(Guid TripId, Guid SkuId, string Label, string Unit, decimal Quantity,
        decimal VehicleQuantity, decimal VehicleCost);

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
            foreach (var stock in await result.ReadAsync<TripStock>())
            {
                rows.Add(new("trip-qty", stock.Unit, stock.TripId, stock.SkuId, stock.Label, "TRIP_LEDGER", stock.Quantity));
                decimal? value = stock.Quantity >= 0 && stock.Quantity <= stock.VehicleQuantity
                    ? App01dCashierUnloadService.D3CarryingCost(stock.VehicleCost, stock.VehicleQuantity, stock.Quantity) : null;
                rows.Add(new("trip-value", "VND", stock.TripId, stock.SkuId, stock.Label, "D3_CARRYING_COST", value));
            }
            var clock = await result.ReadSingleAsync<(DateTimeOffset AsOf, DateTimeOffset BusinessCutoff)>();
            var metrics = rows.GroupBy(row => (row.Metric, row.Unit)).Select(group =>
            {
                int known = group.Count(row => row.Value.HasValue);
                return new OwnerMetric(group.Key.Metric, group.Key.Unit, known, group.Count(),
                    known == 0 ? null : group.Sum(row => row.Value), group.ToArray());
            }).ToArray();
            return new { TenantId = tenant, clock.AsOf, clock.BusinessCutoff, Metrics = metrics };
        }, ct);

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
