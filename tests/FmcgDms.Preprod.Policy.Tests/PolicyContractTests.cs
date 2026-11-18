using FmcgDms.Preprod.Policy;
using Xunit;

namespace FmcgDms.Preprod.Policy.Tests;

public sealed class PolicyContractTests
{
    private static B6PolicyProposal Valid() => new(
        Guid.NewGuid(), Guid.NewGuid(), "approval.enabled", "L1", null,
        B6PolicyValue.Approved(0m), DateTime.UtcNow.AddMinutes(2),
        DateTime.UtcNow.AddMinutes(10), Guid.NewGuid(), new byte[32], 1);

    [Fact]
    public void Explicitly_approved_zero_is_not_unset()
    {
        Assert.False(B6PolicyValue.Unset.IsApproved);
        Assert.Throws<B6PolicyDeniedException>(() => B6PolicyValue.Unset.RequireApproved());
        var zero = B6PolicyValue.Approved(0m);
        Assert.True(zero.IsApproved);
        zero.RequireApproved();
        Assert.Equal(0m, zero.NumericValue);
    }

    [Fact]
    public void Source_envelope_never_defaults_missing_fields()
    {
        Valid().Validate();
        Assert.Throws<B6PolicyDeniedException>(() => (Valid() with { TenantId=Guid.Empty }).Validate());
        Assert.Throws<B6PolicyDeniedException>(() => (Valid() with { SourceHash=new byte[31] }).Validate());
        Assert.Throws<B6PolicyDeniedException>(() => (Valid() with { SourceVersion=0 }).Validate());
        Assert.Throws<B6PolicyDeniedException>(() => (Valid() with { Tier="L2" }).Validate());
        Assert.Throws<B6PolicyDeniedException>(() => (Valid() with { PolicyCode="policy;drop" }).Validate());
    }

    [Fact]
    public void Case_specific_snapshot_requires_exact_case_context()
    {
        var now = DateTime.UtcNow;
        var caseId = Guid.NewGuid();
        var snapshot = new B6EffectiveSnapshot(
            Guid.NewGuid(), "approval.enabled", Guid.NewGuid(), "L2",
            caseId, 0.5m, Guid.NewGuid(), new byte[32], 1,
            now.AddMinutes(-1), now.AddMinutes(1));
        snapshot.AssertExpected(snapshot.TenantId, snapshot.ProposalId, new byte[32],
            now, caseId);
        Assert.Throws<B6PolicyDeniedException>(() =>
            snapshot.AssertExpected(snapshot.TenantId, snapshot.ProposalId,
                new byte[32], now));
        Assert.Throws<B6PolicyDeniedException>(() =>
            snapshot.AssertExpected(snapshot.TenantId, snapshot.ProposalId,
                new byte[32], now, Guid.NewGuid()));
        Assert.Throws<B6PolicyDeniedException>(() =>
            snapshot.AssertExpected(snapshot.TenantId, snapshot.ProposalId,
                new byte[31], now, caseId));
    }

    [Fact]
    public void Effective_interval_is_half_open_and_expected_hash_is_pinned()
    {
        var from = DateTime.UtcNow;
        var until = from.AddDays(1);
        var a = new B6EffectiveSnapshot(Guid.NewGuid(), "approval.enabled",
            Guid.NewGuid(), "L1", null, 0m, Guid.NewGuid(), new byte[32], 1, from, until);
        Assert.True(a.IsEffectiveAt(from));
        Assert.False(a.IsEffectiveAt(until));
        a.AssertExpected(a.TenantId, a.ProposalId, new byte[32], from);
        Assert.Throws<B6PolicyDeniedException>(() =>
            a.AssertExpected(a.TenantId, Guid.NewGuid(), new byte[32], from));
        Assert.Throws<B6PolicyDeniedException>(() =>
            a.AssertExpected(a.TenantId, a.ProposalId, new byte[32], until));
    }
}
