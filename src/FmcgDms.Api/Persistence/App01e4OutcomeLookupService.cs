using System.Security.Claims;
using Dapper;
using FmcgDms.Api.Authentication;
using FmcgDms.Api.Tenancy;

namespace FmcgDms.Api.Persistence;

/// <summary>
/// Read-only APP-01E.4 recovery view. UNKNOWN is never evidence that a
/// concurrent command failed: clients must retain the original identity,
/// actor and canonical payload and reconcile/retry that exact command.
/// </summary>
public sealed record BusinessCommandRecovery(
    string State, string? Status, string? OutcomeCode,
    string? ReasonCode, Guid? SourceId)
{
    public static BusinessCommandRecovery Unknown(string reason) =>
        new("UNKNOWN", null, null, reason, null);
}

public sealed class App01e4OutcomeLookupService(TenantConnections connections)
{
    private sealed class PersistedOutcome
    {
        public string Status { get; set; } = "";
        public string? OutcomeCode { get; set; }
        public string? ReasonCode { get; set; }
        public Guid? SourceId { get; set; }
        public long SourceCount { get; set; }
        public DateTime? CompletedAtUtc { get; set; }
    }

    public static bool ValidIdentity(string? key) =>
        !string.IsNullOrWhiteSpace(key) && key.Length <= 128
        && !key.Any(char.IsControl);

    public async Task<BusinessCommandRecovery> LookupAsync(
        ClaimsPrincipal principal, string producerId, string commandId, CancellationToken ct)
    {
        if (!ValidIdentity(producerId) || !ValidIdentity(commandId))
            throw new ArgumentException("Invalid recovery identity.");

        // Defence in depth; the route also requires this policy. An auditor-only
        // token cannot use a command identity to discover private outcomes.
        if (!principal.HasClaim("synthetic_role", "warehouse")
            && !principal.HasClaim("synthetic_role", "sales")
            && !principal.HasClaim("synthetic_role", "cashier"))
            throw new UnauthorizedAccessException("Synthetic command actor role required.");

        string tenant = SyntheticAuth.GetTenantId(principal);
        string actor = principal.FindFirst("actor_id")?.Value
            ?? throw new UnauthorizedAccessException("Bound synthetic actor required.");
        if (!ValidIdentity(actor) || actor != actor.Trim()
            || !string.Equals(actor, principal.FindFirst("sub")?.Value, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Bound synthetic actor is invalid.");

        // OpenSyntheticScratchAsync verifies LocalDB, nonce and tenant binding
        // on every lookup. No caller-controlled database, tenant or actor.
        await using var db = await connections.OpenSyntheticScratchAsync(principal, ct);
        PersistedOutcome? row = await db.QuerySingleOrDefaultAsync<PersistedOutcome>(
            new CommandDefinition("""
                SELECT c.Status, c.OutcomeCode, c.ReasonCode, c.CompletedAtUtc,
                       s.Id AS SourceId,
                       (SELECT COUNT_BIG(*) FROM dbo.BusinessSource x
                        WHERE x.TenantId=c.TenantId AND x.BusinessCommandId=c.Id) AS SourceCount
                FROM dbo.BusinessCommand c WITH (READCOMMITTEDLOCK)
                OUTER APPLY (
                    SELECT TOP(1) x.Id FROM dbo.BusinessSource x
                    WHERE x.TenantId=c.TenantId AND x.BusinessCommandId=c.Id
                    ORDER BY x.Id
                ) s
                WHERE c.TenantId=@Tenant AND c.ActorId=@Actor
                  AND c.ProducerId=@Producer AND c.CommandId=@Command
                  -- The scratch database may use a case-insensitive SQL
                  -- collation. Different signed actors/identities must not
                  -- discover one another's outcomes through CI collation.
                  AND CONVERT(varbinary(256),c.ActorId)=CONVERT(varbinary(256),@Actor)
                  AND CONVERT(varbinary(256),c.ProducerId)=CONVERT(varbinary(256),@Producer)
                  AND CONVERT(varbinary(256),c.CommandId)=CONVERT(varbinary(256),@Command)
                """, new { Tenant = tenant, Actor = actor, Producer = producerId, Command = commandId },
                cancellationToken: ct));

        // A missing row can be a command that has not started or has not
        // committed. Actor-mismatch is intentionally indistinguishable.
        if (row is null) return BusinessCommandRecovery.Unknown("NOT_FOUND");

        // Do not claim a terminal outcome from partial/corrupt rows, including
        // a purported APPLIED with a missing or duplicated source. A valid
        // REJECTED is a durable terminal record with no business source.
        bool terminal = row.CompletedAtUtc.HasValue
            && (row.Status == "APPLIED" && row.SourceCount == 1
                    && row.SourceId.HasValue && !string.IsNullOrEmpty(row.OutcomeCode)
                    && row.ReasonCode is null
                || row.Status == "REJECTED" && row.SourceCount == 0
                    && row.SourceId is null && row.OutcomeCode == "REJECTED"
                    && !string.IsNullOrWhiteSpace(row.ReasonCode));
        return terminal
            ? new BusinessCommandRecovery("COMPLETED", row.Status, row.OutcomeCode,
                row.ReasonCode, row.SourceId)
            : BusinessCommandRecovery.Unknown("NOT_FINAL");
    }
}
