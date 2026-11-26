using FmcgDms.Api.Persistence;
using Xunit;

namespace FmcgDms.Api.Tests;

public sealed class App05ReconciliationProofTests
{
    private static readonly DateTimeOffset From = new(2026, 11, 23, 0, 0, 0, TimeSpan.FromHours(7));
    private static readonly DateTimeOffset Cutoff = From.AddDays(1);
    private static readonly DateTimeOffset AsOf = Cutoff.AddHours(9);

    [Theory]
    [InlineData(0, true)]
    [InlineData(9, true)]
    [InlineData(24, false)]
    [InlineData(33, false)]
    [InlineData(-1, false)]
    public void Report_covers_only_sources_inside_its_half_open_window(int hoursAfterFrom, bool covered)
    {
        Assert.Equal(covered, App02d4Accounting.CoversReconciliationSource(
            From, Cutoff, AsOf, AsOf, From.AddHours(hoursAfterFrom)));
    }

    [Fact]
    public void Report_cannot_cover_a_source_before_its_as_of_or_with_an_inverted_window()
    {
        Assert.False(App02d4Accounting.CoversReconciliationSource(From, Cutoff, AsOf, AsOf.AddTicks(-1), From));
        Assert.False(App02d4Accounting.CoversReconciliationSource(From, Cutoff, From, AsOf, From));
        Assert.False(App02d4Accounting.CoversReconciliationSource(Cutoff, Cutoff, AsOf, AsOf, From));
    }

    [Theory]
    [InlineData(false, false, false, "REPORT_STALE")]
    [InlineData(true, false, true, "REPORT_STALE")]
    [InlineData(true, true, false, "CASE_OPEN")]
    [InlineData(true, false, false, null)]
    public void Latest_ok_report_completes_only_work_with_current_proof(bool provesLine, bool openCase, bool stale,
        string? pending)
    {
        var projection = new App05CloseReadiness.FinanceProjection(Guid.NewGuid(), Guid.NewGuid(), From.AddHours(9),
            Guid.NewGuid(), Guid.NewGuid(), "RECONCILED_OK", stale, openCase ? Guid.NewGuid() : null,
            Guid.NewGuid(), From, Cutoff, AsOf, provesLine);
        Assert.Equal(pending, projection.ToRow(AsOf).Pending(AsOf));
    }
}
