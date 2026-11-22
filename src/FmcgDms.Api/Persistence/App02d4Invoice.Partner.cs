using Dapper;
using Microsoft.Data.SqlClient;
using System.Text.Json;

namespace FmcgDms.Api.Persistence;

internal static partial class App02d4Invoice
{
    internal static void MapStub(RouteGroupBuilder group)
    {
        group.MapPost("/record", PartnerRecord);
        group.MapPost("/lookup", PartnerLookup);
    }

    private static async Task<IResult> PartnerRecord(PartnerMessage input, HttpContext http,
        App02d4Runtime runtime, CancellationToken ct)
    {
        await using var db = await runtime.OpenStub(http, "invoice", ct);
        if (!Valid(input, http.Request.Headers["X-D4-Technical-Tenant"].ToString()) || input.Fault is not (null or "BEFORE_COMMIT" or "AFTER_COMMIT" or "CRASH_AFTER_COMMIT"))
            return Results.BadRequest();

        await using var tx = await db.BeginTransactionAsync(ct);
        await Acquire(db, "D4Invoice:partner:" + input.StableKey, tx, ct);
        var fact = await ReadFact(db, input, tx, ct);
        if (fact is not null && !MatchesTuple(input, fact))
            return Reject("PARTNER_KEY_CONFLICT");
        if (fact is null)
        {
            fact = new("FOUND", input.IntentId, input.StableKey, input.Channel, input.Hash,
                Guid.NewGuid(), runtime.UtcNow);
            await db.ExecuteAsync(new CommandDefinition("""
                INSERT d4invoice.InvoiceFact(IntentId,StableKey,FactId,Channel,Hash,CommittedAt,Payload)
                VALUES(@IntentId,@StableKey,@FactId,@Channel,@Hash,@CommittedAt,@Payload);
                """, new { fact.IntentId, fact.StableKey, fact.FactId, fact.Channel, fact.Hash, fact.CommittedAt, input.Payload },
                tx, cancellationToken: ct));
        }

        if (input.Fault == "BEFORE_COMMIT")
        {
            await tx.RollbackAsync(ct);
            return Abort(http);
        }
        await tx.CommitAsync(ct);
        return input.Fault is "AFTER_COMMIT" or "CRASH_AFTER_COMMIT" ? Abort(http) : Results.Ok(fact);
    }

    private static async Task<IResult> PartnerLookup(PartnerMessage input, HttpContext http,
        App02d4Runtime runtime, CancellationToken ct)
    {
        await using var db = await runtime.OpenStub(http, "invoice", ct);
        if (!Valid(input, http.Request.Headers["X-D4-Technical-Tenant"].ToString()))
            return Results.BadRequest();
        if (input.Fault == "UNAVAILABLE")
            return Abort(http);
        if (input.Fault == "PROCESSING")
            return Results.Ok(WithoutFact("PROCESSING", input));

        // Serialize against an in-progress partner transaction for this key.
        // A lookup may answer ABSENT only after that writer committed or rolled back.
        await using var tx = await db.BeginTransactionAsync(ct);
        await Acquire(db, "D4Invoice:partner:" + input.StableKey, tx, ct);
        var fact = await ReadFact(db, input, tx, ct);
        await tx.CommitAsync(ct);

        if (fact is null)
            return Results.Ok(WithoutFact("ABSENT", input));
        return Results.Ok(input.Fault switch
        {
            "WRONG_ID" => fact with { IntentId = Guid.Empty },
            "WRONG_HASH" => fact with { Hash = new string(fact.Hash[0] == '0' ? '1' : '0', 64) },
            "WRONG_PROFILE" => fact with { Channel = fact.Channel + "-conflict" },
            _ => fact
        });
    }

    private static Task<PartnerResult?> ReadFact(SqlConnection db, PartnerMessage input, System.Data.IDbTransaction tx,
        CancellationToken ct) => db.QuerySingleOrDefaultAsync<PartnerResult>(new CommandDefinition("""
            SELECT 'FOUND' AS Status,IntentId,StableKey,Channel,Hash,FactId,CommittedAt
            FROM d4invoice.InvoiceFact WHERE StableKey=@StableKey;
            """, input, tx, cancellationToken: ct));

    private static PartnerResult WithoutFact(string status, PartnerMessage input) =>
        new(status, input.IntentId, input.StableKey, input.Channel, input.Hash);

    private static IResult Abort(HttpContext http)
    {
        http.Abort();
        return Results.Empty;
    }

    private static bool Valid(PartnerMessage input, string tenant)
    {
        if (input.IntentId == Guid.Empty || !ValidSeller(input.Channel) || !HexDigest(input.StableKey) ||
            !HexDigest(input.Hash) || input.Payload is not { Length: > 0 and <= 4096 } ||
            input.Hash != HashText(input.Payload))
            return false;

        try
        {
            var payload = JsonSerializer.Deserialize<Payload>(input.Payload);
            return payload is { Version: "v0.1", Units: "SYNTHETIC_UNITLESS" } && payload.Tenant == tenant &&
                payload.Channel == input.Channel && StableKey(tenant, payload.Seller, payload.DeliveryId) == input.StableKey;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool HexDigest(string? value) => value is { Length: 64 } &&
        value.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F');

    private static bool MatchesTuple(PartnerMessage input, PartnerResult result) =>
        result.IntentId == input.IntentId && result.StableKey == input.StableKey &&
        result.Hash == input.Hash && result.Channel == input.Channel;

    private static bool MatchesTuple(Intent intent, PartnerResult result) =>
        MatchesTuple(PartnerInput(intent, null), result);

    private static bool MatchesFact(Intent intent, PartnerResult? result) => result is
        { Status: "FOUND", FactId: { } factId, CommittedAt: not null } &&
        factId != Guid.Empty && MatchesTuple(intent, result);

    private static PartnerMessage PartnerInput(Intent intent, string? fault) =>
        new(intent.IntentId, intent.StableKey, intent.Channel, intent.Hash, intent.Payload, fault);

    private static async Task<PartnerResult?> Call(App02d4Runtime runtime, HttpContext http, string operation,
        Intent intent, string? fault, CancellationToken ct)
    {
        using var client = runtime.Client(http, "invoice");
        try
        {
            using var reply = await client.PostAsJsonAsync(operation, PartnerInput(intent, fault), ct);
            if (reply.IsSuccessStatusCode)
                return await reply.Content.ReadFromJsonAsync<PartnerResult>(ct);
        }
        catch (Exception error) when (error is HttpRequestException or JsonException ||
            error is OperationCanceledException && !ct.IsCancellationRequested)
        {
            // A missing or unreadable remote response has no authority to close UNKNOWN.
        }
        return null;
    }
}
