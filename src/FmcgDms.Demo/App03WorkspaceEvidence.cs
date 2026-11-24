using System.Diagnostics;
using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;

namespace FmcgDms.Demo;

internal static class App03WorkspaceEvidence
{
    internal static async Task Run(string root, string source, bool handoff = false)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("APP-03 requires Windows LocalDB.");
        var run = new DemoRun(Guid.NewGuid().ToString("N"));
        string profile = handoff ? "app03-4" : "app03-3";
        var evidence = Directory.CreateDirectory(Path.Combine(root, "TestResults", profile, run.Id));
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var ct = deadline.Token;
        var ready = new TaskCompletionSource<App03DevHost>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task host = App03DevSession.Run("Start", root, source, run.Id, active =>
        {
            ready.SetResult(active);
            return Task.CompletedTask;
        });
        try
        {
            if (await Task.WhenAny(ready.Task, host).WaitAsync(ct) == host) await host;
            var active = await ready.Task.WaitAsync(ct);
            if (handoff) await App03HandoffEvidence.Prepare(run, ct);
            var start = new ProcessStartInfo("node")
            {
                WorkingDirectory = Path.Combine(root, "web"), UseShellExecute = false, CreateNoWindow = true
            };
            foreach (string arg in new[] { "node_modules/@playwright/test/cli.js", "test", "--config", $"playwright.{profile}.config.ts" })
                start.ArgumentList.Add(arg);
            start.Environment["APP02C1_WEB_URL"] = active.WebUrl;
            start.Environment["APP02C1_RUN_ID"] = run.Id;
            start.Environment["APP03_SOURCE_TOKEN"] = active.SourceToken;
            start.Environment["APP03_PEER_TOKEN"] = active.PeerToken;
            start.Environment["APP03_FINANCE_TOKEN"] = active.FinanceToken;
            start.Environment["APP03_EXPIRED_TOKEN"] = active.ExpiredToken;
            start.Environment["APP03_RECEIVER_TOKEN"] = active.ReceiverToken;
            start.Environment["APP03_EVIDENCE_DIR"] = evidence.FullName;
            using var browser = Process.Start(start) ?? throw new InvalidOperationException("Cannot start APP-03 browser verification.");
            try
            {
                if (!handoff) await RevokeSales(browser);
                await browser.WaitForExitAsync(ct);
                if (browser.ExitCode != 0) throw new InvalidOperationException($"APP-03 Chromium exited {browser.ExitCode}.");
                if (handoff) await App03HandoffEvidence.Review(run, evidence.FullName, source, ct);
                else await ReviewWorkspace();
            }
            finally
            {
                if (!browser.HasExited)
                {
                    browser.Kill(entireProcessTree: true);
                    await browser.WaitForExitAsync(CancellationToken.None);
                }
            }
        }
        finally
        {
            if (!host.IsCompleted && EventWaitHandle.TryOpenExisting($@"Local\FmcgDms.APP03.{run.Id}", out var stop))
            {
                using (stop) stop.Set();
            }
            try { await host; }
            finally
            {
                if (App03DevSession.HasManifest(run.Id)) await App03DevSession.Run("Remove", root, source, run.Id);
            }
        }
        Console.WriteLine($"{(handoff ? "APP-03.4" : "APP-03.3")} workspace PASS: {evidence.FullName}");

        async Task RevokeSales(Process browser)
        {
            var checkpoint = Path.Combine(evidence.FullName, "revoke.request");
            while (!File.Exists(checkpoint) && !browser.HasExited && !host.IsCompleted)
                await Task.Delay(150, ct);
            if (!File.Exists(checkpoint) || await File.ReadAllTextAsync(checkpoint, ct) != run.Id)
                throw new InvalidOperationException("APP-03 Chromium did not reach the revocation checkpoint.");
            await run.Verify("tenant-a", app03Session: true);
            await using var db = new SqlConnection(run.Connection("tenant-a"));
            const string revoke = """
                UPDATE dbo.App02ActorRoleGrant SET IsActive=0, ChangedBy=N'app03-workspace-verifier'
                WHERE TenantId=N'tenant-a' AND ActorId=@Actor AND RoleCode=N'sales' AND IsActive=1
                """;
            if (await db.ExecuteAsync(new CommandDefinition(revoke, new { App03DayBootstrap.Actor }, cancellationToken: ct)) != 1)
                throw new InvalidOperationException("Expected one active source Sales grant.");
            await File.WriteAllTextAsync(Path.Combine(evidence.FullName, "revoke.ready"), run.Id, ct);
        }

        async Task ReviewWorkspace()
        {
            var primary = await Read("tenant-a");
            var peer = await Read("tenant-b");
            if (primary != new Counts(1, 1, 1, 0) || peer != new Counts(0, 0, 0, 0))
                throw new InvalidOperationException($"Unexpected APP-03 SQL state: primary={primary}, peer={peer}.");
            await File.WriteAllTextAsync(Path.Combine(evidence.FullName, "sql-review.json"),
                JsonSerializer.Serialize(new { source, runId = run.Id, primary, peer },
                    new JsonSerializerOptions { WriteIndented = true }), ct);
        }

        async Task<Counts> Read(string tenant)
        {
            await run.Verify(tenant, app03Session: true);
            await using var connection = new SqlConnection(run.Connection(tenant));
            return await connection.QuerySingleAsync<Counts>(new CommandDefinition("""
                SELECT (SELECT COUNT(*) FROM dbo.AOrder WHERE TenantId=@Tenant) AS Orders,
                    (SELECT COUNT(*) FROM dbo.SaleDelivery WHERE TenantId=@Tenant) AS Deliveries,
                    (SELECT COUNT(*) FROM dbo.F05ReturnCase WHERE TenantId=@Tenant) AS ReturnCases,
                    (SELECT COUNT(*) FROM dbo.F05ReturnStage WHERE TenantId=@Tenant) AS ReturnStages
                """, new { Tenant = tenant }, cancellationToken: ct));
        }
    }

    private sealed record Counts(int Orders, int Deliveries, int ReturnCases, int ReturnStages);
}
