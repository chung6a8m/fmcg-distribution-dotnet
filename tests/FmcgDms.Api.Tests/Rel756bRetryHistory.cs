using Dapper;
using FmcgDms.Api.Persistence;
using Microsoft.Data.SqlClient;

namespace FmcgDms.Api.Tests;

// Numeric and allowlisted metadata from the immutable history of this exact
// C receipt; no user text, payload, event ID, connection string or SQL text.
internal sealed class Rel756bHistoryRow
{
    public Guid HistoryId { get; set; }
    public string? StatusFrom { get; set; }
    public string StatusTo { get; set; } = "";
    public int TechnicalRetryCount { get; set; }
    public string? ReasonCode { get; set; }
    public DateTime? NextAttemptAtUtc { get; set; }
    public DateTime LoggedAtUtc { get; set; }

    internal void Redact()
    {
        var state = new RecoverySqlState { PStatus = StatusFrom,
            CStatus = StatusTo, CReasonCode = ReasonCode };
        state.Redact();
        StatusFrom = state.PStatus;
        StatusTo = state.CStatus ?? "[redacted]";
        ReasonCode = state.CReasonCode;
    }
}

internal sealed record Rel756bHistoryCapture(string Status, DateTimeOffset StartedUtc,
    DateTimeOffset FinishedUtc, Rel756bHistoryRow[] Rows, RecoveryException? Error);

internal static class Rel756bRetryHistory
{
    internal const int MaxRows = 64;
    internal static async Task<Rel756bHistoryCapture> CaptureAsync(SqlScratchFixture fixture,
        string tenant, Guid trip, Guid receipt, App02EventInput c, CancellationToken ct)
    {
        DateTimeOffset started = DateTimeOffset.UtcNow;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(3));
        CancellationToken token = deadline.Token;
        try
        {
            string database = tenant switch
            {
                "tenant-a" => fixture.TenantADb,
                "tenant-b" => fixture.TenantBDb,
                _ => throw new UnauthorizedAccessException()
            };
            if (!fixture.SqlServer.StartsWith(@"(localdb)\", StringComparison.OrdinalIgnoreCase)
                || receipt == Guid.Empty || c.TripId != trip
                || c.EventKind != App02EventCanonicalV1.AcceptanceKind)
                throw new UnauthorizedAccessException();
            var target = new SqlConnectionStringBuilder(fixture.ConnectionString(database))
            {
                ConnectTimeout = 2, Pooling = false
            };
            await using var db = new SqlConnection(target.ConnectionString);
            await db.OpenAsync(token);
            int owned = await db.ExecuteScalarAsync<int>(new CommandDefinition("""
                SELECT CASE WHEN DB_NAME()=@Database
                  AND EXISTS (SELECT 1 FROM dbo.App00ScratchMarker WHERE Nonce=@Nonce)
                  AND EXISTS (SELECT 1 FROM dbo.AppTenantBinding WHERE Slot=1 AND TenantId=@Tenant)
                  AND EXISTS (SELECT 1 FROM dbo.App02EventReceipt WHERE TenantId=@Tenant
                    AND ReceiptId=@Receipt AND TripId=@Trip AND EventKind=N'CashierAcceptanceRequested'
                    AND ProducerId=@Producer AND EventId=@Event)
                  THEN 1 ELSE 0 END
                """, new { Database = database, fixture.Nonce, Tenant = tenant,
                    Receipt = receipt, Trip = trip, Producer = c.ProducerId, Event = c.EventId },
                commandTimeout: 2, cancellationToken: token));
            if (owned != 1) throw new UnauthorizedAccessException();
            var rows = (await db.QueryAsync<Rel756bHistoryRow>(new CommandDefinition("""
                SELECT TOP(65) h.HistoryId,h.StatusFrom,h.StatusTo,
                    CAST(h.TechnicalRetryCount AS int) TechnicalRetryCount,
                    h.ReasonCode,h.NextAttemptAtUtc,h.LoggedAtUtc
                FROM dbo.App02EventStatusHistory h
                JOIN dbo.App02EventReceipt r ON r.TenantId=h.TenantId AND r.ReceiptId=h.ReceiptId
                WHERE h.TenantId=@Tenant AND h.ReceiptId=@Receipt AND r.TripId=@Trip
                  AND r.EventKind=N'CashierAcceptanceRequested'
                  AND r.ProducerId=@Producer AND r.EventId=@Event
                ORDER BY h.LoggedAtUtc,h.HistoryId
                """, new { Tenant = tenant, Receipt = receipt, Trip = trip,
                    Producer = c.ProducerId, Event = c.EventId },
                commandTimeout: 2, cancellationToken: token))).ToArray();
            foreach (var row in rows) row.Redact();
            return new(rows.Length == 0 ? "EMPTY" : rows.Length > MaxRows ? "TRUNCATED" : "CAPTURED",
                started, DateTimeOffset.UtcNow, rows.Take(MaxRows).ToArray(), null);
        }
        catch (Exception ex)
        {
            return new("UNAVAILABLE", started, DateTimeOffset.UtcNow, [], RecoveryException.From(ex));
        }
    }

    // History and progress are separate read-committed observations. Only call
    // their agreement corroborated when all observed retry counts are present.
    internal static bool Agrees(Rel756bHistoryCapture history, RecoverySqlState? state, Guid receipt)
    {
        if (history.Status != "CAPTURED" || state?.CReceiptId != receipt
            || state.TechnicalRetryCount is not int count || count is < 0 or > 6
            || history.Rows.Length is < 1 or > MaxRows)
            return false;
        var rows = history.Rows;
        if (rows.Any(x => x.TechnicalRetryCount < 0 || x.TechnicalRetryCount > count
                || x.StatusTo == "[redacted]" || x.StatusFrom == "[redacted]"
                || x.ReasonCode == "[redacted]")
            || rows.Select(x => x.HistoryId).Distinct().Count() != rows.Length
            || rows[^1].StatusTo != state.CStatus
            || rows[^1].TechnicalRetryCount != count
            || rows[^1].NextAttemptAtUtc != state.NextAttemptAtUtc
            || rows[^1].ReasonCode != state.CReasonCode)
            return false;
        for (int i = 1; i < rows.Length; i++)
            if (rows[i].LoggedAtUtc < rows[i - 1].LoggedAtUtc
                || rows[i].TechnicalRetryCount < rows[i - 1].TechnicalRetryCount)
                return false;
        // Every persisted nonterminal technical retry has an immutable due.
        // Exhaustion CASE (6) has no future due by design.
        int scheduled = Math.Min(count, 5);
        return Enumerable.Range(1, scheduled).All(n => rows.Any(x =>
            x.TechnicalRetryCount == n && x.ReasonCode == "TECHNICAL_RETRY"
            && x.NextAttemptAtUtc.HasValue));
    }
}
