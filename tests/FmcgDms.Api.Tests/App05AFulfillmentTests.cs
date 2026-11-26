using FmcgDms.Api.Persistence;
using Xunit;

namespace FmcgDms.Api.Tests;

public sealed class App05AFulfillmentTests
{
    [Fact]
    public void Successor_profile_extends_closure_with_ordered_forward_steps()
    {
        var closure = App03A1Profile.MigrationSteps(true, true, true, true, true).ToArray();
        var successor = App03A1Profile.MigrationSteps(true, true, true, true, true, successor: true).ToArray();
        Assert.Equal(closure, successor[..^6]);
        Assert.Equal(("APP-05.3-v1", 202611250003L), successor[^6]);
        Assert.Equal(("APP-05.3-v1", 202611250004L), successor[^5]);
        Assert.Equal(("APP-05.3-v1", 202611260001L), successor[^4]);
        Assert.Equal(("APP-05.3-v1", 202611260002L), successor[^3]);
        Assert.Equal(("APP-05.3-v1", 202611260003L), successor[^2]);
        Assert.Equal(("APP-05.3-v1", 202611260004L), successor[^1]);
        Assert.Throws<InvalidOperationException>(() =>
            App03A1Profile.MigrationSteps(true, true, true, true, false, successor: true).ToArray());
    }
}
