using System.Data;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using FmcgDms.Api.Authentication;
using FmcgDms.Api.Tenancy;
using Microsoft.Data.SqlClient;

namespace FmcgDms.Api.Persistence;

public sealed record App02EventInput(
    string ProducerId, string EventId, string EventKind, Guid TripId,
    Guid? OfferId, decimal? OfferedAmount,
    string? PrerequisiteProducerId, string? PrerequisiteEventId,
    string? CountEvidenceRef, DateTimeOffset OccurredAtUtc);

public sealed record App02IntakeResult(
    string Ack, string Status, string OutcomeCode, Guid? ReceiptId, bool Replayed);

public sealed record App02OutcomeResult(
    string Status, Guid? ReceiptId, Guid? AcceptanceId, string? ReasonCode);

/// <summary>Intentional fail-closed invalid synthetic event; never a durable REJECTED.</summary>
public sealed class App02InvalidEventException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

internal static class App02a3Sql
{
    public static CommandDefinition Cmd(string sql, object? args,
        IDbTransaction? tx, CancellationToken ct) =>
        new(sql, args, tx, cancellationToken: ct);

    public static string Tenant(ClaimsPrincipal actor)
    {
        string value = SyntheticAuth.GetTenantId(actor);
        if (value is not ("tenant-a" or "tenant-b"))
            throw new UnauthorizedAccessException("Invalid synthetic tenant.");
        return value;
    }

    public static string Actor(ClaimsPrincipal principal)
    {
        string? actor = principal.FindFirst("actor_id")?.Value;
        if (actor is null || actor.Length is < 1 or > 128
            || !string.Equals(actor, principal.FindFirst("sub")?.Value, StringComparison.Ordinal)
            || actor != actor.Trim() || actor.Any(char.IsControl))
            throw new UnauthorizedAccessException("Bound actor required.");
        return actor;
    }

    // NOT exposed as a request-supplied tenant credential. This identity is
    // constructed solely inside the configured dev/test worker from its fixed
    // tenant catalog and still passes the nonce/LocalDB binding guard.
    public static string Producer(ClaimsPrincipal principal)
    {
        string? producer = principal.FindFirst("producer_id")?.Value;
        if (producer is null || producer.Length is < 1 or > 128
            || !char.IsAsciiLetterOrDigit(producer[0])
            || producer.Any(c => !(char.IsAsciiLetterOrDigit(c)
                || c is '.' or '_' or ':' or '-')))
            throw new UnauthorizedAccessException("Signed producer identity required.");
        return producer;
    }

    public static ClaimsPrincipal WorkerPrincipal(string tenant)
    {
        if (tenant is not ("tenant-a" or "tenant-b"))
            throw new UnauthorizedAccessException("Unsupported worker tenant.");
        return new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(SyntheticAuth.TenantClaim, tenant)], "APP02_SYNTHETIC_WORKER"));
    }

    public static async Task RequireRole(SqlConnection db, IDbTransaction tx,
        string tenant, string actor, string role, CancellationToken ct)
    {
        bool active = await db.ExecuteScalarAsync<bool>(Cmd("""
            SELECT CAST(CASE WHEN EXISTS (
                SELECT 1 FROM dbo.App02ActorRoleGrant WITH(UPDLOCK,HOLDLOCK)
                WHERE TenantId=@Tenant AND ActorId=@Actor AND RoleCode=@Role
                    AND IsActive=1) THEN 1 ELSE 0 END AS bit)
            """, new { Tenant = tenant, Actor = actor, Role = role }, tx, ct));
        if (!active)
            throw new UnauthorizedAccessException("Current synthetic role not granted.");
    }

    public static async Task Lock(SqlConnection db, IDbTransaction tx,
        string resource, CancellationToken ct)
    {
        int result = await db.ExecuteScalarAsync<int>(Cmd("""
            DECLARE @result int;
            EXEC @result=sys.sp_getapplock @Resource=@Key,
                @LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=10000;
            SELECT @result;
            """, new { Key = resource }, tx, ct));
        if (result < 0) throw new TimeoutException("APP02 transaction lock was not granted.");
    }

    public static string IdentityLock(string tenant, string producer, string eventId) =>
        "APP02:EVENT:" + Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(tenant + "\0" + producer + "\0" + eventId)));
}

/// <summary>
/// Synthetic-only, server actor/tenant-derived durable receipt writer. A 202
/// is returned only AFTER the receipt AND initial progress COMMIT. No C business
/// source, ledger or outbox is inserted during intake.
/// </summary>
public sealed class App02a3EventIntakeService(
    TenantConnections connections, App02a3WorkerSignal signal)
{
    public async Task<App02IntakeResult> IntakeAsync(ClaimsPrincipal principal,
        App02EventInput input, CancellationToken ct)
    {
        string tenant = App02a3Sql.Tenant(principal);
        string actor = App02a3Sql.Actor(principal);
        if (!string.Equals(input.ProducerId,
            App02a3Sql.Producer(principal), StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Event producer must match signed principal.");
        string requiredRole = input.EventKind switch
        {
            App02EventCanonicalV1.OfferKind => "sales",
            App02EventCanonicalV1.AcceptanceKind => "cashier",
            _ => throw new App02InvalidEventException("EVENT_KIND_INVALID")
        };
        if (!principal.HasClaim("synthetic_role", requiredRole))
            throw new UnauthorizedAccessException("Synthetic event actor role required.");
        var envelope = new App02EventEnvelope(tenant, input.ProducerId,
            input.EventId, input.EventKind, actor, input.TripId,
            input.OfferId, input.OfferedAmount, input.PrerequisiteProducerId,
            input.PrerequisiteEventId, input.CountEvidenceRef, input.OccurredAtUtc);
        byte[] bytes;
        try { bytes = App02EventCanonicalV1.Encode(envelope); }
        catch (ArgumentException) { throw new App02InvalidEventException("EVENT_V1_INVALID"); }
        byte[] hash = SHA256.HashData(bytes);
        await using SqlConnection db = await connections.OpenSyntheticScratchAsync(principal, ct);
        await using SqlTransaction tx =
            (SqlTransaction)await db.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        try
        {
            await App02a3Sql.Lock(db, tx,
                App02a3Sql.IdentityLock(tenant, input.ProducerId, input.EventId), ct);
            // Current role is a tenant-bound SQL fact, not merely a JWT
            // issued before a possible synthetic revocation.
            await App02a3Sql.RequireRole(db, tx, tenant, actor, requiredRole, ct);

            var saved = await db.QuerySingleOrDefaultAsync<ReceiptRow>(App02a3Sql.Cmd("""
                SELECT r.ReceiptId,r.ActorId,r.PayloadVersion,r.PayloadHash,p.Status
                FROM dbo.App02EventReceipt r WITH(UPDLOCK,HOLDLOCK)
                JOIN dbo.App02EventProgress p ON p.TenantId=r.TenantId
                    AND p.ReceiptId=r.ReceiptId
                WHERE r.TenantId=@Tenant AND r.ProducerId=@Producer
                    AND r.EventId=@EventId
                """, new { Tenant = tenant, Producer = input.ProducerId, input.EventId }, tx, ct));
            if (saved is not null)
            {
                if (!string.Equals(saved.ActorId, actor, StringComparison.Ordinal))
                    throw new UnauthorizedAccessException("Event identity belongs to another actor.");
                if (saved.PayloadVersion != App02EventCanonicalV1.Version ||
                    !CryptographicOperations.FixedTimeEquals(saved.PayloadHash, hash))
                {
                    await tx.RollbackAsync(ct);
                    return new("NONE", "CONFLICT", "EVENT_PAYLOAD_MISMATCH", null, false);
                }
                await tx.CommitAsync(ct);
                return new("RECEIVED_COMMITTED", saved.Status, "DURABLE_REPLAY",
                    saved.ReceiptId, true);
            }

            if (requiredRole == "sales")
            {
                bool sourceCommitted = await db.ExecuteScalarAsync<bool>(App02a3Sql.Cmd("""
                    SELECT CAST(CASE WHEN EXISTS (
                        SELECT 1 FROM dbo.CashHandoverOffer o WITH(HOLDLOCK)
                        JOIN dbo.BusinessSource s WITH(HOLDLOCK)
                            ON s.TenantId=o.TenantId AND s.Id=o.Id
                        JOIN dbo.BusinessCommand cmd WITH(HOLDLOCK)
                            ON cmd.TenantId=s.TenantId AND cmd.Id=s.BusinessCommandId
                        WHERE o.TenantId=@Tenant AND o.Id=@OfferId
                          AND o.TripId=@Trip AND o.OfferedAmount=@Amount
                          AND o.OfferedBy COLLATE Latin1_General_100_BIN2
                              = @Actor COLLATE Latin1_General_100_BIN2
                          AND s.SourceKind=N'CASH_HANDOVER_OFFER'
                          AND s.ActorId COLLATE Latin1_General_100_BIN2
                              = @Actor COLLATE Latin1_General_100_BIN2
                          AND cmd.Status=N'APPLIED'
                          AND cmd.OutcomeCode=N'CASH_HANDOVER_OFFERED'
                    ) THEN 1 ELSE 0 END AS bit)
                    """, new { Tenant = tenant, input.OfferId,
                        Trip = input.TripId, Amount = input.OfferedAmount,
                        Actor = actor }, tx, ct));
                if (!sourceCommitted)
                    throw new App02InvalidEventException("P_OFFER_NOT_COMMITTED");
            }
            Guid receipt = Guid.NewGuid();
            await db.ExecuteAsync(App02a3Sql.Cmd("""
                INSERT dbo.App02EventReceipt
                    (ReceiptId,TenantId,ProducerId,EventId,EventKind,ActorId,TripId,
                     OfferId,OfferedAmount,PrerequisiteProducerId,PrerequisiteEventId,
                     CountEvidenceRef,OccurredAtUtc,PayloadVersion,CanonicalBytes,PayloadHash)
                VALUES(@Receipt,@Tenant,@Producer,@EventId,@Kind,@Actor,@Trip,
                    @Offer,@Amount,@PProducer,@PEvent,@Count,@Occurred,1,@Bytes,@Hash);
                INSERT dbo.App02EventProgress(TenantId,ReceiptId)
                VALUES(@Tenant,@Receipt);
                """, new { Receipt = receipt, Tenant = tenant,
                    Producer = input.ProducerId, input.EventId,
                    Kind = input.EventKind, Actor = actor, Trip = input.TripId,
                    Offer = input.OfferId, Amount = input.OfferedAmount,
                    PProducer = input.PrerequisiteProducerId,
                    PEvent = input.PrerequisiteEventId, Count = input.CountEvidenceRef,
                    Occurred = input.OccurredAtUtc.UtcDateTime, Bytes = bytes,
                    Hash = hash }, tx, ct));
            string state = requiredRole == "sales" ? "APPLIED" : "PENDING_DEPENDENCY";
            await db.ExecuteAsync(App02a3Sql.Cmd("""
                UPDATE dbo.App02EventProgress SET Status=@State,
                    StateChangedAtUtc=SYSUTCDATETIME()
                WHERE TenantId=@Tenant AND ReceiptId=@Receipt
                """, new { State = state, Tenant = tenant, Receipt = receipt }, tx, ct));
            await tx.CommitAsync(ct);
            signal.Wake(tenant);
            return new("RECEIVED_COMMITTED", state, "DURABLE_INTAKE", receipt, false);
        }
        catch
        {
            try { await tx.RollbackAsync(CancellationToken.None); }
            catch (InvalidOperationException) { }
            throw;
        }
    }

    public async Task<App02OutcomeResult> LookupAsync(ClaimsPrincipal principal,
        string producer, string eventId, CancellationToken ct)
    {
        string tenant = App02a3Sql.Tenant(principal);
        string actor = App02a3Sql.Actor(principal);
        if (!string.Equals(producer,
            App02a3Sql.Producer(principal), StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Outcome producer must match signed principal.");
        // Canonical input identity grammar, not an unvalidated SQL lookup.
        if (producer.Length is < 1 or > 128 || eventId.Length is < 1 or > 128
            || producer.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or ':' or '-'))
            || eventId.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or ':' or '-'))
            || !char.IsAsciiLetterOrDigit(producer[0])
            || !char.IsAsciiLetterOrDigit(eventId[0]))
            throw new App02InvalidEventException("EVENT_IDENTITY_INVALID");

        await using SqlConnection db = await connections.OpenSyntheticScratchAsync(principal, ct);
        await using SqlTransaction tx =
            (SqlTransaction)await db.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        try
        {
            var found = await db.QuerySingleOrDefaultAsync<LookupRow>(App02a3Sql.Cmd("""
                SELECT r.ReceiptId,r.EventKind,p.Status,p.ReasonCode,
                    a.AcceptanceId
                FROM dbo.App02EventReceipt r WITH(HOLDLOCK)
                JOIN dbo.App02EventProgress p ON p.TenantId=r.TenantId
                    AND p.ReceiptId=r.ReceiptId
                LEFT JOIN dbo.App02AcceptanceApplication a
                    ON a.TenantId=r.TenantId AND a.CReceiptId=r.ReceiptId
                WHERE r.TenantId=@Tenant AND r.ProducerId=@Producer
                    AND r.EventId=@EventId
                    AND r.ActorId COLLATE Latin1_General_100_BIN2
                        = @Actor COLLATE Latin1_General_100_BIN2
                """, new { Tenant = tenant, Producer = producer, EventId = eventId,
                    Actor = actor }, tx, ct));
            // Unknown identity or different actor must be indistinguishable.
            if (found is not null)
                await App02a3Sql.RequireRole(db, tx, tenant, actor,
                    found.EventKind == App02EventCanonicalV1.OfferKind ? "sales" : "cashier", ct);
            await tx.CommitAsync(ct);
            return found is null
                ? new("UNKNOWN", null, null, null)
                : new(found.Status, found.ReceiptId, found.AcceptanceId, found.ReasonCode);
        }
        catch
        {
            try { await tx.RollbackAsync(CancellationToken.None); }
            catch (InvalidOperationException) { }
            throw;
        }
    }

    private sealed class ReceiptRow
    {
        public Guid ReceiptId { get; set; }
        public string ActorId { get; set; } = "";
        public short PayloadVersion { get; set; }
        public byte[] PayloadHash { get; set; } = [];
        public string Status { get; set; } = "";
    }

    private sealed class LookupRow
    {
        public Guid ReceiptId { get; set; }
        public string EventKind { get; set; } = "";
        public string Status { get; set; } = "";
        public string? ReasonCode { get; set; }
        public Guid? AcceptanceId { get; set; }
    }
}
