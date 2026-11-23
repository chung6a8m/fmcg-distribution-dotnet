using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;

namespace FmcgDms.Demo;

internal static class DevF02Evidence
{
    private static readonly string[] Tables = ["ReceivableOpenItem", "CashReceipt", "ReceiptAllocation", "LedgerEffect"];
    private sealed record Step(string StepCode, long Issued, long Expenses, long Accepted, long Outstanding);
    private sealed record Line(Guid Id, Guid? ReferenceId, string Kind, string ActorId, string Evidence, long Amount,
        string? DecisionReason);

    internal static Task<DevF01Evidence.TableState[]> Capture(DemoRun run) => DevF01Evidence.Capture(run, Tables);

    internal static async Task Verify(DemoRun run, string root, string source, string apiSha256,
        DevF01Evidence.TableState[] before)
    {
        string oraclePath = Path.Combine(root, "business", "reference-data", "expected-results", "golden-oracles.json");
        string oracleText = await File.ReadAllTextAsync(oraclePath);
        using var oracle = JsonDocument.Parse(oracleText);
        var expected = oracle.RootElement.GetProperty("AC-20").GetProperty("steps").EnumerateArray()
            .Select(row => new Step(row[0].GetString()!, row[1].GetInt64(), row[2].GetInt64(), row[3].GetInt64(),
                row[4].GetInt64())).ToArray();

        var after = await Capture(run);
        await using var a = new SqlConnection(run.Connection("tenant-a"));
        await using var b = new SqlConnection(run.Connection("tenant-b"));
        var history = (await a.QueryAsync<Step>("""
            WITH Source AS (
                SELECT Id,OccurredAtUtc,'ISSUE' AS Kind,Issued AS Amount,Evidence FROM dbo.F02Advance
                UNION ALL
                SELECT Id,OccurredAtUtc,Kind,Amount,Evidence FROM dbo.F02AdvanceEntry
            ), Totals AS (
                SELECT Id, Kind,Evidence,OccurredAtUtc,
                    SUM(CASE WHEN Kind='ISSUE' THEN Amount ELSE 0 END) OVER(ORDER BY OccurredAtUtc,Id) AS Issued,
                    SUM(CASE WHEN Kind='EXPENSE_APPROVE' THEN Amount ELSE 0 END) OVER(ORDER BY OccurredAtUtc,Id) AS Expenses,
                    SUM(CASE WHEN Kind='RETURN_ACCEPT' THEN Amount ELSE 0 END) OVER(ORDER BY OccurredAtUtc,Id) AS Accepted
                FROM Source
            )
            SELECT CASE Kind WHEN 'EXPENSE_APPROVE' THEN 'EXPENSE' WHEN 'RETURN_OFFER' THEN 'OFFER'
                    WHEN 'RETURN_ACCEPT' THEN CASE Evidence WHEN 'DEV-F02-COUNT-1' THEN 'ACCEPT_1'
                    WHEN 'DEV-F02-COUNT-2' THEN 'ACCEPT_2' END
                    ELSE Kind END AS StepCode,
                Issued,Expenses,Accepted,Issued-Expenses-Accepted AS Outstanding
            FROM Totals ORDER BY OccurredAtUtc,Id
            """)).ToArray();
        var lines = (await a.QueryAsync<Line>("""
            SELECT Id,ReferenceId,Kind,ActorId,Evidence,Amount,DecisionReason FROM dbo.F02AdvanceEntry ORDER BY OccurredAtUtc,Id
            """)).ToArray();
        int issued = await a.ExecuteScalarAsync<int>("""
            SELECT COUNT(*) FROM dbo.F02Advance WHERE TenantId=N'tenant-a' AND ActorId=N'f02-finance'
                AND EmployeeId=N'c1-dev-sales' AND Evidence=N'DEV-F02-ISSUE' AND Currency='VND'
            """);
        int other = await b.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.F02Advance");
        var request = lines.SingleOrDefault(line => line.Kind == "EXPENSE_REQUEST");
        var offer = lines.SingleOrDefault(line => line.Kind == "RETURN_OFFER");
        bool lineage = issued == 1 && other == 0 && lines.Length == 5
            && request is { ActorId: "c1-dev-sales", Amount: 300000 }
            && offer is { ActorId: "c1-dev-sales", Amount: 200000 }
            && lines.Count(line => line is { Kind: "EXPENSE_APPROVE", ActorId: "f02-finance", Amount: 300000 }
                                   && line.DecisionReason == "Đã đối chiếu chứng từ DEV" && line.ReferenceId == request.Id) == 1
            && lines.Count(line => line is { Kind: "RETURN_ACCEPT", ActorId: "f02-cashier", Amount: 100000 }
                                   && line.DecisionReason == "Đã đối chiếu chứng từ DEV" && line.ReferenceId == offer.Id) == 2;
        var actual = history.Where(row => row.StepCode != "EXPENSE_REQUEST").ToArray();
        bool passed = lineage && before.SequenceEqual(after) && expected.SequenceEqual(actual)
            && Equals(history.SingleOrDefault(step => step.StepCode == "EXPENSE_REQUEST"),
                new Step("EXPENSE_REQUEST", 500000, 0, 0, 500000));
        string output = Path.Combine(root, "TestResults", "dev-f02", run.Id);
        Directory.CreateDirectory(output);
        await File.WriteAllTextAsync(Path.Combine(output, "sql-result.json"), JsonSerializer.Serialize(new
        {
            RunId = run.Id, Source = source, ApiSha256 = apiSha256, Case = "F02-04", Acceptance = "AC-20",
            FixtureVersion = "v1", Status = passed ? "PASS" : "FAIL",
            OracleSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(oracleText.Replace("\r\n", "\n")))),
            Expected = expected, Actual = history, Lines = lines, Lineage = lineage, TenantBAdvances = other,
            Diff = expected.Zip(actual, (e, v) => new { e.StepCode, v = v.Outstanding - e.Outstanding }),
            Before = before, After = after
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"DEV-F02 SQL {run.Id}: outstanding {string.Join(" -> ", actual.Select(r => r.Outstanding))}; " +
            $"unchanged snapshots {before.Zip(after).Count(pair => pair.First == pair.Second)}/{before.Length} ({output})");
        if (!passed) throw new InvalidOperationException("DEV-F02 business oracle did not match.");
    }
}
