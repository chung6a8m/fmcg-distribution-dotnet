using FmcgDms.Api.Persistence;
using Xunit;

namespace FmcgDms.Api.Tests;

public sealed class App05ProfileTests
{
    [Fact]
    public void Close_readiness_adds_one_forward_step_after_finance()
    {
        var predecessor = App03A1Profile.MigrationSteps(true, true, true).ToArray();
        var readiness = App03A1Profile.MigrationSteps(true, true, true, closeReadiness: true).ToArray();

        Assert.Equal(predecessor, readiness[..^1]);
        Assert.Equal((App03A1Profile.CloseReadinessName, 202611250001L), readiness[^1]);
    }

    [Fact]
    public void Close_readiness_requires_its_exact_history_and_preserves_finance_schema_contract()
    {
        var finance = App03A1Profile.MigrationSteps(true, true, true).Select(step => step.Version).ToArray();
        var predecessor = new App03A1Profile.SchemaState(true, true, string.Join(',', finance), true, true);
        var upgraded = predecessor with { FeatureVersions = string.Join(',', finance.Append(202611250001L)) };
        var incomplete = predecessor with { FeatureVersions = string.Join(',', finance.SkipLast(1).Append(202611250001L)) };

        App03A1Profile.RequireSchema(predecessor, complete: false, field: true, settlement: true, finance: true, closeReadiness: true);
        App03A1Profile.RequireSchema(upgraded, complete: true, field: true, settlement: true, finance: true, closeReadiness: true);
        Assert.Throws<InvalidOperationException>(() => App03A1Profile.RequireSchema(
            predecessor, complete: true, field: true, settlement: true, finance: true, closeReadiness: true));
        Assert.Throws<InvalidOperationException>(() => App03A1Profile.RequireSchema(
            upgraded, complete: true, field: true, settlement: true, finance: true));
        Assert.Throws<InvalidOperationException>(() => App03A1Profile.RequireSchema(
            incomplete, complete: false, field: true, settlement: true, finance: true, closeReadiness: true));
    }
}
