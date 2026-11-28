using FmcgDms.Demo;
using Xunit;

namespace FmcgDms.Api.Tests;

public sealed class App03SqlObserverStartupWitnessTests
{
    [Fact]
    public void Only_exact_bounded_milestones_survive_and_raw_stderr_is_not_retained()
    {
        var witness = new App03SqlObserverStartupWitness();
        witness.Stdout(null);
        witness.Stdout("private exception message");
        witness.Stdout("APP03_SQL_OBSERVER_STARTUP_V1 entered");
        witness.Stdout("APP03_SQL_OBSERVER_STARTUP_V1 entered");
        witness.Stdout("APP03_SQL_OBSERVER_STARTUP_V1 guarded secret");
        witness.Stdout("APP03_SQL_OBSERVER_STARTUP_V1 guarded");
        witness.Stdout("APP03_SQL_OBSERVER_STARTUP_V1 connection-open");
        witness.Stderr(null);
        Assert.False(witness.Snapshot().StderrObserved);
        witness.Stderr("private SQL message");
        var result = witness.Snapshot();
        Assert.Equal(new[] { "entered", "guarded", "connection-open" }, result.Stages);
        Assert.True(result.StderrObserved);
    }

    [Fact]
    public void Missing_child_markers_are_not_inferred()
    {
        var witness = new App03SqlObserverStartupWitness();
        Assert.Empty(witness.Snapshot().Stages);
        Assert.False(witness.Snapshot().StderrObserved);
    }
}
