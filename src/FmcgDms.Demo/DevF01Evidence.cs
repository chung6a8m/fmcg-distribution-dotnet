using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;

namespace FmcgDms.Demo;

internal static class DevF01Evidence
{
    private static readonly string[] Tables = ["AOrder", "SaleDelivery", "ReceivableOpenItem",
        "PhysicalStockMovement", "PhysicalStockBalance", "LedgerEffect"];

    internal sealed record TableState(string Tenant, string Table, long Rows, string Sha256);
    private sealed record VisitRow(Guid Id, Guid CommandId, string TenantId, string ActorId, string CustomerCode,
        string Outlet, string Outcome, string Reason, string Observation, DateTime CompletedAtUtc, string CommandKind);

    internal static async Task<TableState[]> Capture(DemoRun run)
    {
        var all = new List<TableState>();
        foreach (string tenant in DemoRun.Tenants)
        {
            await using var db = new SqlConnection(run.Connection(tenant));
            foreach (string table in Tables)
            {
                string[] images = await ReadImages(db, table);
                string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                    JsonSerializer.Serialize(images))));
                all.Add(new TableState(tenant, table, images.LongLength, hash));
            }
        }
        return all.ToArray();
    }

    private static async Task<string[]> ReadImages(SqlConnection db, string table) =>
        (await db.QueryAsync<string>($"""
            SELECT (SELECT source.* FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES)
            FROM dbo.[{table}] source
            """)).Order(StringComparer.Ordinal).ToArray();

    internal static async Task Verify(DemoRun run, string root, string source, string apiSha256, TableState[] before)
    {
        var after = await Capture(run);
        await using var a = new SqlConnection(run.Connection("tenant-a"));
        await using var b = new SqlConnection(run.Connection("tenant-b"));
        var rows = (await a.QueryAsync<VisitRow>("""
            SELECT v.Id,v.CommandId,v.TenantId,v.ActorId,c.Code AS CustomerCode,o.Name AS Outlet,
                   v.Outcome,v.Reason,v.Observation,v.CompletedAtUtc,k.Kind AS CommandKind
            FROM dbo.F01Visit v
            JOIN dbo.Customer c ON c.TenantId=v.TenantId AND c.Id=v.CustomerId
            JOIN dbo.C1Outlet o ON o.TenantId=v.TenantId AND o.CustomerId=v.CustomerId AND o.Id=v.OutletId
            JOIN dbo.C1Command k ON k.TenantId=v.TenantId AND k.ActorId=v.ActorId AND k.CommandId=v.CommandId
                AND k.ResultId=v.Id AND k.HttpStatus=200
            """)).AsList();
        int countA = await a.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.F01Visit");
        int countB = await b.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.F01Visit");
        var row = rows.Count == 1 ? rows[0] : null;
        var tableDiff = before.Zip(after, (prior, current) => new
        {
            prior.Tenant, prior.Table, ExpectedRowDelta = 0, RowDelta = current.Rows - prior.Rows,
            Identical = prior == current
        }).ToArray();
        bool passed = countA == 1 && countB == 0 && before.SequenceEqual(after) && row is
        {
            TenantId: "tenant-a", ActorId: "c1-dev-sales", CustomerCode: "C2-FIXTURE", Outlet: "Outlet Visit DEV",
            Outcome: "NO_ORDER", Reason: "Hẹn lần sau", Observation: "Có ghi chú", CommandKind: "VISIT_COMPLETE"
        };
        string output = Path.Combine(root, "TestResults", "dev-f01", run.Id);
        Directory.CreateDirectory(output);
        await File.WriteAllTextAsync(Path.Combine(output, "sql-result.json"), JsonSerializer.Serialize(new
        {
            RunId = run.Id, Source = source, ApiSha256 = apiSha256, FixtureVersion = "v1",
            FixtureSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                File.ReadAllText(Path.Combine(root, "web", "e2e-dev-f01", "visits.spec.ts")).Replace("\r\n", "\n")))),
            Case = "F01-01/F01-03", Acceptance = "AC-17", Status = passed ? "PASS" : "FAIL",
            Expected = new { CountA = 1, CountB = 0, RowDelta = 0 }, Before = before, After = after,
            Actual = new { CountA = countA, CountB = countB, Visits = rows }, Diff = tableDiff
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"DEV-F01 SQL {run.Id}: tenant rows {countA}/{countB}, unchanged snapshots " +
            $"{tableDiff.Count(d => d.Identical)}/{tableDiff.Length} ({output})");
        if (!passed) throw new InvalidOperationException("DEV-F01 visit SQL oracle did not match.");
    }
}
