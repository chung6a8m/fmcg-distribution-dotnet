using FmcgDms.Api.Persistence;
using Xunit;

namespace FmcgDms.Api.Tests;

public sealed class App05CloseReadinessTests
{
    [Theory]
    [InlineData(5, 4, 3, 1, 2, 4, 3, 0)]
    [InlineData(5, 4, 3, 1, 0, 4, 1, 2)]
    [InlineData(5, 0, 0, 0, 0, 5, 0, 0)]
    [InlineData(40, 40, 40, 25, 15, 15, 15, 0)]
    public void Splits_remaining_warehouse_held_and_on_vehicle(int confirmed, int granted, int loaded,
        int delivered, int returned, int remaining, int held, int onVehicle)
    {
        var line = new App05CloseReadiness.ARow(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            "SKU", "EA", confirmed, granted, delivered, 0, loaded, delivered, returned, true).ToView();
        Assert.Equal((remaining, held, onVehicle), (line.Remaining, line.Held, line.OnVehicle));
    }

    [Theory]
    [InlineData(false, 0, 0, "UNUSED")]
    [InlineData(true, 1, 0, "BLOCKED")]
    [InlineData(true, 0, 2, "NEEDS_ASSIGNMENT")]
    [InlineData(true, 0, 0, "READY")]
    public void Hard_blockers_take_priority_and_unused_is_outside_close_population(bool used,
        int hard, int missing, string expected) =>
        Assert.Equal(expected, App05CloseReadiness.ReadinessState(used, hard, missing, missing));

    [Theory]
    [InlineData("2026-11-23T09:00:00.0000001+07:00", true)]
    [InlineData("2026-11-23T02:00:00Z", true)]
    [InlineData("2026-11-23T09:00:00", false)]
    [InlineData("2026-11-23T09:00:00+15:00", false)]
    public void Assignment_deadline_requires_explicit_offset(string value, bool valid) =>
        Assert.Equal(valid, App05CloseReadiness.TryDueAt(value, out _));

    [Theory]
    [InlineData(null, true, "AWAITING_CUTOFF")]
    [InlineData(null, false, "AWAITING_RECONCILIATION")]
    [InlineData("RECONCILED_OK", false, null)]
    [InlineData("DIFF_OPEN", false, "DIFF_OPEN")]
    public void Accounting_cutoff_and_reconciliation_have_distinct_outcomes(string? status,
        bool beforeCutoff, string? expected)
    {
        var now = DateTime.Parse("2026-11-24T02:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        var row = new App05CloseReadiness.FinanceRow(Guid.NewGuid(), Guid.NewGuid(),
            beforeCutoff ? now : now.AddDays(-1), Guid.NewGuid(), Guid.NewGuid(),
            status, false, null);
        Assert.Equal(expected, row.Pending(now));
    }
}
