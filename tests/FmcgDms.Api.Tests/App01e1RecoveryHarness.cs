using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Data.SqlClient;

namespace FmcgDms.Api.Tests;

/// <summary>
/// APP-01E.1: two distinct HTTP transports start together at an explicit
/// request-admission barrier. This does not pretend to control SQL scheduling.
/// </summary>
internal sealed class TwoClientReleaseGate
{
    private readonly TaskCompletionSource<bool> _bothReady =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> _released =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _arrived;

    public int Arrived => Volatile.Read(ref _arrived);
    public Task BothReady => _bothReady.Task;

    public async Task AdmitAsync(CancellationToken ct)
    {
        int arrived = Interlocked.Increment(ref _arrived);
        if (arrived == 2) _bothReady.TrySetResult(true);
        if (arrived > 2) throw new InvalidOperationException("Exactly two arrivals are supported.");
        await _released.Task.WaitAsync(ct);
    }

    public void Release() => _released.TrySetResult(true);
}

internal sealed class TwoClientGateHandler(TwoClientReleaseGate gate) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        await gate.AdmitAsync(cancellationToken);
        return await base.SendAsync(request, cancellationToken);
    }
}

/// <summary>
/// Drops ONE client-visible response only AFTER TestServer has completed the
/// request. A synthetic lost ACK, not a claim about real network/TCP behavior.
/// The application service and database are never patched by this handler.
/// </summary>
internal sealed class AfterServerAckLossHandler(string targetPath) : DelegatingHandler
{
    private int _remaining = 1;
    private int _suppressed;
    public int Suppressed => Volatile.Read(ref _suppressed);

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        HttpResponseMessage response = await base.SendAsync(request, cancellationToken);
        if (request.Method == HttpMethod.Post
            && string.Equals(request.RequestUri?.AbsolutePath, targetPath, StringComparison.Ordinal)
            && Interlocked.Exchange(ref _remaining, 0) == 1)
        {
            response.Dispose();
            Interlocked.Increment(ref _suppressed);
            throw new HttpRequestException("APP01E1_SYNTHETIC_ACK_SUPPRESSED_AFTER_RESPONSE");
        }
        return response;
    }
}

/// <summary>
/// Temporary LocalDB-only SQL fault, armed by the test and removed in finally.
/// It throws AFTER a LedgerEffect INSERT and BEFORE the command transaction
/// commits, so previous in-transaction stock/source/right work must roll back.
/// Never part of migrations, production API or application DI.
/// </summary>
internal sealed class PreCommitLedgerFault(string connectionString) : IAsyncDisposable
{
    private const string Trigger = "APP01E1_ThrowOnLedgerInsert";

    public static async Task<PreCommitLedgerFault> ArmAsync(
        string connectionString, string tenant, string sourceNumber)
    {
        if (tenant is not ("tenant-a" or "tenant-b")
            || !Regex.IsMatch(sourceNumber, @"\A[a-f0-9]{32}\z", RegexOptions.CultureInvariant))
            throw new ArgumentException("Unsafe test-only SQL fault identity.");

        await using var db = new SqlConnection(connectionString);
        await db.OpenAsync();
        await db.ExecuteAsync($"""
            CREATE TRIGGER dbo.{Trigger} ON dbo.LedgerEffect AFTER INSERT
            AS
            BEGIN
                SET NOCOUNT ON;
                IF EXISTS (
                    SELECT 1 FROM inserted i
                    JOIN dbo.BusinessSourceLine l
                      ON l.TenantId=i.TenantId AND l.Id=i.BusinessSourceLineId
                    JOIN dbo.BusinessSource s
                      ON s.TenantId=l.TenantId AND s.Id=l.BusinessSourceId
                    WHERE s.TenantId=N'{tenant}'
                      AND s.SourceKind=N'STOCK_ALLOC'
                      AND s.SourceNumber=N'{sourceNumber}'
                )
                    THROW 51061, 'APP01E1_INJECT_PRECOMMIT', 1;
            END
            """);
        return new PreCommitLedgerFault(connectionString);
    }

    public async ValueTask DisposeAsync()
    {
        await using var db = new SqlConnection(connectionString);
        await db.ExecuteAsync($"DROP TRIGGER IF EXISTS dbo.{Trigger}");
    }
}

internal sealed class CommandDbRow
{
    public Guid Id { get; set; }
    public string Status { get; set; } = "";
    public string OutcomeCode { get; set; } = "";
    public string? ReasonCode { get; set; }
}

internal sealed class LedgerFingerprintRow
{
    public Guid Id { get; set; }
    public string SourceNumber { get; set; } = "";
    public int LineNumber { get; set; }
    public string EffectKey { get; set; } = "";
    public string LedgerKind { get; set; } = "";
    public decimal QuantityDelta { get; set; }
    public decimal AmountDelta { get; set; }
}

internal sealed record CommandSqlReadback(
    string Database, string TenantId, string ProducerId, string CommandId,
    string? Status, string? OutcomeCode, string? ReasonCode, Guid? SourceId,
    int Commands, int Sources, int SourceLines, int Effects, int Rights,
    decimal Allocated, string LedgerSha256);

/// <summary>
/// Reads observed values exclusively from persisted SQL rows; never from HTTP
/// response bodies, expected constants or mutable application caches.
/// </summary>
internal static class RecoverySqlProbe
{
    public static async Task<CommandSqlReadback> ReadAsync(
        string connectionString, string tenant, string producer, string commandId,
        Guid location, Guid sku)
    {
        await using var db = new SqlConnection(connectionString);
        await db.OpenAsync();
        var key = new { Tenant = tenant, Producer = producer, Command = commandId };
        string database = await db.QuerySingleAsync<string>("SELECT DB_NAME()");
        CommandDbRow? row = await db.QuerySingleOrDefaultAsync<CommandDbRow>("""
            SELECT Id, Status, OutcomeCode, ReasonCode FROM dbo.BusinessCommand
            WHERE TenantId=@Tenant AND ProducerId=@Producer AND CommandId=@Command
            """, key);
        var p = new { Tenant = tenant, CommandRowId = (Guid?)row?.Id };

        int sources = await db.ExecuteScalarAsync<int>("""
            SELECT COUNT(*) FROM dbo.BusinessSource
            WHERE TenantId=@Tenant AND BusinessCommandId=@CommandRowId
            """, p);
        Guid? sourceId = await db.QuerySingleOrDefaultAsync<Guid?>("""
            SELECT Id FROM dbo.BusinessSource
            WHERE TenantId=@Tenant AND BusinessCommandId=@CommandRowId
            """, p);
        int sourceLines = await db.ExecuteScalarAsync<int>("""
            SELECT COUNT(*) FROM dbo.BusinessSourceLine l
            JOIN dbo.BusinessSource s ON s.TenantId=l.TenantId AND s.Id=l.BusinessSourceId
            WHERE s.TenantId=@Tenant AND s.BusinessCommandId=@CommandRowId
            """, p);
        int rights = await db.ExecuteScalarAsync<int>("""
            SELECT COUNT(*) FROM dbo.StockRight r
            JOIN dbo.BusinessSourceLine l
              ON l.TenantId=r.TenantId AND l.Id=r.BusinessSourceLineId
            JOIN dbo.BusinessSource s
              ON s.TenantId=l.TenantId AND s.Id=l.BusinessSourceId
            WHERE s.TenantId=@Tenant AND s.BusinessCommandId=@CommandRowId
            """, p);
        LedgerFingerprintRow[] ledger = (await db.QueryAsync<LedgerFingerprintRow>("""
            SELECT e.Id, s.SourceNumber, l.LineNumber, e.EffectKey, e.LedgerKind,
                   e.QuantityDelta, e.AmountDelta
            FROM dbo.LedgerEffect e
            JOIN dbo.BusinessSourceLine l
              ON l.TenantId=e.TenantId AND l.Id=e.BusinessSourceLineId
            JOIN dbo.BusinessSource s
              ON s.TenantId=l.TenantId AND s.Id=l.BusinessSourceId
            WHERE s.TenantId=@Tenant AND s.BusinessCommandId=@CommandRowId
            ORDER BY s.SourceNumber, l.LineNumber, e.EffectKey, e.Id
            """, p)).ToArray();
        decimal allocated = await db.QuerySingleAsync<decimal>("""
            SELECT AllocatedBaseQuantity FROM dbo.StockPool
            WHERE TenantId=@Tenant AND StockLocationId=@Location AND SkuId=@Sku
            """, new { Tenant = tenant, Location = location, Sku = sku });
        string digest = Convert.ToHexString(
            SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(ledger)));
        return new CommandSqlReadback(
            database, tenant, producer, commandId, row?.Status, row?.OutcomeCode,
            row?.ReasonCode, sourceId, row is null ? 0 : 1, sources,
            sourceLines, ledger.Length, rights, allocated, digest);
    }
}

internal sealed record HarnessStage(
    string Name, CommandSqlReadback Actual,
    int ExpectedCommands, int ExpectedSources, int ExpectedSourceLines,
    int ExpectedEffects, int ExpectedRights, decimal ExpectedAllocated);

internal sealed record SqlNumericDiff(string Field, decimal Expected, decimal Actual)
{
    public decimal Diff => Actual - Expected;
    public bool Match => Diff == 0m;
}

internal static class RecoveryHarnessEvidence
{
    public static void Write(string tenant, string scenario, params HarnessStage[] stages)
    {
        if (tenant is not ("tenant-a" or "tenant-b")
            || !Regex.IsMatch(scenario, @"\A[a-z][a-z0-9-]+\z")
            || stages.Length == 0)
            throw new ArgumentException("Invalid recovery evidence identity.");

        var snapshots = stages.Select(stage =>
        {
            var a = stage.Actual;
            SqlNumericDiff[] cells =
            [
                new("BusinessCommand", stage.ExpectedCommands, a.Commands),
                new("BusinessSource", stage.ExpectedSources, a.Sources),
                new("BusinessSourceLine", stage.ExpectedSourceLines, a.SourceLines),
                new("LedgerEffect", stage.ExpectedEffects, a.Effects),
                new("StockRight", stage.ExpectedRights, a.Rights),
                new("StockPoolAllocated", stage.ExpectedAllocated, a.Allocated)
            ];
            return new
            {
                Stage = stage.Name,
                Sql = a,
                ExpectedCells = cells.Length,
                MatchedCells = cells.Count(c => c.Match),
                Cells = cells
            };
        }).ToArray();
        string? database = snapshots.Select(x => x.Sql.Database).Distinct().Single();
        var report = new
        {
            Schema = "APP01E1_HARNESS_V1",
            TestRunId = Environment.GetEnvironmentVariable("APP01E1_TEST_RUN_ID") ?? "LOCAL_UNTRACKED",
            SourceHeadSha = Environment.GetEnvironmentVariable("APP01E1_SOURCE_HEAD_SHA"),
            WorkflowSha = Environment.GetEnvironmentVariable("GITHUB_SHA"),
            TenantId = tenant,
            Database = database,
            Scenario = scenario,
            Status = snapshots.All(s => s.MatchedCells == s.ExpectedCells) ? "PASS" : "FAIL",
            Stages = snapshots
        };
        string directory = Path.Combine(AppContext.BaseDirectory, "TestResults", "APP-01E1");
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, $"{tenant}-{scenario}-harness.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false));
    }
}
