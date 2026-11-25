using FmcgDms.Api.Persistence;
using Xunit;

namespace FmcgDms.Api.Tests;

public sealed class App05TripClosureTests
{
    [Theory]
    [InlineData("RECONCILING", "READY", 2, 2, true, "CLOSED")]
    [InlineData("RECONCILING", "READY_WITH_WORK", 2, 2, true, "CLOSED_WITH_OPEN_CASES")]
    [InlineData("PREPARED", "UNUSED", 0, 0, false, null)]
    [InlineData("RECONCILING", "NEEDS_ASSIGNMENT", 2, 2, true, null)]
    [InlineData("RECONCILING", "BLOCKED", 2, 2, true, null)]
    [InlineData("RECONCILING", "READY", 1, 2, true, null)]
    [InlineData("RECONCILING", "READY", 2, 2, false, null)]
    [InlineData("CLOSED", "READY", 2, 2, true, null)]
    public void Decision_requires_proven_handoff_source_and_readiness(string status, string state,
        int known, int expected, bool hasHandoff, string? outcome) =>
        Assert.Equal(outcome, App05CloseReadiness.CloseTarget(status, state, new(known, expected), hasHandoff ? Guid.NewGuid() : null));
}
