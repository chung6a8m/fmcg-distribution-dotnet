using Dapper;
using FmcgDms.Api.Persistence;
using Microsoft.Data.SqlClient;

namespace FmcgDms.Api.Tests;

internal sealed record RecoverySqlError(int Number, int State, int Severity);
internal sealed record RecoveryException(string Type, RecoverySqlError[] SqlErrors)
{
    internal static RecoveryException From(Exception ex)
    {
        var errors = new List<RecoverySqlError>();
        for (Exception? current = ex; current is not null && errors.Count < 8; current = current.InnerException)
            if (current is SqlException sql)
                errors.AddRange(sql.Errors.Cast<SqlError>().Take(8 - errors.Count).Select(e =>
                    new RecoverySqlError(e.Number, e.State, e.Class)));
        return new(FailureDiagnostics.Label(ex.GetType().Name), errors.ToArray());
    }
}

internal sealed class RecoverySqlState
{
    public Guid? PReceiptId { get; set; }
    public DateTime? PReceivedAtUtc { get; set; }
    public string? PStatus { get; set; }
    public Guid? CReceiptId { get; set; }
    public DateTime? CReceivedAtUtc { get; set; }
    public string? CStatus { get; set; }
    public string? CReasonCode { get; set; }
    public int? TechnicalRetryCount { get; set; }
    public DateTime? NextAttemptAtUtc { get; set; }
    public DateTime? LastAttemptAtUtc { get; set; }
    public int ApplicationCount { get; set; }
    public Guid? AcceptanceId { get; set; }
    public DateTime? AppliedAtUtc { get; set; }
    public int AcceptanceCount { get; set; }
    public int CustodyLegCount { get; set; }
    public int FundLegCount { get; set; }
    public bool? LedgerBalanced { get; set; }
    public int OutboxCount { get; set; }
    public Guid? MessageId { get; set; }
    public string? DeliveryStatus { get; set; }
    public int? DeliveryAttemptCount { get; set; }
    public DateTime? DeliveryNextAttemptAtUtc { get; set; }
    public DateTime? LeaseExpiresAtUtc { get; set; }
    public DateTime? AckAtUtc { get; set; }
    public string? LastErrorCode { get; set; }
    public int ConsumerCount { get; set; }
    public DateTime? ConsumerReceivedAtUtc { get; set; }

    // The only free-form SQL strings selected. Never export arbitrary reason text.
    internal void Redact()
    {
        PStatus = Status(PStatus);
        CStatus = Status(CStatus);
        DeliveryStatus = Status(DeliveryStatus);
        CReasonCode = Reason(CReasonCode);
        LastErrorCode = Reason(LastErrorCode);
    }

    private static string? Status(string? value) => value switch
    {
        null or "RECEIVED" or "PENDING_DEPENDENCY" or "APPLIED" or
            "CASE" or "REJECTED" or "PENDING" or "LEASED" or "ACKED" => value,
        _ => "[redacted]"
    };

    private static string? Reason(string? value) => value switch
    {
        null or "TECHNICAL_RETRY" or "TECHNICAL_RETRIES_EXHAUSTED" or
            "MISSING_PREREQUISITE" or "C_ACTOR_REVOKED" or
            "P_ACTOR_INVALID_OR_REVOKED" or "COUNT_NOT_VERIFIED" or
            "OFFER_ALREADY_ACCEPTED" or "DELIVERY_RETRY" or
            "DELIVERY_RETRIES_EXHAUSTED" => value,
        _ => "[redacted]"
    };
}

internal sealed record RecoverySqlCapture(string Status, DateTimeOffset StartedUtc,
    DateTimeOffset FinishedUtc, RecoverySqlState? State, RecoveryException? Error);
internal sealed record RecoveryProcessState(int Pid, int Port, bool WorkerEnabled,
    DateTimeOffset StartedUtc, DateTimeOffset? StoppedUtc, bool Exited, int? ExitCode);
internal sealed record RecoveryFailureEvidence(string Schema, Guid TripId, string FixtureNonce,
    RecoveryProcessState? PreviousProcess, long ElapsedMs, int PollCount,
    RecoveryException Failure, RecoverySqlCapture Sql);

internal static class RecoveryDiagnostics
{
    internal static async Task<(string? Path, string? Error)> TryCaptureAsync(Func<Task<string>> capture)
    {
        try { return (await capture(), null); }
        catch (Exception ex) { return (null, FailureDiagnostics.Label(ex.GetType().Name)); }
    }

    internal static async Task<RecoverySqlCapture> CaptureSqlAsync(SqlScratchFixture fixture,
        string tenant, Guid trip, Guid offer, App02EventInput p, App02EventInput c,
        CancellationToken ct)
    {
        DateTimeOffset started = DateTimeOffset.UtcNow;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            string database = tenant switch
            {
                "tenant-a" => fixture.TenantADb,
                "tenant-b" => fixture.TenantBDb,
                _ => throw new UnauthorizedAccessException()
            };
            if (!fixture.SqlServer.StartsWith(@"(localdb)\", StringComparison.OrdinalIgnoreCase)
                || p.TripId != trip || c.TripId != trip || p.OfferId != offer
                || p.EventKind != App02EventCanonicalV1.OfferKind
                || c.EventKind != App02EventCanonicalV1.AcceptanceKind
                || c.PrerequisiteProducerId != p.ProducerId || c.PrerequisiteEventId != p.EventId)
                throw new UnauthorizedAccessException();
            deadline.Token.ThrowIfCancellationRequested();
            var target = new SqlConnectionStringBuilder(fixture.ConnectionString(database))
            {
                ConnectTimeout = 2, Pooling = false
            };
            await using var db = new SqlConnection(target.ConnectionString);
            await db.OpenAsync(deadline.Token);
            // Verify exact fixture ownership and tenant before reading any
            // business rows. All commands are read-only, on this connection.
            int owned = await db.ExecuteScalarAsync<int>(new CommandDefinition("""
                SELECT CASE WHEN DB_NAME()=@Database
                  AND EXISTS (SELECT 1 FROM dbo.App00ScratchMarker WHERE Nonce=@Nonce)
                  AND EXISTS (SELECT 1 FROM dbo.AppTenantBinding WHERE Slot=1 AND TenantId=@Tenant)
                  THEN 1 ELSE 0 END
                """, new { Database = database, fixture.Nonce, Tenant = tenant },
                commandTimeout: 2, cancellationToken: deadline.Token));
            if (owned != 1) throw new UnauthorizedAccessException();

            // Always returns a row even if neither receipt exists. Independent
            // LEFT joins distinguish missing stages; no raw event/ledger payload.
            // Read-committed observation, NOT a consistent transaction snapshot
            // across worker commits or an acceptance oracle.
            var state = await db.QuerySingleAsync<RecoverySqlState>(new CommandDefinition("""
                SELECT p.ReceiptId PReceiptId,p.ReceivedAtUtc PReceivedAtUtc,pp.Status PStatus,
                  c.ReceiptId CReceiptId,c.ReceivedAtUtc CReceivedAtUtc,cp.Status CStatus,
                  cp.ReasonCode CReasonCode,CAST(cp.TechnicalRetryCount AS int) TechnicalRetryCount,
                  cp.NextAttemptAtUtc,cp.LastAttemptAtUtc,
                  (SELECT COUNT(*) FROM dbo.App02AcceptanceApplication x
                    WHERE x.TenantId=@Tenant AND x.CReceiptId=c.ReceiptId) ApplicationCount,
                  COALESCE(a.AcceptanceId,ca.Id) AcceptanceId,a.AppliedAtUtc,
                  (SELECT COUNT(*) FROM dbo.CashHandoverAcceptance x
                    WHERE x.TenantId=@Tenant AND x.TripId=@Trip AND x.OfferId=@Offer) AcceptanceCount,
                  (SELECT COUNT(*) FROM dbo.LedgerEffect e WHERE e.TenantId=@Tenant
                    AND e.BusinessSourceLineId=ca.SourceLineId AND e.EffectKey=N'CUSTODY-OUT') CustodyLegCount,
                  (SELECT COUNT(*) FROM dbo.LedgerEffect e WHERE e.TenantId=@Tenant
                    AND e.BusinessSourceLineId=ca.SourceLineId AND e.EffectKey=N'FUND-IN') FundLegCount,
                  CAST(CASE WHEN ca.Id IS NULL THEN NULL
                    WHEN (SELECT COUNT(*) FROM dbo.LedgerEffect e WHERE e.TenantId=@Tenant
                        AND e.BusinessSourceLineId=ca.SourceLineId
                        AND e.EffectKey IN(N'CUSTODY-OUT',N'FUND-IN'))=2
                      AND (SELECT COALESCE(SUM(e.AmountDelta),0) FROM dbo.LedgerEffect e
                        WHERE e.TenantId=@Tenant AND e.BusinessSourceLineId=ca.SourceLineId
                          AND e.EffectKey IN(N'CUSTODY-OUT',N'FUND-IN'))=0
                    THEN 1 ELSE 0 END AS bit) LedgerBalanced,
                  (SELECT COUNT(*) FROM dbo.App02OutboxMessage x
                    WHERE x.TenantId=@Tenant AND x.CReceiptId=c.ReceiptId) OutboxCount,
                  COALESCE(a.MessageId,m.MessageId) MessageId,d.Status DeliveryStatus,
                  CAST(d.AttemptCount AS int) DeliveryAttemptCount,
                  d.NextAttemptAtUtc DeliveryNextAttemptAtUtc,d.LeaseExpiresAtUtc,d.AckAtUtc,d.LastErrorCode,
                  (SELECT COUNT(*) FROM dbo.App02SyntheticConsumerInbox x
                    WHERE x.TenantId=@Tenant AND x.MessageId=COALESCE(a.MessageId,m.MessageId)) ConsumerCount,
                  i.ReceivedAtUtc ConsumerReceivedAtUtc
                FROM (SELECT 1 AS Seed) seed
                LEFT JOIN dbo.App02EventReceipt p ON p.TenantId=@Tenant AND p.TripId=@Trip
                  AND p.ProducerId=@PProducer AND p.EventId=@PEvent
                  AND p.EventKind=N'CashHandoverOffered'
                LEFT JOIN dbo.App02EventProgress pp ON pp.TenantId=p.TenantId AND pp.ReceiptId=p.ReceiptId
                LEFT JOIN dbo.App02EventReceipt c ON c.TenantId=@Tenant AND c.TripId=@Trip
                  AND c.ProducerId=@CProducer AND c.EventId=@CEvent
                  AND c.EventKind=N'CashierAcceptanceRequested'
                LEFT JOIN dbo.App02EventProgress cp ON cp.TenantId=c.TenantId AND cp.ReceiptId=c.ReceiptId
                LEFT JOIN dbo.App02AcceptanceApplication a ON a.TenantId=c.TenantId AND a.CReceiptId=c.ReceiptId
                LEFT JOIN dbo.CashHandoverAcceptance ca ON ca.TenantId=@Tenant AND ca.TripId=@Trip AND ca.OfferId=@Offer
                LEFT JOIN dbo.App02OutboxMessage m ON m.TenantId=c.TenantId AND m.CReceiptId=c.ReceiptId
                LEFT JOIN dbo.App02OutboxDelivery d ON d.TenantId=@Tenant AND d.MessageId=COALESCE(a.MessageId,m.MessageId)
                LEFT JOIN dbo.App02SyntheticConsumerInbox i ON i.TenantId=@Tenant AND i.MessageId=COALESCE(a.MessageId,m.MessageId)
                """, new { Tenant = tenant, Trip = trip, Offer = offer,
                    PProducer = p.ProducerId, PEvent = p.EventId,
                    CProducer = c.ProducerId, CEvent = c.EventId },
                commandTimeout: 2, cancellationToken: deadline.Token));
            state.Redact();
            return new("CAPTURED", started, DateTimeOffset.UtcNow, state, null);
        }
        catch (Exception ex)
        {
            return new("UNAVAILABLE", started, DateTimeOffset.UtcNow, null, RecoveryException.From(ex));
        }
    }
}
