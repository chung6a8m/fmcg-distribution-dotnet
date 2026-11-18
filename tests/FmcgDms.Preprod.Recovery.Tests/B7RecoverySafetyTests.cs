using FmcgDms.Preprod.Recovery;
using Xunit;

namespace FmcgDms.Preprod.Recovery.Tests;

public sealed class B7RecoverySafetyTests
{
    private static readonly string Hash = new('a', 64);

    private static B7BackupInventory HealthyInventory()
    {
        var tenant = Guid.NewGuid();
        var db = Guid.NewGuid();
        var fork = Guid.NewGuid();
        var name = "FmcgPpT_" + tenant.ToString("N");
        var now = new DateTime(2026, 11, 18, 10, 0, 0, DateTimeKind.Utc);
        B7BackupPiece Piece(B7BackupKind kind, int minute, decimal first, decimal last,
            decimal checkpoint = 0, decimal baseLsn = 0) =>
            new(Guid.NewGuid(), name, db, fork, kind, now.AddMinutes(minute), first, last,
                checkpoint, baseLsn, false, true, true, Hash, "offhost/immutable-object",
                "backup-key-v2", true, true);
        var parts = new List<B7BackupPiece>
        {
            Piece(B7BackupKind.Full, -480, 90, 100, 99, 99),
            Piece(B7BackupKind.Differential, -300, 150, 160, 159, 99),
            Piece(B7BackupKind.Differential, -120, 350, 360, 359, 99)
        };
        decimal prev = 100;
        for (var minute = -475; minute <= 0; minute += 5)
        {
            parts.Add(Piece(B7BackupKind.Log, minute, prev, prev + 10));
            prev += 10;
        }
        return new(tenant, name, db, "FULL", now, true, true, parts);
    }

    private static B7RestoreContext HealthyRestore()
    {
        var tenant = Guid.NewGuid();
        var binding = Guid.NewGuid();
        var peer = Guid.NewGuid();
        return new(tenant, tenant, "FmcgPpT_" + tenant.ToString("N"),
            "FmcgPpT_" + tenant.ToString("N"), binding, binding, 8, 8,
            12, 10, true, true, true, true, true, true, true, true, true,
            Hash, Hash, peer, peer, Guid.NewGuid(), Guid.NewGuid(),
            [new(Guid.NewGuid(), Guid.NewGuid(), B7OutcomeState.Committed, 1, true)]);
    }

    [Fact]
    public void Contiguous_encrypted_offhost_full_diff_log_chain_can_be_inspected()
    {
        var inspected = B7BackupChain.Inspect(HealthyInventory());
        Assert.True(inspected.AcceptedForOperatorReview, string.Join(",", inspected.Denials));
        Assert.False(inspected.IsPreprodAccepted);
    }

    [Fact]
    public void Backup_gap_lsn_key_copy_only_or_wrong_database_fails_closed()
    {
        var input = HealthyInventory();
        Assert.False(B7BackupChain.Inspect(input with { RecoveryModel = "SIMPLE" }).AcceptedForOperatorReview);
        Assert.False(B7BackupChain.Inspect(input with { DatabaseName = "master" }).AcceptedForOperatorReview);
        Assert.False(B7BackupChain.Inspect(input with { IndependentRestorePrincipalVerified = false }).AcceptedForOperatorReview);
        var broken = input.Pieces.ToList();
        var index = broken.FindIndex(p => p.Kind == B7BackupKind.Log);
        broken[index] = broken[index] with { FirstLsn = 123456m };
        Assert.Contains("LOG_LSN_GAP_OR_REPLAY", B7BackupChain.Inspect(input with { Pieces = broken }).Denials);
        broken[index] = broken[index] with { FirstLsn = 100m, OffHostDecryptVerified = false };
        Assert.Contains("OFFHOST_ENCRYPTION_CHECKSUM_KEY_PROOF_REQUIRED",
            B7BackupChain.Inspect(input with { Pieces = broken }).Denials);
        var missing = input.Pieces.Where(p => p.Kind != B7BackupKind.Log ||
            p.FinishUtc.Minute != 5).ToArray();
        Assert.False(B7BackupChain.Inspect(input with { Pieces = missing }).AcceptedForOperatorReview);
        var stale = input with { ObservationUtc = input.ObservationUtc.AddMinutes(6) };
        Assert.Contains("FIVE_MINUTE_LOG_INTERVAL_EXCEEDED", B7BackupChain.Inspect(stale).Denials);
        var missingDiffs = input.Pieces
            .Where(p => p.Kind != B7BackupKind.Differential)
            .Append(input.Pieces.First(p => p.Kind == B7BackupKind.Differential)
                with { FinishUtc = input.ObservationUtc.AddMinutes(-30) })
            .ToArray();
        Assert.Contains("DIFFERENTIAL_SCHEDULE_GAP",
            B7BackupChain.Inspect(input with { Pieces = missingDiffs }).Denials);
    }

    [Fact]
    public void Restore_is_never_automatically_activated_even_with_consistent_inputs()
    {
        var result = B7SafeRestore.Inspect(HealthyRestore());
        Assert.True(result.ReadyForSeparatePhysicalReview, string.Join(",", result.Denials));
        Assert.False(result.PermitsTenantActivation);
        Assert.Contains("SOURCE_OR_RECEIVER_WITNESS_MISSING",
            B7SafeRestore.Inspect(HealthyRestore() with
                { Outcomes = [new(Guid.NewGuid(), Guid.NewGuid(), default, 1, false)] }).Denials);
    }

    [Fact]
    public void Stale_fence_wrong_binding_peer_change_and_missing_approver_block()
    {
        var original = HealthyRestore();
        Assert.Contains("RESTORE_SAFE_REVOCATION_FENCE_UNVERIFIED",
            B7SafeRestore.Inspect(original with { RevocationsLinearizedAcrossRestore = false }).Denials);
        Assert.Contains("RESTORE_SAFE_REVOCATION_FENCE_UNVERIFIED",
            B7SafeRestore.Inspect(original with { CurrentExternalSecurityEpoch = 9 }).Denials);
        Assert.Contains("TENANT_REGISTRY_BINDING_MISMATCH",
            B7SafeRestore.Inspect(original with { RestoredBindingId = Guid.NewGuid() }).Denials);
        Assert.Contains("PEER_DATABASE_CHANGED_OR_NOT_OBSERVED",
            B7SafeRestore.Inspect(original with { PeerDatabaseAfter = Guid.NewGuid() }).Denials);
        Assert.Contains("SUSPENDED_APPROVED_REACTIVATION_REQUIRED",
            B7SafeRestore.Inspect(original with { NewReactivationApprovalVerified = false }).Denials);
        Assert.Contains("CURRENT_VAULT_AND_RESTORE_KEY_REQUIRED",
            B7SafeRestore.Inspect(original with { RestoreKeySeparatelyAuthorized = false }).Denials);
        Assert.Contains("INDEPENDENT_REVIEWER_REQUIRED",
            B7SafeRestore.Inspect(original with { IndependentReviewerIdentity = original.ImplementerIdentity }).Denials);
    }

    [Fact]
    public void Lost_ack_unknown_duplicate_or_unwitnessed_effect_holds_same_message_id()
    {
        var original = HealthyRestore();
        var committed = original.Outcomes[0];
        var unknown = committed with { State = B7OutcomeState.Unknown };
        Assert.Contains("UNKNOWN_OUTCOME_HOLD_CASE",
            B7SafeRestore.Inspect(original with { Outcomes = [unknown] }).Denials);
        Assert.Contains("UNKNOWN_OUTCOME_HOLD_CASE",
            B7SafeRestore.Inspect(original with
                { Outcomes = [committed with { State = (B7OutcomeState)99 }] }).Denials);
        Assert.Contains("EXTERNAL_COMMIT_COUNT_INVALID",
            B7SafeRestore.Inspect(original with { Outcomes = [committed with { ObservedEffectCount = 2 }] }).Denials);
        Assert.Contains("DUPLICATE_MESSAGE_ID",
            B7SafeRestore.Inspect(original with { Outcomes = [committed, committed] }).Denials);
        Assert.Contains("SOURCE_OR_RECEIVER_WITNESS_MISSING",
            B7SafeRestore.Inspect(original with { Outcomes = [committed with { IndependentReceiverJournalVerified = false }] }).Denials);
        Assert.False(B7SafeRestore.Inspect(original with { Outcomes = [unknown] }).PermitsTenantActivation);
    }

    [Fact]
    public void Rpo_and_rto_require_independent_witness_and_monotonic_safe_recovery()
    {
        var t = new B7DrillTiming(new DateTime(2026, 11, 18, 10, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 11, 18, 9, 50, 0, DateTimeKind.Utc),
            TimeSpan.FromHours(3), true, true, true, true);
        var ok = B7RecoveryTargets.Inspect(t);
        Assert.True(ok.WithinTargets);
        Assert.Equal(600, ok.RpoSeconds);
        Assert.Equal(10800, ok.RtoSeconds);
        Assert.False(ok.IsRealPreprodDrill);
        Assert.Contains("RPO_OUTSIDE_TARGET", B7RecoveryTargets.Inspect(t with
            { LatestRecoveredCommittedWitnessUtc = t.FailureUtc.AddMinutes(-16) }).Denials);
        Assert.Contains("RTO_OUTSIDE_TARGET", B7RecoveryTargets.Inspect(t with
            { MonotonicUnsafeDuration = TimeSpan.FromHours(5) }).Denials);
        Assert.Contains("INDEPENDENT_SAFE_RESTORATION_WITNESS_MISSING",
            B7RecoveryTargets.Inspect(t with { SafeApiAndWorkerProbeVerified = false }).Denials);
    }
}
