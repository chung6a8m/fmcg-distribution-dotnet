using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace FmcgDms.Preprod.Operations;

/// <summary>B.8 operator-side only: no PREPROD API/Worker registration or business dispatch.</summary>
public static class B8Schema
{
    public const string Version = "B8_20261118_001";
}

public enum B8AuditKind : short
{
    SecurityDeny = 1, Identity = 2, TenantLifecycle = 3,
    PolicyDecision = 4, DurableIntake = 5, Inbox = 6,
    Outbox = 7, Case = 8, Recovery = 9,
    Alert = 10, Incident = 11, Retention = 12
}

/// <summary>
/// Source lineage is opaque IDs and a digest of ALREADY REDACTED data. No raw
/// token, refresh grant, connection string, policy value, or customer PII.
/// PrincipalSha256 is bound by SQL to the authenticated database login.
/// Nullable lineage IDs encode as 16 zero bytes in chain version 1.
/// </summary>
public sealed record B8AuditEntry(
    Guid TenantId, long Sequence, Guid EntryId, B8AuditKind Kind,
    byte[] PrincipalSha256, Guid CorrelationId, Guid? SourceId,
    Guid? PolicyVersionId, Guid? MessageId, byte[] PayloadSha256,
    byte[] PreviousSha256, byte[] EntrySha256);

/// <summary>
/// An externally signed checkpoint. Signature must be produced by an independent
/// key outside the SQL writer/DBA trust boundary, not by the mutable audit DB.
/// </summary>
public sealed record B8AuditAnchor(Guid TenantId, long LastSequence,
    byte[] LastHash, Guid AnchorId, string KeyVersion, byte[] Signature);

/// <summary>
/// This DTO is not proof of storage or custody. PREPROD G-B04 needs an actual
/// independently fetched, immutable off-host anchor and key ownership evidence.
/// </summary>
public sealed record B8AnchorReceipt(Guid AnchorId, byte[] SignedPayloadSha256,
    string IndependentObjectRef, DateTime ObservedUtc, bool RetrievedOutsideAuditDatabase);

public sealed record B8AuditInspection(bool CryptographicallyConsistent,
    IReadOnlyList<string> Denials)
{
    public bool IsPhysicalPreprodVerified => false;
}

public static class B8AuditVerifier
{
    private static readonly byte[] Zero = new byte[32];
    private static readonly byte[] Domain = Encoding.ASCII.GetBytes("APP02B8:EXTERNAL-ANCHOR:V1");

    public static byte[] ComputeEntryHash(B8AuditEntry e)
    {
        ArgumentNullException.ThrowIfNull(e);
        Require32(e.PreviousSha256);
        Require32(e.PrincipalSha256);
        Require32(e.PayloadSha256);
        using var buffer = new MemoryStream(202);
        buffer.Write(e.PreviousSha256);
        WriteGuid(buffer, e.TenantId);
        WriteLong(buffer, e.Sequence);
        WriteGuid(buffer, e.EntryId);
        Span<byte> kind = stackalloc byte[2];
        BinaryPrimitives.WriteInt16BigEndian(kind, (short)e.Kind);
        buffer.Write(kind);
        buffer.Write(e.PrincipalSha256);
        WriteGuid(buffer, e.CorrelationId);
        WriteGuid(buffer, e.SourceId ?? Guid.Empty);
        WriteGuid(buffer, e.PolicyVersionId ?? Guid.Empty);
        WriteGuid(buffer, e.MessageId ?? Guid.Empty);
        buffer.Write(e.PayloadSha256);
        return SHA256.HashData(buffer.ToArray());
    }

    public static byte[] ComputeAnchorDigest(B8AuditAnchor a)
    {
        ArgumentNullException.ThrowIfNull(a);
        Require32(a.LastHash);
        if (a.TenantId == Guid.Empty || a.AnchorId == Guid.Empty ||
            a.LastSequence < 1 || string.IsNullOrWhiteSpace(a.KeyVersion) ||
            a.KeyVersion.Length > 64 ||
            a.KeyVersion.Any(ch => !(char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.')))
            throw new ArgumentException("B8_ANCHOR_ENVELOPE_DENIED");
        using var buffer = new MemoryStream();
        buffer.Write(Domain);
        WriteGuid(buffer, a.TenantId);
        WriteLong(buffer, a.LastSequence);
        buffer.Write(a.LastHash);
        WriteGuid(buffer, a.AnchorId);
        buffer.Write(Encoding.ASCII.GetBytes(a.KeyVersion));
        return SHA256.HashData(buffer.ToArray());
    }

    public static B8AuditInspection Verify(IReadOnlyList<B8AuditEntry> rows,
        B8AuditAnchor anchor, B8AnchorReceipt? receipt, ReadOnlySpan<byte> publicKeySpki)
    {
        var errors = new List<string>();
        void Deny(string code) { if (!errors.Contains(code)) errors.Add(code); }

        if (rows is null || rows.Count == 0 || rows.Count > 100_000)
            Deny("B8_AUDIT_ROWS_REQUIRED");
        if (anchor is null || anchor.LastHash is not { Length: 32 } ||
            anchor.TenantId == Guid.Empty || anchor.LastSequence < 1 ||
            anchor.AnchorId == Guid.Empty || anchor.Signature is not { Length: > 0 })
            Deny("B8_EXTERNAL_ANCHOR_REQUIRED");
        if (receipt is null || !receipt.RetrievedOutsideAuditDatabase ||
            string.IsNullOrWhiteSpace(receipt.IndependentObjectRef) ||
            receipt.AnchorId != anchor?.AnchorId ||
            receipt.ObservedUtc.Kind != DateTimeKind.Utc ||
            receipt.SignedPayloadSha256 is not { Length: 32 })
            Deny("B8_INDEPENDENT_RECEIPT_REQUIRED");

        if (!errors.Contains("B8_EXTERNAL_ANCHOR_REQUIRED"))
        {
            try
            {
                var validatedAnchor = anchor!;
                var digest = ComputeAnchorDigest(validatedAnchor);
                if (receipt?.SignedPayloadSha256 is not { Length: 32 } ||
                    !CryptographicOperations.FixedTimeEquals(digest, receipt.SignedPayloadSha256))
                    Deny("B8_ANCHOR_RECEIPT_MISMATCH");
                using var publicKey = ECDsa.Create();
                publicKey.ImportSubjectPublicKeyInfo(publicKeySpki, out _);
                if (!publicKey.VerifyHash(digest, validatedAnchor.Signature,
                    DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
                    Deny("B8_ANCHOR_SIGNATURE_INVALID");
            }
            catch (Exception ex) when (ex is ArgumentException or CryptographicException)
            {
                Deny("B8_ANCHOR_SIGNATURE_INVALID");
            }
        }

        if (rows is { Count: > 0 and <= 100_000 } && anchor is not null)
        {
            byte[] previous = Zero;
            var entryIds = new HashSet<Guid>();
            long next = 1;
            foreach (var e in rows)
            {
                if (e is null || e.TenantId != anchor.TenantId ||
                    e.Sequence != next || e.EntryId == Guid.Empty ||
                    e.CorrelationId == Guid.Empty ||
                    !Enum.IsDefined(e.Kind) ||
                    !entryIds.Add(e.EntryId))
                {
                    Deny("B8_SEQUENCE_TENANT_OR_LINEAGE_INVALID");
                    break;
                }
                if (e.PreviousSha256 is not { Length: 32 } ||
                    e.EntrySha256 is not { Length: 32 } ||
                    e.PrincipalSha256 is not { Length: 32 } ||
                    e.PayloadSha256 is not { Length: 32 } ||
                    !CryptographicOperations.FixedTimeEquals(previous, e.PreviousSha256))
                {
                    Deny("B8_CHAIN_PREVIOUS_OR_FIELD_HASH_INVALID");
                    break;
                }
                if (!CryptographicOperations.FixedTimeEquals(
                    ComputeEntryHash(e), e.EntrySha256))
                {
                    Deny("B8_CHAIN_HASH_TAMPERED");
                    break;
                }
                previous = e.EntrySha256;
                next++;
            }
            if (next - 1 != anchor.LastSequence ||
                !CryptographicOperations.FixedTimeEquals(previous, anchor.LastHash))
                Deny("B8_CHAIN_TAIL_OR_EXTERNAL_ANCHOR_MISMATCH");
        }
        return new(errors.Count == 0, errors);
    }

    private static void Require32(byte[]? bytes)
    {
        if (bytes is not { Length: 32 })
            throw new ArgumentException("B8_SHA256_LENGTH_DENIED");
    }

    // SQL Server CONVERT(binary(16), uniqueidentifier) uses the native mixed-endian
    // byte layout, equivalent to Guid.ToByteArray() (NOT RFC 4122 big-endian).
    // The two-tenant LocalDB test independently compares real SQL hashes.
    private static void WriteGuid(Stream output, Guid guid) =>
        output.Write(guid.ToByteArray());

    private static void WriteLong(Stream output, long value)
    {
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(bytes, value);
        output.Write(bytes);
    }
}
