using FmcgDms.Preprod.Authorization;
using Xunit;

namespace FmcgDms.Preprod.Authorization.Tests;

public sealed class AuthorizationBoundaryTests
{
    private static AuthorizationContext Valid() => new(
        Guid.NewGuid(), Guid.Empty, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
        1, 1, 1, 0, "tenant.read", "tenant:own");

    private static AuthorizationContext Own() =>
        Valid() is var a ? a with { ResourceTenantId = a.TenantId } : throw new Exception();

    [Fact]
    public void Cross_tenant_resource_is_rejected_before_SQL()
    {
        Assert.Throws<AuthorizationDeniedException>(() => Valid().Validate());
    }

    [Fact]
    public void Valid_server_bound_envelope_is_accepted_for_later_sql_checks() =>
        Own().Validate();

    [Fact]
    public void Maker_cannot_check_own_action()
    {
        var envelope = Own();
        Assert.Throws<AuthorizationDeniedException>(() =>
            (envelope with { MakerId = envelope.ActorId }).Validate());
    }

    [Fact]
    public void Arbitrary_permission_and_invalid_resource_are_rejected()
    {
        var envelope = Own();
        Assert.Throws<AuthorizationDeniedException>(() =>
            (envelope with { PermissionCode = "tenant.write; DROP TABLE" }).Validate());
        Assert.Throws<AuthorizationDeniedException>(() =>
            (envelope with { ResourceKey = "row\nnext" }).Validate());
    }

    [Fact]
    public void Missing_identity_or_revocation_epoch_never_defaults_to_allow()
    {
        var envelope = Own();
        Assert.Throws<AuthorizationDeniedException>(() =>
            (envelope with { SessionId = Guid.Empty }).Validate());
        Assert.Throws<AuthorizationDeniedException>(() =>
            (envelope with { FenceEpoch = 0 }).Validate());
        Assert.Throws<AuthorizationDeniedException>(() =>
            (envelope with { RegistryEpoch = 0 }).Validate());
        Assert.Throws<AuthorizationDeniedException>(() =>
            (envelope with { BindingId = Guid.Empty }).Validate());
        Assert.Throws<AuthorizationDeniedException>(() =>
            (envelope with { SessionEpoch = -1 }).Validate());
    }

    [Fact]
    public async Task B5_runtime_execution_is_denied_before_SQL_without_test_only_gate()
    {
        var authority = Own();
        var fence = new SqlAuthorizationFence(
            "Server=(localdb)\\MSSQLLocalDB;Database=B5NeverCreated;Integrated Security=true");
        var guards = new SqlControlPlaneGuards("FmcgB5Identity_ci", "FmcgB5Registry_ci");
        var error = await Assert.ThrowsAsync<AuthorizationDeniedException>(() =>
            fence.ExecuteAsync(authority, guards,
                (_db, _tx, _ct) => Task.FromResult(1), CancellationToken.None));
        Assert.Equal("B5_RUNTIME_NOT_AUTHORIZED", error.Code);
    }

    [Theory]
    [InlineData("master")]
    [InlineData("tempdb")]
    [InlineData("[control]")]
    [InlineData("other;select")]
    [InlineData("")]
    public void Unapproved_control_database_identifiers_are_denied(string database)
    {
        Assert.Throws<AuthorizationDeniedException>(() =>
            new SqlControlPlaneGuards(database, "FmcgB5Registry_ci"));
    }
}
