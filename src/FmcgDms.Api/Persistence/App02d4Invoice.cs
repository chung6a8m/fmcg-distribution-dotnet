using Dapper;
using FmcgDms.Api.Authentication;
using Microsoft.Data.SqlClient;
using System.Data;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FmcgDms.Api.Persistence;

internal static partial class App02d4Invoice
{
    internal const string Schema = """
        CREATE TABLE d4app.InvoiceProfile(
            Seller nvarchar(80) NOT NULL PRIMARY KEY,
            Channel nvarchar(80) NULL,
            Active bit NOT NULL
        );
        CREATE TABLE d4app.InvoiceIntent(
            IntentId uniqueidentifier NOT NULL PRIMARY KEY,
            StableKey char(64) NOT NULL UNIQUE,
            DeliveryId uniqueidentifier NOT NULL UNIQUE,
            SourceLineId uniqueidentifier NOT NULL,
            Seller nvarchar(80) NOT NULL,
            Channel nvarchar(80) NOT NULL,
            Hash char(64) NOT NULL,
            SourceHash char(64) NOT NULL,
            Status varchar(20) NOT NULL CHECK(Status IN ('PREPARED','UNKNOWN','MOCK_RECORDED')),
            CreatedAt datetimeoffset NOT NULL,
            FactId uniqueidentifier NULL,
            FactCommittedAt datetimeoffset NULL,
            Payload nvarchar(max) NOT NULL
        );
        CREATE TABLE d4app.InvoiceAttempt(
            AttemptId bigint IDENTITY PRIMARY KEY,
            IntentId uniqueidentifier NOT NULL REFERENCES d4app.InvoiceIntent(IntentId),
            Operation varchar(12) NOT NULL CHECK(Operation IN ('SEND','LOOKUP')),
            TransportId uniqueidentifier NULL,
            StartedAt datetimeoffset NOT NULL,
            FinishedAt datetimeoffset NULL,
            Result varchar(30) NULL,
            Fault varchar(20) NULL,
            ObservedStatus varchar(20) NULL,
            ObservedIntentId uniqueidentifier NULL,
            ObservedHash char(64) NULL,
            ObservedChannel nvarchar(96) NULL
        );
        CREATE TABLE d4app.InvoiceCase(
            CaseId uniqueidentifier NOT NULL PRIMARY KEY,
            CaseKey char(64) NOT NULL UNIQUE,
            Kind varchar(25) NOT NULL,
            IntentId uniqueidentifier NULL REFERENCES d4app.InvoiceIntent(IntentId),
            FactId uniqueidentifier NULL,
            Seller nvarchar(80) NOT NULL,
            OccurredAt datetimeoffset NOT NULL,
            ReceivedAt datetimeoffset NOT NULL,
            OwnerRole varchar(30) NOT NULL,
            NextAction nvarchar(120) NOT NULL
        );
        CREATE TABLE d4invoice.InvoiceFact(
            IntentId uniqueidentifier NOT NULL PRIMARY KEY,
            StableKey char(64) NOT NULL UNIQUE,
            FactId uniqueidentifier NOT NULL UNIQUE,
            Channel nvarchar(80) NOT NULL,
            Hash char(64) NOT NULL,
            CommittedAt datetimeoffset NOT NULL,
            Payload nvarchar(max) NOT NULL
        );
        """;

    internal sealed record PrepareInput(Guid DeliveryId, string? Seller, string? Channel, string? Hash);
    internal sealed record SendInput(Guid TransportId, string? Fault, string? Seller, string? Channel, string? Hash);
    internal sealed record LookupInput(string? Fault);
    internal sealed record BeforeBInput(string Seller);
    internal sealed record ObservedBInput(Guid FactId, string Seller, DateTimeOffset OccurredAt);
    private sealed record Intent(Guid IntentId, string StableKey, Guid DeliveryId, Guid SourceLineId,
        string Seller, string Channel, string Hash, string SourceHash, string Status, DateTimeOffset CreatedAt,
        Guid? FactId, DateTimeOffset? FactCommittedAt, string Payload);
    private sealed record Profile(string? Channel, bool Active);
    private sealed record Case(Guid CaseId, string Kind, Guid? IntentId, Guid? FactId, string Seller,
        DateTimeOffset OccurredAt, DateTimeOffset ReceivedAt, string OwnerRole, string NextAction);
    private sealed record Payload(string Tenant, Guid DeliveryId, Guid SourceLineId, string Seller, string Channel,
        DateTimeOffset OccurredAt, decimal Quantity, decimal Gross, decimal Ar, decimal Cost, string SourceHash,
        string Units, string Version);
    private sealed record PartnerMessage(Guid IntentId, string StableKey, string Channel, string Hash, string Payload, string? Fault);
    private sealed record PartnerResult(string Status, Guid IntentId, string StableKey, string Channel, string Hash,
        Guid? FactId = null, DateTimeOffset? CommittedAt = null);
    private const string IntentColumns = "IntentId,StableKey,DeliveryId,SourceLineId,Seller,Channel,Hash,SourceHash,Status,CreatedAt,FactId,FactCommittedAt,Payload";
    private const string CaseColumns = "CaseId,Kind,IntentId,FactId,Seller,OccurredAt,ReceivedAt,OwnerRole,NextAction";

    internal static void Map(RouteGroupBuilder group)
    {
        var invoice = group.MapGroup("/invoice");
        invoice.MapPost("/prepare", Prepare);
        invoice.MapGet("/{intentId:guid}", Read);
        invoice.MapPost("/{intentId:guid}/send", Send);
        invoice.MapPost("/{intentId:guid}/lookup", Lookup);
        invoice.MapPost("/b-intent", BeforeB);
        invoice.MapPost("/b-observed", ObserveB);
    }

    private static async Task<IResult> Prepare(PrepareInput input, ClaimsPrincipal user, App02d4Runtime runtime,
        CancellationToken ct)
    {
        await using SqlConnection db = await runtime.Open(user, "invoice", ct);
        var source = await runtime.ReadSource(db, input.DeliveryId, ct);
        if (source is null)
            return Reject("SOURCE_NOT_COMMITTED");
        if (input.Seller is not null && input.Seller != source.Seller ||
            input.Channel is not null && input.Channel != source.Channel)
            return Reject("SOURCE_CONFLICT");

        await using var transaction = await db.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        await Acquire(db, "D4Invoice:delivery:" + input.DeliveryId, transaction, ct);
        var profile = await SourceProfile(db, source.Seller, transaction, ct);
        if (profile?.Channel is null || string.IsNullOrWhiteSpace(source.Channel))
            return Reject("B_NO_CHANNEL");
        if (!profile.Active || profile.Channel != source.Channel)
            return Reject("PROFILE_INACTIVE");

        // The authenticated tenant has its own catalog. The resolved catalog joins
        // the immutable logical identity to its nonce-owned rehearsal namespace.
        string tenant = SyntheticAuth.GetTenantId(user);
        string payload = JsonSerializer.Serialize(new Payload(tenant, source.DeliveryId, source.SourceLineId,
            source.Seller, source.Channel, source.OccurredAt, source.Quantity, source.Gross, source.Ar, source.Cost,
            source.Hash, "SYNTHETIC_UNITLESS", "v0.1"));
        string hash = HashText(payload);
        if (input.Hash is not null && input.Hash != hash)
            return Reject("PAYLOAD_CONFLICT");

        var existing = await db.QuerySingleOrDefaultAsync<Intent>(new CommandDefinition(
            $"SELECT {IntentColumns} FROM d4app.InvoiceIntent WHERE DeliveryId=@DeliveryId",
            new { input.DeliveryId }, transaction, cancellationToken: ct));
        if (existing is not null)
            return existing.Hash == hash ? Results.Ok(existing) : Reject("PAYLOAD_CONFLICT");

        var intent = new Intent(Guid.NewGuid(), StableKey(tenant, source.Seller, source.DeliveryId),
            source.DeliveryId, source.SourceLineId, source.Seller, source.Channel, hash, source.Hash,
            "PREPARED", runtime.UtcNow, null, null, payload);
        await db.ExecuteAsync(new CommandDefinition("""
            INSERT d4app.InvoiceIntent(IntentId,StableKey,DeliveryId,SourceLineId,Seller,Channel,Hash,SourceHash,Status,CreatedAt,Payload)
            VALUES(@IntentId,@StableKey,@DeliveryId,@SourceLineId,@Seller,@Channel,@Hash,@SourceHash,@Status,@CreatedAt,@Payload);
            """, intent, transaction, cancellationToken: ct));
        await transaction.CommitAsync(ct);
        return Results.Ok(intent);
    }

    private static async Task<IResult> Read(Guid intentId, ClaimsPrincipal user, App02d4Runtime runtime, CancellationToken ct)
    {
        await using var db = await runtime.Open(user, "invoice", ct);
        return await ReadIntent(db, intentId, ct) is { } intent ? Results.Ok(intent) : Results.NotFound();
    }

    private static async Task<IResult> Send(Guid intentId, SendInput input, HttpContext http, App02d4Runtime runtime,
        CancellationToken ct)
    {
        await using var db = await runtime.Open(http.User, "invoice", ct);
        if (input.TransportId == Guid.Empty || input.Fault is not (null or "BEFORE_COMMIT" or "AFTER_COMMIT" or "CRASH_AFTER_COMMIT"))
            return Results.BadRequest();
        await Acquire(db, "D4Invoice:intent:" + intentId, null, ct);
        var intent = await ReadIntent(db, intentId, ct);
        if (intent is null)
            return Results.NotFound();
        if ((input.Seller is not null && input.Seller != intent.Seller) ||
            (input.Channel is not null && input.Channel != intent.Channel) ||
            (input.Hash is not null && input.Hash != intent.Hash))
            return Reject("PAYLOAD_CONFLICT");
        if (intent.Status != "PREPARED")
            return Results.Ok(intent);

        var profile = await SourceProfile(db, intent.Seller, null, ct);
        if (profile?.Active != true || profile.Channel != intent.Channel)
            return Reject("PROFILE_INACTIVE");

        // Dispatch is persisted before crossing the HTTP boundary. A crash after
        // either side commits therefore reopens as UNKNOWN using the original key.
        var attempt = await Begin(db, intent, "SEND", input.TransportId, input.Fault, runtime.UtcNow, ct);
        var result = await Call(runtime, http, "record", intent, input.Fault, ct);
        if (input.Fault == "CRASH_AFTER_COMMIT")
            return Results.Ok(intent with { Status = "UNKNOWN" });
        if (MatchesFact(intent, result))
            return Results.Ok(await Finish(db, intent, attempt, runtime.UtcNow, "MOCK_RECORDED", result, ct));
        await Finish(db, intent, attempt, runtime.UtcNow, "ACK_MISSING", result, ct);
        return Results.Ok(intent with { Status = "UNKNOWN" });
    }

    private static async Task<IResult> Lookup(Guid intentId, LookupInput input, HttpContext http, App02d4Runtime runtime,
        CancellationToken ct)
    {
        await using var db = await runtime.Open(http.User, "invoice", ct);
        if (input.Fault is not (null or "UNAVAILABLE" or "PROCESSING" or "WRONG_ID" or "WRONG_HASH" or "WRONG_PROFILE"))
            return Results.BadRequest();
        await Acquire(db, "D4Invoice:intent:" + intentId, null, ct);
        var intent = await ReadIntent(db, intentId, ct);
        if (intent is null)
            return Results.NotFound();
        if (intent.Status != "UNKNOWN")
            return Results.Ok(intent);

        var attempt = await Begin(db, intent, "LOOKUP", null, input.Fault, runtime.UtcNow, ct);
        var profile = await SourceProfile(db, intent.Seller, null, ct);
        if (profile?.Active != true || profile.Channel != intent.Channel)
        {
            await Finish(db, intent, attempt, runtime.UtcNow, "PROFILE_CONFLICT", null, ct);
            return Results.Ok(intent);
        }
        var result = await Call(runtime, http, "lookup", intent, input.Fault, ct);
        if (MatchesFact(intent, result))
            return Results.Ok(await Finish(db, intent, attempt, runtime.UtcNow, "FOUND", result, ct));
        if (result is { Status: "ABSENT" } && MatchesTuple(intent, result) && result.FactId is null)
            return Results.Ok(await Finish(db, intent, attempt, runtime.UtcNow, "ABSENT", result, ct));

        if (result is { Status: "FOUND" })
            await RecordCase(db, Digest("INVOICE_LOOKUP_MISMATCH", intent.IntentId, result.FactId ?? Guid.Empty), "LOOKUP_MISMATCH",
                intent.IntentId, result.FactId, intent.Seller, result.CommittedAt ?? intent.CreatedAt, runtime.UtcNow,
                "Xác minh định danh và payload với đối tác mô phỏng", ct);
        string disposition = result?.Status switch
        {
            "PROCESSING" => "PROCESSING",
            "FOUND" or "ABSENT" => "LOOKUP_MISMATCH",
            _ => "LOOKUP_UNAVAILABLE"
        };
        await Finish(db, intent, attempt, runtime.UtcNow, disposition, result, ct);
        return Results.Ok(intent);
    }

    private static async Task<IResult> BeforeB(BeforeBInput input, ClaimsPrincipal user, App02d4Runtime runtime,
        CancellationToken ct)
    {
        await using var db = await runtime.Open(user, "invoice", ct);
        if (!ValidSeller(input.Seller))
            return Results.BadRequest();
        return IsMissingChannel(await SourceProfile(db, input.Seller, null, ct))
            ? Reject("B_NO_CHANNEL") : Reject("NO_MISSING_CHANNEL_FACT");
    }

    private static async Task<IResult> ObserveB(ObservedBInput input, ClaimsPrincipal user, App02d4Runtime runtime,
        CancellationToken ct)
    {
        await using var db = await runtime.Open(user, "invoice", ct);
        if (!ValidSeller(input.Seller) || input.FactId == Guid.Empty || input.OccurredAt == default ||
            input.OccurredAt > runtime.UtcNow)
            return Results.BadRequest();
        var key = Digest("B_NO_CHANNEL_OCCURRED", input.FactId);
        var result = await ReadCase(db, key, null, ct);
        if (result is null)
        {
            if (!IsMissingChannel(await SourceProfile(db, input.Seller, null, ct)))
                return Reject("NO_MISSING_CHANNEL_FACT");
            result = await RecordCase(db, key, "B_NO_CHANNEL_OCCURRED", null, input.FactId, input.Seller,
                input.OccurredAt, runtime.UtcNow, "Xác minh sự kiện và xử lý thiếu kênh", ct);
        }
        return result.Seller == input.Seller && result.OccurredAt == input.OccurredAt
            ? Results.Ok(result)
            : Reject("FACT_CONFLICT");
    }

    private static bool IsMissingChannel(Profile? profile) => profile is { Channel: null };
    private static bool ValidSeller(string? seller) => !string.IsNullOrWhiteSpace(seller) && seller.Length <= 80;
    private static IResult Reject(string status) => Results.Conflict(new { status });
    private static string Digest(params object[] values) => HashText(JsonSerializer.Serialize(values));
    private static string HashText(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    internal static string StableKey(string tenant, string seller, Guid deliveryId) => Digest(tenant, seller, deliveryId, "invoice-intent");

    private static Task<Profile?> SourceProfile(SqlConnection db, string seller, IDbTransaction? tx, CancellationToken ct) =>
        db.QuerySingleOrDefaultAsync<Profile>(new CommandDefinition(
            "SELECT Channel,Active FROM d4app.InvoiceProfile WHERE Seller=@seller", new { seller }, tx, cancellationToken: ct));

    private static Task<Intent?> ReadIntent(SqlConnection db, Guid id, CancellationToken ct) =>
        db.QuerySingleOrDefaultAsync<Intent>(new CommandDefinition(
            $"SELECT {IntentColumns} FROM d4app.InvoiceIntent WHERE IntentId=@id", new { id }, cancellationToken: ct));

    private static async Task Acquire(SqlConnection db, string key, IDbTransaction? tx, CancellationToken ct)
    {
        var result = await db.ExecuteScalarAsync<int>(new CommandDefinition("""
            DECLARE @result int;
            EXEC @result=sys.sp_getapplock @Resource=@key,@LockMode='Exclusive',@LockOwner=@owner,@LockTimeout=15000;
            SELECT @result;
            """, new { key, owner = tx is null ? "Session" : "Transaction" }, tx, cancellationToken: ct));
        if (result < 0)
            throw new InvalidOperationException("Invoice rehearsal serialization lock unavailable.");
    }

    private static async Task<long> Begin(SqlConnection db, Intent intent, string operation, Guid? transportId,
        string? fault, DateTimeOffset now, CancellationToken ct)
    {
        await using var tx = await db.BeginTransactionAsync(ct);
        long id = await db.ExecuteScalarAsync<long>(new CommandDefinition("""
            UPDATE d4app.InvoiceIntent SET Status='UNKNOWN' WHERE IntentId=@IntentId;
            INSERT d4app.InvoiceAttempt(IntentId,Operation,TransportId,StartedAt,Fault)
            OUTPUT INSERTED.AttemptId VALUES(@IntentId,@operation,@transportId,@now,@fault);
            """, new { intent.IntentId, operation, transportId, now, fault }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return id;
    }

    private static async Task<Intent> Finish(SqlConnection db, Intent intent, long attempt, DateTimeOffset now,
        string disposition, PartnerResult? result, CancellationToken ct)
    {
        var attached = disposition is "FOUND" or "MOCK_RECORDED" ? result : null;
        string status = attached is not null ? "MOCK_RECORDED" : disposition == "ABSENT" ? "PREPARED" : "UNKNOWN";
        await using var tx = await db.BeginTransactionAsync(ct);
        await db.ExecuteAsync(new CommandDefinition("""
            UPDATE d4app.InvoiceIntent SET Status=@status,FactId=@FactId,FactCommittedAt=@CommittedAt WHERE IntentId=@IntentId;
            UPDATE d4app.InvoiceAttempt
            SET Result=@disposition,FinishedAt=@now,ObservedStatus=@ObservedStatus,ObservedIntentId=@ObservedIntentId,
                ObservedHash=@ObservedHash,ObservedChannel=@ObservedChannel
            WHERE AttemptId=@attempt;
            """, new
            {
                status, FactId = attached?.FactId, CommittedAt = attached?.CommittedAt, intent.IntentId, disposition, now, attempt,
                ObservedStatus = result?.Status, ObservedIntentId = result?.IntentId,
                ObservedHash = result?.Hash, ObservedChannel = result?.Channel
            }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return intent with { Status = status, FactId = attached?.FactId, FactCommittedAt = attached?.CommittedAt };
    }

    private static async Task<Case> RecordCase(SqlConnection db, string key, string kind, Guid? intentId, Guid? factId,
        string seller, DateTimeOffset occurredAt, DateTimeOffset receivedAt, string action, CancellationToken ct)
    {
        await using var tx = await db.BeginTransactionAsync(ct);
        await Acquire(db, "D4Invoice:case:" + key, tx, ct);
        var existing = await ReadCase(db, key, tx, ct);
        if (existing is not null)
            return existing;
        var item = new Case(Guid.NewGuid(), kind, intentId, factId, seller, occurredAt, receivedAt, "invoice", action);
        await db.ExecuteAsync(new CommandDefinition("""
            INSERT d4app.InvoiceCase(CaseId,CaseKey,Kind,IntentId,FactId,Seller,OccurredAt,ReceivedAt,OwnerRole,NextAction)
            VALUES(@CaseId,@key,@Kind,@IntentId,@FactId,@Seller,@OccurredAt,@ReceivedAt,@OwnerRole,@NextAction);
            """, new {
                key, item.CaseId, item.Kind, item.IntentId, item.FactId, item.Seller, item.OccurredAt,
                item.ReceivedAt, item.OwnerRole, item.NextAction
            }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return item;
    }

    private static Task<Case?> ReadCase(SqlConnection db, string key, IDbTransaction? tx, CancellationToken ct) =>
        db.QuerySingleOrDefaultAsync<Case>(new CommandDefinition(
            $"SELECT {CaseColumns} FROM d4app.InvoiceCase WHERE CaseKey=@key", new { key }, tx, cancellationToken: ct));
}
