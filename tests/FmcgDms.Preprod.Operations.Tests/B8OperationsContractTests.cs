using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using System.Text;
using FmcgDms.Preprod.Operations;
using Xunit;

namespace FmcgDms.Preprod.Operations.Tests;

/// <summary>6 synthetic source-contract tests map B04-01..06; NOT six real B.1 cases.</summary>
public sealed class B8OperationsContractTests
{
    private static readonly DateTime Now = new(2026, 11, 18, 12, 0, 0, DateTimeKind.Utc);
    private static readonly string Hash = new('a', 64);

    private static B8AuditEntry Next(B8AuditEntry? previous, Guid tenant)
    {
        var seq = (previous?.Sequence ?? 0) + 1;
        var e = new B8AuditEntry(tenant, seq, Guid.NewGuid(), B8AuditKind.DurableIntake,
            SHA256.HashData(Encoding.UTF8.GetBytes("ci-sql-identity")),
            Guid.NewGuid(), Guid.NewGuid(), null, Guid.NewGuid(),
            SHA256.HashData(Encoding.UTF8.GetBytes("redacted-evidence")),
            previous?.EntrySha256 ?? new byte[32], new byte[32]);
        return e with { EntrySha256 = B8AuditVerifier.ComputeEntryHash(e) };
    }

    [Fact]
    public void B04_01_corrupted_removed_reordered_or_wrong_tenant_chain_is_denied_even_with_valid_signature()
    {
        var tenant = Guid.NewGuid();
        var one = Next(null, tenant);
        var two = Next(one, tenant);
        var anchor = new B8AuditAnchor(tenant, 2, two.EntrySha256, Guid.NewGuid(), "ci-key-1", []);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var digest = B8AuditVerifier.ComputeAnchorDigest(anchor);
        anchor = anchor with
        {
            Signature = key.SignHash(digest, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)
        };
        var receipt = new B8AnchorReceipt(anchor.AnchorId, digest, "ci/offhost/immutable-anchor",
            Now, true);
        var pub = key.ExportSubjectPublicKeyInfo();
        Assert.True(B8AuditVerifier.Verify([one, two], anchor, receipt, pub).CryptographicallyConsistent);
        Assert.False(B8AuditVerifier.Verify([one, two], anchor, null, pub).CryptographicallyConsistent);
        Assert.Contains("B8_CHAIN_HASH_TAMPERED",
            B8AuditVerifier.Verify([one with { PayloadSha256 = new byte[32] }, two],
                anchor, receipt, pub).Denials);
        Assert.Contains("B8_CHAIN_TAIL_OR_EXTERNAL_ANCHOR_MISMATCH",
            B8AuditVerifier.Verify([one], anchor, receipt, pub).Denials);
        Assert.Contains("B8_SEQUENCE_TENANT_OR_LINEAGE_INVALID",
            B8AuditVerifier.Verify([two, one], anchor, receipt, pub).Denials);
        Assert.Contains("B8_SEQUENCE_TENANT_OR_LINEAGE_INVALID",
            B8AuditVerifier.Verify([one with { TenantId = Guid.NewGuid() }, two],
                anchor, receipt, pub).Denials);
        using var wrongKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.Contains("B8_ANCHOR_SIGNATURE_INVALID",
            B8AuditVerifier.Verify([one, two], anchor, receipt,
                wrongKey.ExportSubjectPublicKeyInfo()).Denials);
        Assert.False(B8AuditVerifier.Verify([one, two], anchor, receipt, pub).IsPhysicalPreprodVerified);
    }

    [Fact]
    public void B04_02_correlated_activity_and_meter_emit_only_allowlisted_identifiers()
    {
        var captured = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == B8Telemetry.InstrumentationName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = captured.Add
        };
        ActivitySource.AddActivityListener(listener);
        var metrics = new List<string>();
        using var meter = new MeterListener
        {
            InstrumentPublished = (instrument, ml) =>
            {
                if (instrument.Meter.Name == B8Telemetry.InstrumentationName)
                    ml.EnableMeasurementEvents(instrument);
            }
        };
        meter.SetMeasurementEventCallback<long>((instrument, _, _, _) => metrics.Add(instrument.Name));
        meter.SetMeasurementEventCallback<double>((instrument, _, _, _) => metrics.Add(instrument.Name));
        meter.Start();
        var tenant = Guid.NewGuid();
        var correlation = Guid.NewGuid();
        var sourceId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        using (B8Telemetry.Start(B8AuditKind.Inbox, tenant, correlation, sourceId, messageId))
        {
            B8Telemetry.RecordSecurityDeny(tenant, correlation);
            B8Telemetry.RecordWorkerBacklog(21);
        }
        Assert.Single(captured);
        var tags = captured[0].TagObjects.ToDictionary(k => k.Key, v => v.Value?.ToString());
        Assert.Equal(tenant.ToString("D"), tags["fmcg.tenant_id"]);
        Assert.Equal(correlation.ToString("D"), tags["fmcg.correlation_id"]);
        Assert.Equal(sourceId.ToString("D"), tags["fmcg.source_id"]);
        Assert.Equal(messageId.ToString("D"), tags["fmcg.message_id"]);
        Assert.Equal(5, tags.Count);
        Assert.Contains("fmcg.preprod.audit.events", metrics);
        Assert.Contains("fmcg.preprod.security.denials", metrics);
        Assert.Contains("fmcg.preprod.worker.backlog", metrics);
        Assert.Throws<ArgumentException>(() => B8Telemetry.Start(
            B8AuditKind.Case, Guid.Empty, correlation));
        Assert.Throws<ArgumentOutOfRangeException>(() => B8Telemetry.RecordWorkerBacklog(-1));
    }

    [Fact]
    public void B04_03_lost_alert_delivery_or_late_ack_never_passes()
    {
        var a = new B8AlertWitness(Guid.NewGuid(), B8AlertType.WorkerBacklog,
            Guid.NewGuid(), "operator-alert-v1", Now, Now.AddMinutes(10),
            TimeSpan.FromMinutes(5), Guid.NewGuid(), Guid.NewGuid(),
            Now.AddSeconds(5), Now.AddSeconds(30));
        Assert.True(B8AlertVerifier.Inspect(a).AcceptedForOperatorReview);
        Assert.Contains("B8_ALERT_DELIVERY_NOT_PROVEN",
            B8AlertVerifier.Inspect(a with { IndependentlyReceivedUtc = null }).Denials);
        Assert.Contains("B8_ALERT_ACK_NOT_PROVEN",
            B8AlertVerifier.Inspect(a with { IndependentlyAcknowledgedUtc = null }).Denials);
        Assert.Contains("B8_ALERT_ESCALATION_REQUIRED",
            B8AlertVerifier.Inspect(a with
            { IndependentlyAcknowledgedUtc = Now.AddMinutes(8) }).Denials);
        Assert.False(B8AlertVerifier.Inspect(a).IsPhysicalPreprodVerified);
    }

    [Fact]
    public void B04_04_incident_requires_owner_handoff_timed_escalation_and_independent_closeout()
    {
        var incident = Guid.NewGuid();
        var owner = Guid.NewGuid();
        var reviewer = Guid.NewGuid();
        var entries = new List<B8IncidentEvent>
        {
            new(incident, B8IncidentAction.Opened, Guid.NewGuid(), Now, "runbook-1"),
            new(incident, B8IncidentAction.Assigned, owner, Now.AddMinutes(1), "runbook-1"),
            new(incident, B8IncidentAction.HandoffAcknowledged, owner, Now.AddMinutes(2), "runbook-1"),
            new(incident, B8IncidentAction.Escalated, owner, Now.AddMinutes(4), "runbook-1"),
            new(incident, B8IncidentAction.Mitigated, owner, Now.AddMinutes(7), "runbook-1"),
            new(incident, B8IncidentAction.Closed, reviewer, Now.AddMinutes(9), "runbook-1")
        };
        var witness = new B8IncidentEvidence(incident, owner, reviewer,
            "incident-severity-v1", TimeSpan.FromMinutes(5), Now.AddMinutes(10), entries);
        Assert.True(B8IncidentVerifier.Inspect(witness).AcceptedForOperatorReview);
        Assert.Contains("B8_INCIDENT_HANDOFF_NOT_PROVEN",
            B8IncidentVerifier.Inspect(witness with
            { Timeline = entries.Where(x => x.Action != B8IncidentAction.HandoffAcknowledged).ToList() }).Denials);
        Assert.Contains("B8_INCIDENT_ESCALATION_MISSED",
            B8IncidentVerifier.Inspect(witness with
            { Timeline = entries.Where(x => x.Action != B8IncidentAction.Escalated).ToList() }).Denials);
        Assert.Contains("B8_INCIDENT_CLOSEOUT_REVIEW_INVALID",
            B8IncidentVerifier.Inspect(witness with
            { Timeline = entries.SkipLast(1).Append(entries[^1] with { ActorId = owner }).ToList() }).Denials);
    }

    private static B8RetentionRequest Retention() => new(
        Guid.NewGuid(), B8EvidenceClass.DiagnosticTelemetry, B8Disposition.Purge,
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "approved-classification-v1",
        Now, Now.AddDays(-1), true, true, false, true, true, true, Hash, Hash);

    [Fact]
    public void B04_05_legal_hold_self_approval_missing_authority_or_manifest_denies()
    {
        var valid = Retention();
        Assert.True(B8RetentionGate.Inspect(valid).EligibleForSeparateOperatorAction);
        Assert.Contains("B8_ACTIVE_LEGAL_HOLD",
            B8RetentionGate.Inspect(valid with { LegalHoldActive = true }).Denials);
        Assert.Contains("B8_APPROVAL_OR_CLASSIFICATION_MISSING",
            B8RetentionGate.Inspect(valid with { CheckerId = valid.MakerId }).Denials);
        Assert.Contains("B8_APPROVAL_OR_CLASSIFICATION_MISSING",
            B8RetentionGate.Inspect(valid with { LegalAuthorityOnline = false }).Denials);
        Assert.Contains("B8_MANIFEST_OR_PEER_PROOF_REQUIRED",
            B8RetentionGate.Inspect(valid with { PeerUnchanged = false }).Denials);
    }

    [Fact]
    public void B04_06_only_explicitly_eligible_diagnostics_are_candidates_for_separate_purge()
    {
        var valid = Retention();
        var candidate = B8RetentionGate.Inspect(valid);
        Assert.True(candidate.EligibleForSeparateOperatorAction);
        Assert.False(candidate.PerformsDeletion);
        Assert.False(candidate.IsPhysicalPreprodVerified);
        foreach (var protectedClass in Enum.GetValues<B8EvidenceClass>()
                     .Where(c => c != B8EvidenceClass.DiagnosticTelemetry))
        {
            var decision = B8RetentionGate.Inspect(valid with { EvidenceClass = protectedClass });
            Assert.Contains("B8_DURABLE_OR_AUDIT_PURGE_FORBIDDEN", decision.Denials);
        }
        Assert.Contains("B8_RETENTION_UNSET_OR_NOT_ELIGIBLE",
            B8RetentionGate.Inspect(valid with { ExplicitRetainUntilUtc = null }).Denials);
        Assert.Contains("B8_RETENTION_UNSET_OR_NOT_ELIGIBLE",
            B8RetentionGate.Inspect(valid with { ExplicitRetainUntilUtc = Now.AddMinutes(1) }).Denials);
        Assert.Contains("B8_IMMUTABLE_ARCHIVE_RECEIPT_MISSING",
            B8RetentionGate.Inspect(valid with { ApprovedImmutableArchiveExists = false }).Denials);
        Assert.Contains("B8_MANIFEST_OR_PEER_PROOF_REQUIRED",
            B8RetentionGate.Inspect(valid with
            {
                Disposition = B8Disposition.Archive,
                BeforeManifestSha256 = Hash,
                AfterManifestSha256 = new('b', 64)
            }).Denials);
    }
}
