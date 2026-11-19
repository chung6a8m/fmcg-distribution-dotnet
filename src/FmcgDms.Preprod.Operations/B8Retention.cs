using System.Text.RegularExpressions;

namespace FmcgDms.Preprod.Operations;

public enum B8EvidenceClass
{
    DiagnosticTelemetry = 1, SecurityAudit = 2,
    DurableEventAndCase = 3, LedgerAndBusinessSource = 4,
    RecoveryAndLegalEvidence = 5
}
public enum B8Disposition { Archive = 1, Purge = 2 }

/// <summary>
/// Dry-run classification/approval gate only; NEVER issues DELETE, TTL, DROP,
/// external storage deletion or ReleaseHold. Legal retention duration and
/// governing jurisdiction remain UNSET until explicit Owner/legal approval.
/// </summary>
public sealed record B8RetentionRequest(
    Guid TenantId, B8EvidenceClass EvidenceClass, B8Disposition Disposition,
    Guid MakerId, Guid CheckerId, Guid IndependentReviewerId,
    string ApprovedAuthorityVersion, DateTime NowUtc, DateTime? ExplicitRetainUntilUtc,
    bool LegalAuthorityOnline, bool ClassificationApproved, bool LegalHoldActive,
    bool IndependentlyVerifiedFullManifest, bool ApprovedImmutableArchiveExists,
    bool PeerUnchanged, string BeforeManifestSha256, string AfterManifestSha256);

public sealed record B8RetentionInspection(bool EligibleForSeparateOperatorAction,
    IReadOnlyList<string> Denials)
{
    public bool PerformsDeletion => false;
    public bool IsPhysicalPreprodVerified => false;
}

public static class B8RetentionGate
{
    private static readonly Regex Hash = new("^[0-9a-fA-F]{64}$", RegexOptions.CultureInvariant);

    public static B8RetentionInspection Inspect(B8RetentionRequest r)
    {
        ArgumentNullException.ThrowIfNull(r);
        var errors = new List<string>();
        void Deny(string code) { if (!errors.Contains(code)) errors.Add(code); }

        if (r.TenantId == Guid.Empty || !Enum.IsDefined(r.EvidenceClass) ||
            !Enum.IsDefined(r.Disposition) || r.MakerId == Guid.Empty ||
            r.CheckerId == Guid.Empty || r.IndependentReviewerId == Guid.Empty ||
            r.MakerId == r.CheckerId || r.MakerId == r.IndependentReviewerId ||
            r.CheckerId == r.IndependentReviewerId ||
            string.IsNullOrWhiteSpace(r.ApprovedAuthorityVersion) ||
            !r.ApprovedAuthorityVersion.All(ch =>
                char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.') ||
            r.NowUtc.Kind != DateTimeKind.Utc || !r.LegalAuthorityOnline ||
            !r.ClassificationApproved)
            Deny("B8_APPROVAL_OR_CLASSIFICATION_MISSING");
        if (r.LegalHoldActive)
            Deny("B8_ACTIVE_LEGAL_HOLD");
        if (!r.IndependentlyVerifiedFullManifest ||
            !Hash.IsMatch(r.BeforeManifestSha256 ?? "") ||
            !Hash.IsMatch(r.AfterManifestSha256 ?? "") ||
            (r.Disposition == B8Disposition.Archive &&
             !string.Equals(r.BeforeManifestSha256, r.AfterManifestSha256,
                 StringComparison.OrdinalIgnoreCase)) ||
            !r.PeerUnchanged)
            Deny("B8_MANIFEST_OR_PEER_PROOF_REQUIRED");
        if (!r.ApprovedImmutableArchiveExists)
            Deny("B8_IMMUTABLE_ARCHIVE_RECEIPT_MISSING");
        // The APP-02A durable event/CASE/source/ledger and immutable security,
        // backup and legal records are categorically not automatically purgeable.
        if (r.Disposition == B8Disposition.Purge)
        {
            if (r.EvidenceClass != B8EvidenceClass.DiagnosticTelemetry)
                Deny("B8_DURABLE_OR_AUDIT_PURGE_FORBIDDEN");
            if (r.ExplicitRetainUntilUtc is not { Kind: DateTimeKind.Utc } until ||
                until > r.NowUtc)
                Deny("B8_RETENTION_UNSET_OR_NOT_ELIGIBLE");
        }
        return new(errors.Count == 0, errors);
    }
}
