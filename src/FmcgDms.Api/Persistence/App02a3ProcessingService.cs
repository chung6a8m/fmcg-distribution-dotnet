using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using FmcgDms.Api.Tenancy;
using Microsoft.Data.SqlClient;

namespace FmcgDms.Api.Persistence;

/// <summary>
/// APP-02A.3 synthetic nonproduction worker. APP-01D remains the only
/// acceptance/source/ledger authority. An intake COMMIT always precedes this
/// independent Serializable apply transaction; only the exact named P is valid.
/// </summary>
public sealed class App02a3ProcessingService(TenantConnections connections)
{
    public const int MaxTechnicalRetries = 6;
    public static readonly TimeSpan SafetyScanInterval = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan MissingDependencyWindow = TimeSpan.FromHours(24);

    private sealed class CRow
    {
        public Guid ReceiptId { get; set; }
        public string TenantId { get; set; } = "";
        public string ProducerId { get; set; } = "";
        public string EventId { get; set; } = "";
        public string EventKind { get; set; } = "";
        public string ActorId { get; set; } = "";
        public Guid TripId { get; set; }
        public string? PrerequisiteProducerId { get; set; }
        public string? PrerequisiteEventId { get; set; }
        public string? CountEvidenceRef { get; set; }
        public string Status { get; set; } = "";
        public string? ReasonCode { get; set; }
        public DateTime? NextAttemptAtUtc { get; set; }
        public Guid? ActiveCaseId { get; set; }
        public DateTime ReceivedAtUtc { get; set; }
        public byte TechnicalRetryCount { get; set; }
    }

    private sealed class PRow
    {
        public Guid ReceiptId { get; set; }
        public Guid OfferId { get; set; }
        public decimal OfferedAmount { get; set; }
        public string ActorId { get; set; } = "";
        public DateTime ReceivedAtUtc { get; set; }
    }

    private sealed class CountRow
    {
        public Guid TripId { get; set; }
        public decimal CountedAmount { get; set; }
        public string CountedBy { get; set; } = "";
        public string VerificationStatus { get; set; } = "";
    }

    public async Task<string> ProcessAsync(string tenant, Guid receiptId,
        DateTimeOffset asOfUtc, CancellationToken ct)
    {
        // The only callers are the in-process synthetic worker and guarded
        // integration tests. There is deliberately no user-facing "apply" API.
        var worker = App02a3Sql.WorkerPrincipal(tenant);
        await using SqlConnection db =
            await connections.OpenSyntheticScratchAsync(worker, ct);
        await using SqlTransaction tx =
            (SqlTransaction)await db.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        try
        {
            var c = await db.QuerySingleOrDefaultAsync<CRow>(App02a3Sql.Cmd("""
                SELECT r.ReceiptId,r.TenantId,r.ProducerId,r.EventId,r.EventKind,
                    r.ActorId,r.TripId,r.PrerequisiteProducerId,r.PrerequisiteEventId,
                    r.CountEvidenceRef,r.ReceivedAtUtc,p.Status,p.ReasonCode,
                    p.NextAttemptAtUtc,p.ActiveCaseId,p.TechnicalRetryCount
                FROM dbo.App02EventReceipt r WITH(UPDLOCK,HOLDLOCK)
                JOIN dbo.App02EventProgress p WITH(UPDLOCK,HOLDLOCK)
                    ON p.TenantId=r.TenantId AND p.ReceiptId=r.ReceiptId
                WHERE r.TenantId=@Tenant AND r.ReceiptId=@Receipt
                """, new { Tenant = tenant, Receipt = receiptId }, tx, ct));
            if (c is null)
            {
                await tx.CommitAsync(ct);
                return "UNKNOWN";
            }
            if (c.EventKind != App02EventCanonicalV1.AcceptanceKind
                || c.Status is "APPLIED" or "REJECTED" or "CASE")
            {
                await tx.CommitAsync(ct);
                return c.Status;
            }
            // The direct processor is also due-fenced; a concurrent wake-up or
            // an aged receipt must never bypass exponential technical backoff.
            if (c.ReasonCode == "TECHNICAL_RETRY"
                && (c.NextAttemptAtUtc is null
                    || c.NextAttemptAtUtc.Value > asOfUtc.UtcDateTime))
            {
                await tx.CommitAsync(ct);
                return "RETRY_SCHEDULED";
            }

            // Serialize with APP-01C T3/T4 and APP-01D T5/T6 as well as the
            // competing C receipts for the same trip. Same lock key/owner.
            await App02a3Sql.Lock(db, tx,
                "APP01C:TRIP:" + tenant + ":" + c.TripId.ToString("N"), ct);

            bool cashierActive = await RoleActive(db, tx, tenant, c.ActorId, "cashier", ct);
            if (!cashierActive)
            {
                await OpenCase(db, tx, c, asOfUtc, "C_ACTOR_REVOKED", ct);
                await tx.CommitAsync(ct);
                return "CASE";
            }

            PRow? p = await db.QuerySingleOrDefaultAsync<PRow>(App02a3Sql.Cmd("""
                SELECT r.ReceiptId,r.OfferId,r.OfferedAmount,r.ActorId,r.ReceivedAtUtc
                FROM dbo.App02EventReceipt r WITH(UPDLOCK,HOLDLOCK)
                JOIN dbo.App02EventProgress progress WITH(HOLDLOCK)
                    ON progress.TenantId=r.TenantId AND progress.ReceiptId=r.ReceiptId
                JOIN dbo.CashHandoverOffer o WITH(HOLDLOCK)
                    ON o.TenantId=r.TenantId AND o.Id=r.OfferId
                    AND o.TripId=r.TripId AND o.OfferedAmount=r.OfferedAmount
                    AND o.OfferedBy COLLATE Latin1_General_100_BIN2
                        = r.ActorId COLLATE Latin1_General_100_BIN2
                JOIN dbo.BusinessSource s WITH(HOLDLOCK)
                    ON s.TenantId=o.TenantId AND s.Id=o.Id
                    AND s.SourceKind=N'CASH_HANDOVER_OFFER'
                JOIN dbo.BusinessCommand b WITH(HOLDLOCK)
                    ON b.TenantId=s.TenantId AND b.Id=s.BusinessCommandId
                    AND b.Status=N'APPLIED' AND b.OutcomeCode=N'CASH_HANDOVER_OFFERED'
                WHERE r.TenantId=@Tenant AND r.ProducerId=@Producer
                    AND r.EventId=@EventId AND r.TripId=@Trip
                    AND r.EventKind=N'CashHandoverOffered' AND progress.Status=N'APPLIED'
                """, new { Tenant = tenant, Producer = c.PrerequisiteProducerId,
                    EventId = c.PrerequisiteEventId, Trip = c.TripId }, tx, ct));
            if (p is null)
            {
                if (asOfUtc.UtcDateTime - c.ReceivedAtUtc >= MissingDependencyWindow)
                {
                    await OpenCase(db, tx, c, asOfUtc, "MISSING_P_24H", ct);
                    await tx.CommitAsync(ct);
                    return "CASE";
                }
                await db.ExecuteAsync(App02a3Sql.Cmd("""
                    UPDATE dbo.App02EventProgress
                    SET Status=N'PENDING_DEPENDENCY',
                        ReasonCode=N'MISSING_PREREQUISITE',
                        NextAttemptAtUtc=@Next,
                        StateChangedAtUtc=SYSUTCDATETIME()
                    WHERE TenantId=@Tenant AND ReceiptId=@Receipt
                      AND Status IN (N'RECEIVED',N'PENDING_DEPENDENCY')
                    """, new { Tenant = tenant, Receipt = c.ReceiptId,
                        Next = asOfUtc.UtcDateTime.Add(SafetyScanInterval) }, tx, ct));
                await tx.CommitAsync(ct);
                return "PENDING_DEPENDENCY";
            }

            // If named P was actually RECEIVED after the original 24-hour
            // dependency deadline, the missing-P CASE cannot be bypassed
            // merely because the process was down before its safety scan.
            bool approvedLateResume = c.ActiveCaseId.HasValue
                && await db.ExecuteScalarAsync<bool>(App02a3Sql.Cmd("""
                    SELECT CAST(CASE WHEN EXISTS (
                        SELECT 1 FROM dbo.App02CaseDisposition WITH(HOLDLOCK)
                        WHERE TenantId=@Tenant AND CaseId=@Case
                          AND Decision=N'APPROVED_RESUME')
                      THEN 1 ELSE 0 END AS bit)
                    """, new { Tenant = tenant, Case = c.ActiveCaseId }, tx, ct));
            if (p.ReceivedAtUtc - c.ReceivedAtUtc >= MissingDependencyWindow
                && !approvedLateResume)
            {
                await OpenCase(db, tx, c, asOfUtc, "LATE_P_AFTER_24H", ct);
                await tx.CommitAsync(ct);
                return "CASE";
            }
            if (p.ActorId == c.ActorId || !await RoleActive(db, tx, tenant,
                p.ActorId, "sales", ct))
            {
                await OpenCase(db, tx, c, asOfUtc, "P_ACTOR_INVALID_OR_REVOKED", ct);
                await tx.CommitAsync(ct);
                return "CASE";
            }

            CountRow? witness = await db.QuerySingleOrDefaultAsync<CountRow>(App02a3Sql.Cmd("""
                SELECT TripId,CountedAmount,CountedBy,VerificationStatus
                FROM dbo.CashierCountEvidence WITH(UPDLOCK,HOLDLOCK)
                WHERE TenantId=@Tenant AND EvidenceRef=@Evidence
                """, new { Tenant = tenant, Evidence = c.CountEvidenceRef }, tx, ct));
            if (witness is null || witness.TripId != c.TripId
                || witness.CountedAmount != p.OfferedAmount
                || witness.CountedBy != c.ActorId || witness.VerificationStatus != "VERIFIED")
            {
                await OpenCase(db, tx, c, asOfUtc, "COUNT_NOT_VERIFIED", ct);
                await tx.CommitAsync(ct);
                return "CASE";
            }
            bool offerAccepted = await db.ExecuteScalarAsync<bool>(App02a3Sql.Cmd("""
                SELECT CAST(CASE WHEN EXISTS (
                    SELECT 1 FROM dbo.CashHandoverAcceptance WITH(UPDLOCK,HOLDLOCK)
                    WHERE TenantId=@Tenant AND OfferId=@Offer)
                    THEN 1 ELSE 0 END AS bit)
                """, new { Tenant = tenant, Offer = p.OfferId }, tx, ct));
            if (offerAccepted)
            {
                await db.ExecuteAsync(App02a3Sql.Cmd("""
                    UPDATE dbo.App02EventProgress SET Status=N'REJECTED',
                        ReasonCode=N'OFFER_ALREADY_ACCEPTED',
                        NextAttemptAtUtc=NULL,StateChangedAtUtc=SYSUTCDATETIME()
                    WHERE TenantId=@Tenant AND ReceiptId=@Receipt
                    """, new { Tenant = tenant, Receipt = c.ReceiptId }, tx, ct));
                await tx.CommitAsync(ct);
                return "REJECTED";
            }

            await db.ExecuteAsync(App02a3Sql.Cmd("""
                IF NOT EXISTS (
                    SELECT 1 FROM dbo.App02ResolvedDependency
                    WHERE TenantId=@Tenant AND CReceiptId=@CReceipt)
                INSERT dbo.App02ResolvedDependency(
                    TenantId,CReceiptId,TripId,CountEvidenceRef,
                    PProducerId,PEventId,CActorId,PReceiptId,OfferId)
                VALUES(@Tenant,@CReceipt,@Trip,@Evidence,
                    @PProducer,@PEvent,@Actor,@PReceipt,@Offer)
                """, new { Tenant = tenant, CReceipt = c.ReceiptId,
                    Trip = c.TripId, Evidence = c.CountEvidenceRef,
                    PProducer = c.PrerequisiteProducerId, PEvent = c.PrerequisiteEventId,
                    Actor = c.ActorId, PReceipt = p.ReceiptId, Offer = p.OfferId }, tx, ct));

            // Reuse the exact APP-01D acceptance implementation INSIDE this
            // same transaction. There is no parallel APP-02 financial ledger.
            Guid command = Guid.NewGuid();
            var request = new AcceptCashHandoverCommand(
                "APP02_INTERNAL_BRIDGE", Guid.NewGuid().ToString("N"),
                "APP02:" + c.ReceiptId.ToString("N"),
                c.TripId, p.OfferId, c.CountEvidenceRef!);
            byte[] payloadHash = CommandPayloadHash.Compute(
                "CASH_HANDOVER_ACCEPT", tenant, c.ActorId, request.SourceNumber,
                ("TripId", request.TripId.ToString("N")),
                ("OfferId", request.OfferId.ToString("N")),
                ("CountEvidenceRef", request.CountEvidenceRef));
            await db.ExecuteAsync(App02a3Sql.Cmd("""
                INSERT dbo.BusinessCommand(
                    Id,TenantId,ProducerId,CommandId,
                    PayloadHashVersion,PayloadHash,ActorId,Status)
                VALUES(@Id,@Tenant,@Producer,@CommandId,1,@Hash,@Actor,N'RECEIVED')
                """, new { Id = command, Tenant = tenant, Producer = request.ProducerId,
                    request.CommandId, Hash = payloadHash, Actor = c.ActorId }, tx, ct));
            var applied = await App01dCashierUnloadService.AcceptAsync(
                db, tx, tenant, c.ActorId, command, request, ct);
            if (applied.Status == "REJECTED")
            {
                await db.ExecuteAsync(App02a3Sql.Cmd("""
                    UPDATE dbo.BusinessCommand SET Status=N'REJECTED',
                        OutcomeCode=N'REJECTED',ReasonCode=@Reason,
                        CompletedAtUtc=SYSUTCDATETIME()
                    WHERE TenantId=@Tenant AND Id=@Command;
                    UPDATE dbo.App02EventProgress SET Status=N'REJECTED',
                        ReasonCode=@Reason,NextAttemptAtUtc=NULL,
                        StateChangedAtUtc=SYSUTCDATETIME()
                    WHERE TenantId=@Tenant AND ReceiptId=@Receipt
                    """, new { Tenant = tenant, Command = command,
                        Receipt = c.ReceiptId, Reason = applied.ReasonCode }, tx, ct));
                await tx.CommitAsync(ct);
                return "REJECTED";
            }
            if (applied.Status != "APPLIED" || applied.SourceId is null)
                throw new InvalidOperationException("APP-01D bridge returned incomplete outcome.");
            Guid acceptance = applied.SourceId.Value;
            await db.ExecuteAsync(App02a3Sql.Cmd("""
                UPDATE dbo.BusinessCommand SET Status=N'APPLIED',
                    OutcomeCode=N'CASH_HANDOVER_ACCEPTED',
                    CompletedAtUtc=SYSUTCDATETIME()
                WHERE TenantId=@Tenant AND Id=@Command AND Status=N'RECEIVED'
                """, new { Tenant = tenant, Command = command }, tx, ct));

            // Deterministic synthetic outbound identity; retries and lost ACK
            // never mint another MessageId.
            byte[] seed = SHA256.HashData(Encoding.UTF8.GetBytes(
                "APP02-OUTBOX-v1\0" + tenant + "\0" + c.ReceiptId.ToString("N")));
            Guid messageId = new(seed.AsSpan(0, 16));
            byte[] outBytes = JsonSerializer.SerializeToUtf8Bytes(new
            {
                v = 1, tenantId = tenant,
                kind = "CashHandoverAcceptanceApplied",
                messageId = messageId.ToString("N"),
                cReceiptId = c.ReceiptId.ToString("N"),
                pReceiptId = p.ReceiptId.ToString("N"),
                acceptanceId = acceptance.ToString("N"),
                offerId = p.OfferId.ToString("N"),
                tripId = c.TripId.ToString("N"),
                countEvidenceRef = c.CountEvidenceRef,
                unit = "SYNTHETIC_UNITLESS", invoiceEligible = false
            });
            byte[] outHash = SHA256.HashData(outBytes);
            await db.ExecuteAsync(App02a3Sql.Cmd("""
                INSERT dbo.App02AcceptanceApplication(
                    TenantId,CReceiptId,PReceiptId,TripId,OfferId,
                    CountEvidenceRef,CActorId,AcceptanceId,MessageId)
                VALUES(@Tenant,@CReceipt,@PReceipt,@Trip,@Offer,
                    @Evidence,@Actor,@Acceptance,@MessageId);
                INSERT dbo.App02OutboxMessage(
                    TenantId,MessageId,CReceiptId,AcceptanceId,
                    PayloadVersion,CanonicalBytes,PayloadHash)
                VALUES(@Tenant,@MessageId,@CReceipt,@Acceptance,1,@Bytes,@Hash);
                INSERT dbo.App02OutboxDelivery(TenantId,MessageId)
                VALUES(@Tenant,@MessageId);
                UPDATE dbo.App02EventProgress SET Status=N'APPLIED',
                    ReasonCode=NULL,NextAttemptAtUtc=NULL,
                    StateChangedAtUtc=SYSUTCDATETIME()
                WHERE TenantId=@Tenant AND ReceiptId=@CReceipt
                """, new { Tenant = tenant, CReceipt = c.ReceiptId,
                    PReceipt = p.ReceiptId, Trip = c.TripId, Offer = p.OfferId,
                    Evidence = c.CountEvidenceRef, Actor = c.ActorId,
                    Acceptance = acceptance, MessageId = messageId,
                    Bytes = outBytes, Hash = outHash }, tx, ct));
            await tx.CommitAsync(ct);
            return "APPLIED";
        }
        catch (Exception ex) when (ex is SqlException or TimeoutException)
        {
            try { await tx.RollbackAsync(CancellationToken.None); }
            catch (InvalidOperationException) { }
            return await ScheduleTechnicalRetry(tenant, receiptId, asOfUtc, ct);
        }
        catch
        {
            try { await tx.RollbackAsync(CancellationToken.None); }
            catch (InvalidOperationException) { }
            throw;
        }
    }

    public async Task<int> SweepTenantAsync(string tenant, DateTimeOffset now,
        bool triggeredByEvent, CancellationToken ct)
    {
        var worker = App02a3Sql.WorkerPrincipal(tenant);
        await using var db = await connections.OpenSyntheticScratchAsync(worker, ct);
        var rows = (await db.QueryAsync<Guid>(App02a3Sql.Cmd("""
            SELECT TOP(100) r.ReceiptId
            FROM dbo.App02EventReceipt r
            JOIN dbo.App02EventProgress p ON p.TenantId=r.TenantId
                AND p.ReceiptId=r.ReceiptId
            WHERE r.TenantId=@Tenant
              AND r.EventKind=N'CashierAcceptanceRequested'
              AND p.Status IN(N'RECEIVED',N'PENDING_DEPENDENCY')
              AND ((p.ReasonCode=N'TECHNICAL_RETRY'
                      AND p.NextAttemptAtUtc IS NOT NULL
                      AND p.NextAttemptAtUtc<=@AsOf)
                  OR ((p.ReasonCode IS NULL OR p.ReasonCode<>N'TECHNICAL_RETRY')
                     AND (p.NextAttemptAtUtc IS NULL OR p.NextAttemptAtUtc<=@AsOf
                       OR r.ReceivedAtUtc<=@Expiry
                       OR (@Force=1 AND p.ReasonCode=N'MISSING_PREREQUISITE'
                           AND EXISTS (
                             SELECT 1 FROM dbo.App02EventReceipt named
                             WHERE named.TenantId=r.TenantId
                               AND named.ProducerId=r.PrerequisiteProducerId
                               AND named.EventId=r.PrerequisiteEventId
                               AND named.TripId=r.TripId
                               AND named.EventKind=N'CashHandoverOffered')))))
            ORDER BY r.ReceivedAtUtc,r.ReceiptId
            """, new { Tenant = tenant, Force = triggeredByEvent ? 1 : 0,
                AsOf = now.UtcDateTime,
                Expiry = now.UtcDateTime.Subtract(MissingDependencyWindow) }, null, ct)));
        foreach (Guid receipt in rows)
            await ProcessAsync(tenant, receipt, now, ct);
        return rows.Count();
    }

    private static Task<bool> RoleActive(SqlConnection db, IDbTransaction tx,
        string tenant, string actor, string role, CancellationToken ct) =>
        db.ExecuteScalarAsync<bool>(App02a3Sql.Cmd("""
            SELECT CAST(CASE WHEN EXISTS (
                SELECT 1 FROM dbo.App02ActorRoleGrant WITH(UPDLOCK,HOLDLOCK)
                WHERE TenantId=@Tenant AND ActorId=@Actor AND RoleCode=@Role
                  AND IsActive=1) THEN 1 ELSE 0 END AS bit)
            """, new { Tenant = tenant, Actor = actor, Role = role }, tx, ct));

    private static async Task OpenCase(SqlConnection db, IDbTransaction tx,
        CRow receipt, DateTimeOffset now, string reason, CancellationToken ct)
    {
        Guid caseId = Guid.NewGuid();
        await db.ExecuteAsync(App02a3Sql.Cmd("""
            INSERT dbo.App02ExceptionCase(
                CaseId,TenantId,ReceiptId,AsOfUtc,ReasonCode,OwnerQueue,NextAction)
            VALUES(@CaseId,@Tenant,@Receipt,@AsOf,@Reason,
                N'CASHIER_SUPERVISION',
                N'Review missing P, current role and independent cashier count');
            UPDATE dbo.App02EventProgress SET Status=N'CASE',
                ActiveCaseId=@CaseId,ReasonCode=@Reason,
                NextAttemptAtUtc=NULL,StateChangedAtUtc=SYSUTCDATETIME()
            WHERE TenantId=@Tenant AND ReceiptId=@Receipt
              AND Status IN(N'RECEIVED',N'PENDING_DEPENDENCY')
            """, new { CaseId = caseId, Tenant = receipt.TenantId,
                Receipt = receipt.ReceiptId, AsOf = now.UtcDateTime,
                Reason = reason }, tx, ct));
    }

    private async Task<string> ScheduleTechnicalRetry(string tenant,
        Guid receiptId, DateTimeOffset asOfUtc, CancellationToken ct)
    {
        await using SqlConnection db = await connections.OpenSyntheticScratchAsync(
            App02a3Sql.WorkerPrincipal(tenant), ct);
        await using SqlTransaction tx =
            (SqlTransaction)await db.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        try
        {
            CRow? c = await db.QuerySingleOrDefaultAsync<CRow>(App02a3Sql.Cmd("""
                SELECT r.ReceiptId,r.TenantId,r.ReceivedAtUtc,p.Status,p.TechnicalRetryCount
                FROM dbo.App02EventReceipt r WITH(UPDLOCK,HOLDLOCK)
                JOIN dbo.App02EventProgress p WITH(UPDLOCK,HOLDLOCK)
                    ON p.TenantId=r.TenantId AND p.ReceiptId=r.ReceiptId
                WHERE r.TenantId=@Tenant AND r.ReceiptId=@Receipt
                """, new { Tenant = tenant, Receipt = receiptId }, tx, ct));
            if (c is null || c.Status is "APPLIED" or "REJECTED" or "CASE")
            {
                await tx.CommitAsync(ct);
                return c?.Status ?? "UNKNOWN";
            }
            int retry = Math.Min(MaxTechnicalRetries, c.TechnicalRetryCount + 1);
            if (retry == MaxTechnicalRetries)
            {
                await db.ExecuteAsync(App02a3Sql.Cmd("""
                    UPDATE dbo.App02EventProgress SET TechnicalRetryCount=@Retry
                    WHERE TenantId=@Tenant AND ReceiptId=@Receipt
                    """, new { Retry = retry, Tenant = tenant, Receipt = receiptId }, tx, ct));
                await OpenCase(db, tx, c, asOfUtc,
                    "TECHNICAL_RETRIES_EXHAUSTED", ct);
            }
            else
            {
                int delaySeconds = Math.Min(900, 30 * (1 << (retry - 1)));
                await db.ExecuteAsync(App02a3Sql.Cmd("""
                    UPDATE dbo.App02EventProgress
                    SET TechnicalRetryCount=@Retry,Status=N'RECEIVED',
                        ReasonCode=N'TECHNICAL_RETRY',
                        LastAttemptAtUtc=@AsOf,NextAttemptAtUtc=@Next,
                        StateChangedAtUtc=SYSUTCDATETIME()
                    WHERE TenantId=@Tenant AND ReceiptId=@Receipt
                    """, new { Tenant = tenant, Receipt = receiptId, Retry = retry,
                        AsOf = asOfUtc.UtcDateTime,
                        Next = asOfUtc.UtcDateTime.AddSeconds(delaySeconds) }, tx, ct));
            }
            await tx.CommitAsync(ct);
            return retry == MaxTechnicalRetries ? "CASE" : "RETRY_SCHEDULED";
        }
        catch
        {
            try { await tx.RollbackAsync(CancellationToken.None); }
            catch (InvalidOperationException) { }
            throw;
        }
    }
}
