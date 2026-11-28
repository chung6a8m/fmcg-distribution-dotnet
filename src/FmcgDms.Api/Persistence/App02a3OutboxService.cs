using System.Data;
using System.Security.Cryptography;
using Dapper;
using FmcgDms.Api.Tenancy;
using Microsoft.Data.SqlClient;

namespace FmcgDms.Api.Persistence;

/// <summary>
/// Synthetic SQL recipient only. Claim, receiver inbox, and sender ACK are
/// THREE separate durable transactions. Repeating after a lost ACK is safe
/// by immutable MessageId+version+hash; no real provider is contacted.
/// </summary>
public sealed class App02a3OutboxService(TenantConnections connections,
    ILogger<App02a3OutboxService>? logger = null)
{
    public static readonly TimeSpan DeliveryLease = TimeSpan.FromMinutes(2);

    private sealed class DeliveryRow
    {
        public Guid MessageId { get; set; }
        public string Status { get; set; } = "";
        public byte AttemptCount { get; set; }
        public short PayloadVersion { get; set; }
        public byte[] PayloadHash { get; set; } = [];
        public byte[] CanonicalBytes { get; set; } = [];
    }

    public async Task<string> DispatchOneAsync(string tenant, DateTimeOffset asOfUtc,
        bool stopAfterConsumerCommit, CancellationToken ct,
        Guid? onlyMessageId = null)
    {
        var identity = App02a3Sql.WorkerPrincipal(tenant);
        Guid message;
        string owner = Guid.NewGuid().ToString("N");
        byte[] hash;
        short version;
        int attempts;
        await using (SqlConnection sender =
            await connections.OpenSyntheticScratchAsync(identity, ct))
        await using (SqlTransaction lease =
            (SqlTransaction)await sender.BeginTransactionAsync(IsolationLevel.Serializable, ct))
        {
            try
            {
                DeliveryRow? row = await sender.QuerySingleOrDefaultAsync<DeliveryRow>(
                    App02a3Sql.Cmd("""
                    SELECT TOP(1) d.MessageId,d.Status,d.AttemptCount,
                        m.PayloadVersion,m.PayloadHash,m.CanonicalBytes
                    FROM dbo.App02OutboxDelivery d WITH(UPDLOCK,HOLDLOCK)
                    JOIN dbo.App02OutboxMessage m WITH(HOLDLOCK)
                        ON m.TenantId=d.TenantId AND m.MessageId=d.MessageId
                    WHERE d.TenantId=@Tenant AND d.AttemptCount<6
                      AND (@OnlyMessage IS NULL OR d.MessageId=@OnlyMessage)
                      AND ((d.Status=N'PENDING'
                           AND (d.NextAttemptAtUtc IS NULL OR d.NextAttemptAtUtc<=@AsOf))
                       OR (d.Status=N'LEASED' AND d.LeaseExpiresAtUtc<=@AsOf))
                    ORDER BY m.CreatedAtUtc,d.MessageId
                    """, new { Tenant = tenant, AsOf = asOfUtc.UtcDateTime,
                        OnlyMessage = onlyMessageId }, lease, ct));
                if (row is null)
                {
                    // A last attempt with an expired lease becomes observable
                    // CASE, not indefinitely invisible.
                    await sender.ExecuteAsync(App02a3Sql.Cmd("""
                        UPDATE dbo.App02OutboxDelivery SET Status=N'CASE',
                            LeaseOwner=NULL,LeaseExpiresAtUtc=NULL,
                            LastErrorCode=N'DELIVERY_RETRIES_EXHAUSTED',
                            NextAttemptAtUtc=NULL
                        WHERE TenantId=@Tenant AND AttemptCount=6
                          AND Status=N'LEASED' AND LeaseExpiresAtUtc<=@AsOf
                        """, new { Tenant = tenant, AsOf = asOfUtc.UtcDateTime }, lease, ct));
                    await lease.CommitAsync(ct);
                    return "IDLE";
                }
                await connections.Rel75CheckpointAsync(sender, tenant, "OutboxAfterClaimRead", ct);
                if (row.PayloadVersion != 1 ||
                    !CryptographicOperations.FixedTimeEquals(
                        row.PayloadHash, SHA256.HashData(row.CanonicalBytes)))
                    throw new App02InvalidEventException("OUTBOX_PAYLOAD_INVALID");
                message = row.MessageId;
                hash = row.PayloadHash;
                version = row.PayloadVersion;
                attempts = row.AttemptCount + 1;
                await sender.ExecuteAsync(App02a3Sql.Cmd("""
                    UPDATE dbo.App02OutboxDelivery SET Status=N'LEASED',
                        AttemptCount=@Attempts,LeaseOwner=@Owner,
                        LeaseExpiresAtUtc=@Expiry,LastAttemptAtUtc=@AsOf,
                        NextAttemptAtUtc=NULL,LastErrorCode=NULL
                    WHERE TenantId=@Tenant AND MessageId=@Message
                      AND Status IN(N'PENDING',N'LEASED')
                    """, new { Tenant = tenant, Message = message, Attempts = attempts,
                        Owner = owner, Expiry = asOfUtc.UtcDateTime.Add(DeliveryLease),
                        AsOf = asOfUtc.UtcDateTime }, lease, ct));
                await lease.CommitAsync(ct);
            }
            catch
            {
                try { await lease.RollbackAsync(CancellationToken.None); }
                catch (InvalidOperationException) { }
                throw;
            }
        }

        try
        {
            // Separate connection and COMMIT; an uncommitted sender
            // delivery transaction cannot masquerade as a receiver ACK.
            await using (SqlConnection receiver =
                await connections.OpenSyntheticScratchAsync(identity, ct))
            await using (SqlTransaction inbox =
                (SqlTransaction)await receiver.BeginTransactionAsync(IsolationLevel.Serializable, ct))
            {
                try
                {
                    var found = await receiver.QuerySingleOrDefaultAsync<DeliveryRow>(
                        App02a3Sql.Cmd("""
                        SELECT MessageId,PayloadVersion,PayloadHash
                        FROM dbo.App02SyntheticConsumerInbox WITH(UPDLOCK,HOLDLOCK)
                        WHERE TenantId=@Tenant AND MessageId=@Message
                        """, new { Tenant = tenant, Message = message }, inbox, ct));
                    if (found is null)
                        await receiver.ExecuteAsync(App02a3Sql.Cmd("""
                            INSERT dbo.App02SyntheticConsumerInbox(
                                TenantId,MessageId,ConsumerReceiptId,
                                PayloadVersion,PayloadHash)
                            VALUES(@Tenant,@Message,@Receipt,@Version,@Hash)
                            """, new { Tenant = tenant, Message = message,
                                Receipt = Guid.NewGuid(), Version = version, Hash = hash },
                            inbox, ct));
                    else if (found.PayloadVersion != version
                        || !CryptographicOperations.FixedTimeEquals(found.PayloadHash, hash))
                        throw new App02InvalidEventException("CONSUMER_MESSAGE_ID_CONFLICT");
                    await inbox.CommitAsync(ct);
                }
                catch
                {
                    try { await inbox.RollbackAsync(CancellationToken.None); }
                    catch (InvalidOperationException) { }
                    throw;
                }
            }
            if (stopAfterConsumerCommit)
                return "CONSUMER_COMMITTED_ACK_LOST";

            await using SqlConnection ackConnection =
                await connections.OpenSyntheticScratchAsync(identity, ct);
            await using SqlTransaction ack =
                (SqlTransaction)await ackConnection.BeginTransactionAsync(
                    IsolationLevel.Serializable, ct);
            try
            {
                int changed = await ackConnection.ExecuteAsync(App02a3Sql.Cmd("""
                    UPDATE dbo.App02OutboxDelivery SET Status=N'ACKED',
                        LeaseOwner=NULL,LeaseExpiresAtUtc=NULL,
                        NextAttemptAtUtc=NULL,AckAtUtc=SYSUTCDATETIME(),
                        LastErrorCode=NULL
                    WHERE TenantId=@Tenant AND MessageId=@Message
                      AND Status=N'LEASED' AND LeaseOwner=@Owner
                    """, new { Tenant = tenant, Message = message, Owner = owner }, ack, ct));
                if (changed != 1)
                    throw new InvalidOperationException("Outbound ACK lease expired or stolen.");
                // A.2 trigger verifies an existing committed consumer inbox.
                await ack.CommitAsync(ct);
                return "ACKED";
            }
            catch
            {
                try { await ack.RollbackAsync(CancellationToken.None); }
                catch (InvalidOperationException) { }
                throw;
            }
        }
        catch (Exception ex) when (ex is SqlException or TimeoutException)
        {
            App02a3RecoveryTelemetry.Technical(logger, tenant, "outbox", message, ex);
            await RecordDispatchFailure(tenant, message, owner,
                attempts, asOfUtc, ct);
            return attempts >= 6 ? "CASE" : "RETRY_SCHEDULED";
        }
    }

    public async Task<int> SweepTenantAsync(string tenant, DateTimeOffset now,
        CancellationToken ct)
    {
        int handled = 0;
        while (handled < 100)
        {
            string state = await DispatchOneAsync(tenant, now,
                stopAfterConsumerCommit: false, ct);
            if (state == "IDLE") break;
            handled++;
            if (state == "RETRY_SCHEDULED") break; // backoff, not hot-loop
        }
        return handled;
    }

    private async Task RecordDispatchFailure(string tenant, Guid message, string owner,
        int attempts, DateTimeOffset now, CancellationToken ct)
    {
        var identity = App02a3Sql.WorkerPrincipal(tenant);
        await using SqlConnection db =
            await connections.OpenSyntheticScratchAsync(identity, ct);
        await using SqlTransaction tx =
            (SqlTransaction)await db.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        try
        {
            int delay = Math.Min(900, 30 * (1 << Math.Max(0, attempts - 1)));
            await db.ExecuteAsync(App02a3Sql.Cmd("""
                UPDATE dbo.App02OutboxDelivery
                SET Status=@State,LeaseOwner=NULL,LeaseExpiresAtUtc=NULL,
                    LastErrorCode=@Reason,NextAttemptAtUtc=@Next
                WHERE TenantId=@Tenant AND MessageId=@Message
                  AND Status=N'LEASED' AND LeaseOwner=@Owner
                """, new { Tenant = tenant, Message = message, Owner = owner,
                    State = attempts >= 6 ? "CASE" : "PENDING",
                    Reason = attempts >= 6 ? "DELIVERY_RETRIES_EXHAUSTED" : "DELIVERY_RETRY",
                    Next = attempts >= 6 ? (DateTime?)null
                        : now.UtcDateTime.AddSeconds(delay) }, tx, ct));
            await tx.CommitAsync(ct);
        }
        catch
        {
            try { await tx.RollbackAsync(CancellationToken.None); }
            catch (InvalidOperationException) { }
            throw;
        }
    }
}
