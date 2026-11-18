using System.Text.RegularExpressions;

namespace FmcgDms.Preprod.Recovery;

public enum B7OutcomeState { Unknown = 0, Committed = 1, IndependentlyProvenNotSent = 2 }

public sealed record B7ExternalOutcome(Guid SourceId, Guid MessageId,
    B7OutcomeState State, int ObservedEffectCount, bool IndependentReceiverJournalVerified);

public sealed record B7RestoreContext(
    Guid TenantId, Guid RestoredTenantId, string ApprovedDatabaseName,
    string RestoredDatabaseName, Guid ApprovedBindingId, Guid RestoredBindingId,
    long ApprovedBindingVersion, long RestoredBindingVersion,
    long CurrentExternalSecurityEpoch, long SnapshotSecurityEpoch,
    bool IndependentRegistryOnline, bool IndependentlyDurableEpochOnline,
    bool RevocationsLinearizedAcrossRestore, bool TenantSuspendedAndNonRoutable,
    bool NewReactivationApprovalVerified, bool CurrentVaultKeyVersionVerified,
    bool RestoreKeySeparatelyAuthorized, bool ReconciledInboxOutboxSourcesAndCases,
    bool IndependentExternalOutcomeJournalOnline,
    string PeerBeforeSha256, string PeerAfterSha256, Guid PeerDatabaseBefore,
    Guid PeerDatabaseAfter, Guid ImplementerIdentity, Guid IndependentReviewerIdentity,
    IReadOnlyList<B7ExternalOutcome> Outcomes);

public sealed record B7RestoreInspection(bool ReadyForSeparatePhysicalReview,
    IReadOnlyList<string> Denials)
{
    // No API, Worker, tenant lifecycle, or SQL activation path consumes this class.
    // The external revocation writer (#53) and PREPROD B.9 gates do not exist yet.
    public bool PermitsTenantActivation => false;
}

public static class B7SafeRestore
{
    private static readonly Regex Hash = new("^[0-9a-fA-F]{64}$", RegexOptions.CultureInvariant);

    public static B7RestoreInspection Inspect(B7RestoreContext p)
    {
        ArgumentNullException.ThrowIfNull(p);
        var errors = new List<string>();
        void Deny(string code) { if (!errors.Contains(code)) errors.Add(code); }

        if (p.TenantId == Guid.Empty || p.TenantId != p.RestoredTenantId ||
            p.ApprovedDatabaseName != "FmcgPpT_" + p.TenantId.ToString("N") ||
            p.ApprovedDatabaseName != p.RestoredDatabaseName ||
            p.ApprovedBindingId == Guid.Empty || p.ApprovedBindingId != p.RestoredBindingId ||
            p.ApprovedBindingVersion < 1 || p.ApprovedBindingVersion != p.RestoredBindingVersion)
            Deny("TENANT_REGISTRY_BINDING_MISMATCH");
        if (!p.IndependentRegistryOnline)
            Deny("INDEPENDENT_REGISTRY_UNAVAILABLE");
        if (!p.IndependentlyDurableEpochOnline || !p.RevocationsLinearizedAcrossRestore ||
            p.CurrentExternalSecurityEpoch < 1 || p.SnapshotSecurityEpoch < 0 ||
            p.SnapshotSecurityEpoch > p.CurrentExternalSecurityEpoch)
            Deny("RESTORE_SAFE_REVOCATION_FENCE_UNVERIFIED");
        if (!p.TenantSuspendedAndNonRoutable || !p.NewReactivationApprovalVerified)
            Deny("SUSPENDED_APPROVED_REACTIVATION_REQUIRED");
        if (!p.CurrentVaultKeyVersionVerified || !p.RestoreKeySeparatelyAuthorized)
            Deny("CURRENT_VAULT_AND_RESTORE_KEY_REQUIRED");
        if (p.PeerDatabaseBefore == Guid.Empty || p.PeerDatabaseAfter != p.PeerDatabaseBefore ||
            !Hash.IsMatch(p.PeerBeforeSha256 ?? "") ||
            !string.Equals(p.PeerBeforeSha256, p.PeerAfterSha256, StringComparison.OrdinalIgnoreCase))
            Deny("PEER_DATABASE_CHANGED_OR_NOT_OBSERVED");
        if (p.ImplementerIdentity == Guid.Empty ||
            p.IndependentReviewerIdentity == Guid.Empty ||
            p.ImplementerIdentity == p.IndependentReviewerIdentity)
            Deny("INDEPENDENT_REVIEWER_REQUIRED");
        if (!p.ReconciledInboxOutboxSourcesAndCases || !p.IndependentExternalOutcomeJournalOnline ||
            p.Outcomes is null)
            Deny("DURABLE_RECONCILIATION_MISSING");

        if (p.Outcomes is not null)
        {
            if (p.Outcomes.Select(x => x.MessageId).Distinct().Count() != p.Outcomes.Count)
                Deny("DUPLICATE_MESSAGE_ID");
            foreach (var o in p.Outcomes)
            {
                if (o.SourceId == Guid.Empty || o.MessageId == Guid.Empty ||
                    !o.IndependentReceiverJournalVerified)
                    Deny("SOURCE_OR_RECEIVER_WITNESS_MISSING");
                if (o.State == B7OutcomeState.Unknown || !Enum.IsDefined(o.State))
                    Deny("UNKNOWN_OUTCOME_HOLD_CASE");
                else if (o.State == B7OutcomeState.Committed && o.ObservedEffectCount != 1)
                    Deny("EXTERNAL_COMMIT_COUNT_INVALID");
                else if (o.State == B7OutcomeState.IndependentlyProvenNotSent &&
                    o.ObservedEffectCount != 0)
                    Deny("NOT_SENT_EFFECT_COUNT_INVALID");
            }
        }
        return new(errors.Count == 0, errors);
    }
}

public sealed record B7DrillTiming(
    DateTime FailureUtc, DateTime LatestRecoveredCommittedWitnessUtc,
    TimeSpan MonotonicUnsafeDuration, bool ClockIndependentlyVerified,
    bool RecoveredWitnessIndependentlyVerified, bool SafeApiAndWorkerProbeVerified,
    bool RestoreChainAndReconciliationVerified);

public sealed record B7TimingInspection(bool WithinTargets, double? RpoSeconds,
    double? RtoSeconds, IReadOnlyList<string> Denials)
{
    public bool IsRealPreprodDrill => false;
}

public static class B7RecoveryTargets
{
    public static B7TimingInspection Inspect(B7DrillTiming t)
    {
        ArgumentNullException.ThrowIfNull(t);
        var errors = new List<string>();
        if (t.FailureUtc.Kind != DateTimeKind.Utc ||
            t.LatestRecoveredCommittedWitnessUtc.Kind != DateTimeKind.Utc ||
            !t.ClockIndependentlyVerified || !t.RecoveredWitnessIndependentlyVerified ||
            !t.SafeApiAndWorkerProbeVerified || !t.RestoreChainAndReconciliationVerified)
            errors.Add("INDEPENDENT_SAFE_RESTORATION_WITNESS_MISSING");
        double rpo = (t.FailureUtc - t.LatestRecoveredCommittedWitnessUtc).TotalSeconds;
        double rto = t.MonotonicUnsafeDuration.TotalSeconds;
        if (rpo < 0 || rpo > 900)
            errors.Add("RPO_OUTSIDE_TARGET");
        if (rto < 0 || rto > 14400)
            errors.Add("RTO_OUTSIDE_TARGET");
        return new(errors.Count == 0,
            errors.Count == 0 ? rpo : null, errors.Count == 0 ? rto : null, errors);
    }
}
