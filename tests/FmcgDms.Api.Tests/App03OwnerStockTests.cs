using FmcgDms.Api.Persistence;
using Xunit;

namespace FmcgDms.Api.Tests;

public sealed class App03OwnerStockTests
{
    [Fact]
    public void Reconciled_fractional_vehicle_value_is_allocated_once_at_stored_precision()
    {
        Guid vehicle = Guid.NewGuid(), sku = Guid.NewGuid(), first = Guid.NewGuid(), second = Guid.NewGuid();
        var rows = new List<OwnerSource>();
        App03OwnerReport.AddTripStock(rows,
        [
            new(first, sku, vehicle, "First trip", "EA", false, 1, 2, 100.0001m, 2, 100.0001m),
            new(second, sku, vehicle, "Second trip", "EA", false, 1, 2, 100.0001m, 2, 100.0001m)
        ]);

        var values = rows.Where(row => row.Metric == "trip-value").ToArray();
        Assert.Collection(values,
            row => Assert.Equal((first, 50.0001m), (row.Id!.Value, row.Value!.Value)),
            row => Assert.Equal((second, 50.0000m), (row.Id!.Value, row.Value!.Value)));
        Assert.Equal(100.0001m, values.Sum(row => row.Value));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Zero_quantity_requires_source_cost_and_a_zero_value(bool missingSourceCost)
    {
        Guid vehicle = Guid.NewGuid(), sku = Guid.NewGuid(), loaded = Guid.NewGuid(), neverLoaded = Guid.NewGuid();
        decimal cachedCost = missingSourceCost ? 0 : 1;
        decimal? sourceCost = missingSourceCost ? null : cachedCost;
        var rows = new List<OwnerSource>();
        App03OwnerReport.AddTripStock(rows,
        [
            new(loaded, sku, vehicle, "Previously loaded", "EA", false, 0, 0, cachedCost, 0, sourceCost),
            new(neverLoaded, sku, vehicle, "Never loaded", "EA", true, 0, 0, cachedCost, 0, sourceCost)
        ]);

        Assert.Equal(0m, rows.Single(row => row.Metric == "trip-qty" && row.Id == loaded).Value);
        var value = rows.Single(row => row.Metric == "trip-value" && row.Id == loaded);
        Assert.Null(value.Value);
        Assert.Equal("RECONCILIATION_REQUIRED", value.State);
        Assert.Equal(0m, rows.Single(row => row.Metric == "trip-value" && row.Id == neverLoaded).Value);
    }
}
