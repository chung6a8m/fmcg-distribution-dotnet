using System.Text.RegularExpressions;

namespace FmcgDms.Preprod.Recovery;

/// <summary>B.7 operator evidence, never a backup scheduler, key provider or business admission API.</summary>
public enum B7BackupKind { Unknown = 0, Full = 1, Differential = 2, Log = 3 }

public sealed record B7BackupPiece(
    Guid BackupId, string DatabaseName, Guid DatabaseId, Guid RecoveryForkId,
    B7BackupKind Kind, DateTime FinishUtc, decimal FirstLsn, decimal LastLsn,
    decimal CheckpointLsn, decimal DatabaseBackupLsn, bool CopyOnly,
    bool SqlEncrypted, bool SqlChecksum, string OffHostObjectSha256,
    string OffHostObjectId, string KeyVersionId, bool OffHostHashVerified,
    bool OffHostDecryptVerified);

public sealed record B7BackupInventory(
    Guid TenantId, string DatabaseName, Guid DatabaseId, string RecoveryModel,
    DateTime ObservationUtc, bool IndependentSqlReadback, bool IndependentRestorePrincipalVerified,
    IReadOnlyList<B7BackupPiece> Pieces);

public sealed record B7Inspection(bool AcceptedForOperatorReview, IReadOnlyList<string> Denials)
{
    public bool IsPreprodAccepted => false; // B.9 physical drill, not source-controlled evidence.
}

/// <summary>
/// Evaluates a separately supplied SQL msdb + off-host readback. Strings/flags
/// supplied by a caller are not proof; only an independent observer may attest.
/// The result is NEVER permission to activate a restored tenant.
/// </summary>
public static class B7BackupChain
{
    private static readonly Regex Hex = new("^[0-9a-fA-F]{64}$", RegexOptions.CultureInvariant);

    public static B7Inspection Inspect(B7BackupInventory inventory)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        var reasons = new List<string>();
        void Deny(string code) { if (!reasons.Contains(code)) reasons.Add(code); }

        if (inventory.TenantId == Guid.Empty ||
            inventory.DatabaseName != "FmcgPpT_" + inventory.TenantId.ToString("N") ||
            inventory.DatabaseId == Guid.Empty)
            Deny("TENANT_DATABASE_IDENTITY_INVALID");
        if (inventory.RecoveryModel != "FULL")
            Deny("FULL_RECOVERY_REQUIRED");
        if (inventory.ObservationUtc.Kind != DateTimeKind.Utc)
            Deny("UTC_OBSERVATION_REQUIRED");
        if (!inventory.IndependentSqlReadback || !inventory.IndependentRestorePrincipalVerified)
            Deny("INDEPENDENT_BACKUP_AND_KEY_OBSERVER_REQUIRED");
        if (inventory.Pieces is null || inventory.Pieces.Count == 0)
            return new(false, [.. reasons, "EMPTY_BACKUP_CHAIN"]);

        var pieces = inventory.Pieces.OrderBy(p => p.FinishUtc).ToArray();
        foreach (var p in pieces)
        {
            if (p.Kind == B7BackupKind.Unknown || !Enum.IsDefined(p.Kind) ||
                p.BackupId == Guid.Empty || p.DatabaseName != inventory.DatabaseName ||
                p.DatabaseId != inventory.DatabaseId || p.RecoveryForkId == Guid.Empty ||
                p.FinishUtc.Kind != DateTimeKind.Utc || p.FinishUtc > inventory.ObservationUtc ||
                p.FirstLsn < 0 || p.LastLsn <= p.FirstLsn)
                Deny("BACKUP_HEADER_INVALID");
            if (!p.SqlEncrypted || !p.SqlChecksum || !p.OffHostHashVerified ||
                !p.OffHostDecryptVerified || !Hex.IsMatch(p.OffHostObjectSha256 ?? "") ||
                string.IsNullOrWhiteSpace(p.OffHostObjectId) ||
                string.IsNullOrWhiteSpace(p.KeyVersionId))
                Deny("OFFHOST_ENCRYPTION_CHECKSUM_KEY_PROOF_REQUIRED");
        }

        if (pieces.Select(p => p.BackupId).Distinct().Count() != pieces.Length)
            Deny("DUPLICATE_BACKUP_ID");
        if (pieces.Select(p => p.RecoveryForkId).Distinct().Count() != 1)
            Deny("RECOVERY_FORK_MISMATCH");

        var full = pieces.LastOrDefault(p => p.Kind == B7BackupKind.Full && !p.CopyOnly);
        if (full is null)
            return new(false, [.. reasons, "REGULAR_FULL_BASE_MISSING"]);

        if (inventory.ObservationUtc - full.FinishUtc > TimeSpan.FromHours(24))
            Deny("DAILY_FULL_INTERVAL_EXCEEDED");

        var diffs = pieces.Where(p => p.Kind == B7BackupKind.Differential &&
            p.FinishUtc >= full.FinishUtc).ToArray();
        if (diffs.Any(p => p.CopyOnly || p.DatabaseBackupLsn != full.CheckpointLsn))
            Deny("DIFFERENTIAL_BASE_MISMATCH");
        var priorDifferentialUtc = full.FinishUtc;
        foreach (var diff in diffs)
        {
            if (diff.FinishUtc - priorDifferentialUtc > TimeSpan.FromHours(6))
                Deny("DIFFERENTIAL_SCHEDULE_GAP");
            priorDifferentialUtc = diff.FinishUtc;
        }
        var newestBase = priorDifferentialUtc;
        if (inventory.ObservationUtc - newestBase > TimeSpan.FromHours(6))
            Deny("SIX_HOUR_DIFFERENTIAL_INTERVAL_EXCEEDED");

        var logs = pieces.Where(p => p.Kind == B7BackupKind.Log &&
            p.FinishUtc > full.FinishUtc).ToArray();
        if (inventory.ObservationUtc - (logs.Length == 0 ? full.FinishUtc : logs[^1].FinishUtc)
            > TimeSpan.FromMinutes(5))
            Deny("FIVE_MINUTE_LOG_INTERVAL_EXCEEDED");
        var priorTime = full.FinishUtc;
        var priorLsn = full.LastLsn;
        foreach (var log in logs)
        {
            if (log.CopyOnly || log.FinishUtc - priorTime > TimeSpan.FromMinutes(5))
                Deny("LOG_SCHEDULE_GAP");
            if (log.FirstLsn > priorLsn || log.LastLsn <= priorLsn)
                Deny("LOG_LSN_GAP_OR_REPLAY");
            priorTime = log.FinishUtc;
            priorLsn = log.LastLsn;
        }
        return new(reasons.Count == 0, reasons);
    }
}
