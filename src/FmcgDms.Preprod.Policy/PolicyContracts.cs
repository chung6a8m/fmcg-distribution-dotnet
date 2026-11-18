namespace FmcgDms.Preprod.Policy;

// B.6 is intentionally a stand-alone operator-only contract. It is NOT
// referenced or registered by the PREPROD API, Worker, or business service.
public static class B6Schema
{
    public const string Version = "B6_20261118_001";
}

public enum B6ValueState
{
    Unset = 0,
    ExplicitlySet = 1
}

/// <summary>UNSET is not the value zero. There is no implicit default.</summary>
public readonly record struct B6PolicyValue(B6ValueState State, decimal? NumericValue)
{
    public static B6PolicyValue Unset => new(B6ValueState.Unset, null);
    public static B6PolicyValue Approved(decimal value) => new(B6ValueState.ExplicitlySet, value);
    public bool IsApproved => State == B6ValueState.ExplicitlySet && NumericValue.HasValue;

    public void RequireApproved()
    {
        if (!IsApproved) throw new B6PolicyDeniedException("B6_UNSET_DENIED");
    }
}

public sealed class B6PolicyDeniedException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

/// <summary>
/// Validated source envelope only. Caller-provided fields are NOT proof of
/// actor, tenant, CASE authority, or L0 bounds: SQL must independently bind
/// SUSER_SNAME(), tenant operator scope, immutable CASE source and L0 policy.
/// </summary>
public sealed record B6PolicyProposal(
    Guid TenantId,
    Guid ProposalId,
    string PolicyCode,
    string Tier,
    Guid? CaseId,
    B6PolicyValue Value,
    DateTime EffectiveFromUtc,
    DateTime EffectiveUntilUtc,
    Guid SourceId,
    byte[] SourceHash,
    long SourceVersion)
{
    public void Validate()
    {
        if (TenantId == Guid.Empty || ProposalId == Guid.Empty ||
            SourceId == Guid.Empty || SourceVersion < 1 ||
            string.IsNullOrWhiteSpace(PolicyCode) || PolicyCode.Length > 96 ||
            PolicyCode.Any(ch => !(char.IsAsciiLetterOrDigit(ch) || ch is '.' or '-' or '_')) ||
            Tier is not ("L1" or "L2") ||
            (Tier == "L1" && CaseId is not null) ||
            (Tier == "L2" && (CaseId is null || CaseId == Guid.Empty)) ||
            SourceHash is null || SourceHash.Length != 32 ||
            EffectiveFromUtc.Kind != DateTimeKind.Utc ||
            EffectiveUntilUtc.Kind != DateTimeKind.Utc ||
            EffectiveFromUtc >= EffectiveUntilUtc ||
            (Value.State == B6ValueState.Unset && Value.NumericValue.HasValue) ||
            (Value.State == B6ValueState.ExplicitlySet && !Value.NumericValue.HasValue) ||
            (Value.State is not (B6ValueState.Unset or B6ValueState.ExplicitlySet)))
            throw new B6PolicyDeniedException("B6_ENVELOPE_DENIED");
    }
}

public sealed record B6EffectiveSnapshot(
    Guid TenantId, string PolicyCode, Guid ProposalId, string Tier,
    Guid? CaseId, decimal NumericValue, Guid SourceId, byte[] SourceHash, long SourceVersion,
    DateTime EffectiveFromUtc, DateTime EffectiveUntilUtc)
{
    public bool IsEffectiveAt(DateTime utcNow) =>
        utcNow.Kind == DateTimeKind.Utc &&
        EffectiveFromUtc <= utcNow && utcNow < EffectiveUntilUtc;

    public void AssertExpected(Guid tenantId, Guid proposalId, ReadOnlySpan<byte> sourceHash,
        DateTime nowUtc, Guid? currentCaseId = null)
    {
        if (tenantId != TenantId || proposalId != ProposalId ||
            !((Tier == "L1" && CaseId is null) ||
              (Tier == "L2" && CaseId is not null && CaseId == currentCaseId)) ||
            SourceHash is not { Length: 32 } || sourceHash.Length != 32 ||
            !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                SourceHash, sourceHash) || !IsEffectiveAt(nowUtc) ||
            SourceVersion < 1)
            throw new B6PolicyDeniedException("B6_STALE_SNAPSHOT_DENIED");
    }
}
