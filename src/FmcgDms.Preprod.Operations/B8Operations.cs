using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace FmcgDms.Preprod.Operations;

/// <summary>
/// OTel-compatible instrumentation source only. An exporter, independent
/// collector, alert rule and real API/Worker/SQL binding are NOT configured.
/// Only typed IDs and allowlisted codes enter telemetry; no arbitrary payloads.
/// </summary>
public static class B8Telemetry
{
    public const string InstrumentationName = "FmcgDms.Preprod.Operations";
    public static readonly ActivitySource Source = new(InstrumentationName, "1.0.0");
    public static readonly Meter Meter = new(InstrumentationName, "1.0.0");
    private static readonly Counter<long> Events =
        Meter.CreateCounter<long>("fmcg.preprod.audit.events");
    private static readonly Counter<long> Denials =
        Meter.CreateCounter<long>("fmcg.preprod.security.denials");
    private static readonly Histogram<double> Backlog =
        Meter.CreateHistogram<double>("fmcg.preprod.worker.backlog", "items");

    public static Activity? Start(B8AuditKind kind, Guid tenantId, Guid correlationId,
        Guid? sourceId = null, Guid? messageId = null)
    {
        Require(kind, tenantId, correlationId);
        var activity = Source.StartActivity("preprod.audit.transition", ActivityKind.Internal);
        activity?.SetTag("fmcg.kind", kind.ToString());
        activity?.SetTag("fmcg.tenant_id", tenantId.ToString("D"));
        activity?.SetTag("fmcg.correlation_id", correlationId.ToString("D"));
        if (sourceId is { } src && src != Guid.Empty)
            activity?.SetTag("fmcg.source_id", src.ToString("D"));
        if (messageId is { } msg && msg != Guid.Empty)
            activity?.SetTag("fmcg.message_id", msg.ToString("D"));
        Events.Add(1, new KeyValuePair<string, object?>("kind", kind.ToString()));
        return activity;
    }

    public static void RecordSecurityDeny(Guid tenantId, Guid correlationId)
    {
        Require(B8AuditKind.SecurityDeny, tenantId, correlationId);
        Denials.Add(1); // no tenant/user IDs in high-cardinality metric labels
    }

    public static void RecordWorkerBacklog(int count)
    {
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
        Backlog.Record(count);
    }

    private static void Require(B8AuditKind kind, Guid tenantId, Guid correlationId)
    {
        if (!Enum.IsDefined(kind) || tenantId == Guid.Empty || correlationId == Guid.Empty)
            throw new ArgumentException("B8_UNTRUSTED_TELEMETRY_ENVELOPE");
    }
}

public enum B8AlertType { CrossTenantDeny = 1, RefreshReplay = 2, WorkerBacklog = 3, RecoveryWindowExceeded = 4, AuditTamper = 5 }
public sealed record B8AlertWitness(
    Guid AlertId, B8AlertType Type, Guid CorrelationId, string ConfigVersion,
    DateTime TriggeredUtc, DateTime ObservationUtc, TimeSpan ApprovedAckDeadline,
    Guid DestinationId, Guid ResponsibleOwnerId,
    DateTime? IndependentlyReceivedUtc, DateTime? IndependentlyAcknowledgedUtc);

public sealed record B8OperationsInspection(bool AcceptedForOperatorReview,
    IReadOnlyList<string> Denials)
{
    public bool IsPhysicalPreprodVerified => false;
}

public static class B8AlertVerifier
{
    public static B8OperationsInspection Inspect(B8AlertWitness a)
    {
        ArgumentNullException.ThrowIfNull(a);
        var errors = new List<string>();
        void Deny(string code) { if (!errors.Contains(code)) errors.Add(code); }
        if (a.AlertId == Guid.Empty || !Enum.IsDefined(a.Type) ||
            a.CorrelationId == Guid.Empty || a.DestinationId == Guid.Empty ||
            a.ResponsibleOwnerId == Guid.Empty ||
            string.IsNullOrWhiteSpace(a.ConfigVersion) ||
            a.ConfigVersion.Length > 96 ||
            a.ConfigVersion.Any(ch => !(char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-' or '.')) ||
            a.TriggeredUtc.Kind != DateTimeKind.Utc ||
            a.ObservationUtc.Kind != DateTimeKind.Utc ||
            a.ObservationUtc < a.TriggeredUtc ||
            a.ApprovedAckDeadline <= TimeSpan.Zero)
            Deny("B8_ALERT_CONFIG_OR_IDENTITY_MISSING");
        if (a.IndependentlyReceivedUtc is not { Kind: DateTimeKind.Utc } recv ||
            recv < a.TriggeredUtc || recv > a.ObservationUtc)
            Deny("B8_ALERT_DELIVERY_NOT_PROVEN");
        if (a.IndependentlyAcknowledgedUtc is not { Kind: DateTimeKind.Utc } ack ||
            a.IndependentlyReceivedUtc is null || ack < a.IndependentlyReceivedUtc ||
            ack > a.ObservationUtc)
            Deny("B8_ALERT_ACK_NOT_PROVEN");
        if (a.TriggeredUtc.Kind == DateTimeKind.Utc &&
            a.ObservationUtc.Kind == DateTimeKind.Utc &&
            a.ApprovedAckDeadline > TimeSpan.Zero &&
            a.ObservationUtc - a.TriggeredUtc > a.ApprovedAckDeadline &&
            (a.IndependentlyAcknowledgedUtc is null ||
             a.IndependentlyAcknowledgedUtc - a.TriggeredUtc > a.ApprovedAckDeadline))
            Deny("B8_ALERT_ESCALATION_REQUIRED");
        return new(errors.Count == 0, errors);
    }
}

public enum B8IncidentAction { Opened = 1, Assigned = 2, HandoffAcknowledged = 3, Escalated = 4, Mitigated = 5, Closed = 6 }
public sealed record B8IncidentEvent(Guid IncidentId, B8IncidentAction Action,
    Guid ActorId, DateTime AtUtc, string RunbookVersion);
public sealed record B8IncidentEvidence(Guid IncidentId, Guid OwnerId, Guid ReviewerId,
    string SeverityAuthorityVersion, TimeSpan ApprovedEscalationAfter,
    DateTime ObservedUtc, IReadOnlyList<B8IncidentEvent> Timeline);

public static class B8IncidentVerifier
{
    public static B8OperationsInspection Inspect(B8IncidentEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        var errors = new List<string>();
        void Deny(string code) { if (!errors.Contains(code)) errors.Add(code); }
        var t = evidence.Timeline;
        if (evidence.IncidentId == Guid.Empty || evidence.OwnerId == Guid.Empty ||
            evidence.ReviewerId == Guid.Empty || evidence.OwnerId == evidence.ReviewerId ||
            string.IsNullOrWhiteSpace(evidence.SeverityAuthorityVersion) ||
            evidence.ApprovedEscalationAfter <= TimeSpan.Zero ||
            evidence.ObservedUtc.Kind != DateTimeKind.Utc || t is not { Count: >= 3 })
            return new(false, ["B8_INCIDENT_AUTHORITY_OR_TIMELINE_MISSING"]);
        var first = t[0];
        if (first.Action != B8IncidentAction.Opened ||
            t.Any(e => e.IncidentId != evidence.IncidentId ||
                e.ActorId == Guid.Empty || e.AtUtc.Kind != DateTimeKind.Utc ||
                string.IsNullOrWhiteSpace(e.RunbookVersion)) ||
            t.Zip(t.Skip(1)).Any(p => p.First.AtUtc >= p.Second.AtUtc) ||
            t[^1].AtUtc > evidence.ObservedUtc)
            Deny("B8_INCIDENT_TIMELINE_INVALID");
        // A handoff acknowledgement is not evidence of an assignment if it
        // precedes that assignment. Duplicate openings/closeouts are invalid.
        var assignedIndex = -1;
        var handoffIndex = -1;
        for (var i = 0; i < t.Count; i++)
        {
            if (assignedIndex < 0 && t[i].Action == B8IncidentAction.Assigned &&
                t[i].ActorId == evidence.OwnerId)
                assignedIndex = i;
            if (handoffIndex < 0 && t[i].Action == B8IncidentAction.HandoffAcknowledged &&
                t[i].ActorId == evidence.OwnerId)
                handoffIndex = i;
        }
        if (assignedIndex < 0 || handoffIndex <= assignedIndex)
            Deny("B8_INCIDENT_HANDOFF_NOT_PROVEN");
        if (t.Skip(1).Any(e => e.Action == B8IncidentAction.Opened) ||
            t.Count(e => e.Action == B8IncidentAction.Closed) > 1)
            Deny("B8_INCIDENT_TIMELINE_INVALID");
        var closed = t.LastOrDefault(e => e.Action == B8IncidentAction.Closed);
        if (closed is not null &&
            (closed != t[^1] || closed.ActorId != evidence.ReviewerId ||
             !t.Any(e => e.Action == B8IncidentAction.Mitigated &&
                         e.AtUtc < closed.AtUtc)))
            Deny("B8_INCIDENT_CLOSEOUT_REVIEW_INVALID");
        if (first.AtUtc.Kind == DateTimeKind.Utc &&
            evidence.ObservedUtc - first.AtUtc > evidence.ApprovedEscalationAfter &&
            !t.Any(e => e.Action == B8IncidentAction.Escalated &&
                        e.AtUtc <= first.AtUtc + evidence.ApprovedEscalationAfter) &&
            (closed is null || closed.AtUtc > first.AtUtc + evidence.ApprovedEscalationAfter))
            Deny("B8_INCIDENT_ESCALATION_MISSED");
        return new(errors.Count == 0, errors);
    }
}
