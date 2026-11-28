using System.Diagnostics;
using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;

namespace FmcgDms.Demo;

internal static class App03WorkspaceEvidence
{
    internal static async Task Run(string root, string source, bool handoff = false, bool field = false, bool settlement = false)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("APP-03 requires Windows LocalDB.");
        var run = new DemoRun(Guid.NewGuid().ToString("N"));
        string profile = settlement ? "app03-6" : field ? "app03-5" : handoff ? "app03-4" : "app03-3";
        var evidence = Directory.CreateDirectory(Path.Combine(root, "TestResults", profile, run.Id));
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var ct = deadline.Token;
        Task? host = null;
        var catalogWitness = settlement
            ? App03SettlementSqlObserver.TryCreateContext(root, run.Id, evidence.FullName, source)
            : null;
        try
        {
            // .3 checks the original A/F workspace. .5 verifies the upgrade from that
            // same predecessor, while .4 exercises its A handoff on the current profile.
            var active = await Open("Start", startBeforeField: !handoff);
            App03FieldEvidence.UpgradeReview? upgrade = null;
            if (settlement)
            {
                var predecessor = await App03FieldEvidence.CheckPredecessor(run, active, ct, withRemainder: true);
                await Stop();
                active = await Open("Resume");
                await Stop();
                active = await Open("Resume", enableSettlement: true);
                upgrade = await App03FieldEvidence.CheckUpgrade(run, active, predecessor, ct);
                await App03SettlementEvidence.CheckGiftOnlyDraft(run, active, predecessor.Delivery.TripId, evidence.FullName, ct);
            }
            else if (field)
            {
                var predecessor = await App03FieldEvidence.CheckPredecessor(run, active, ct);
                await Stop();
                active = await Open("Resume", verifyField: true);
                upgrade = await App03FieldEvidence.CheckUpgrade(run, active, predecessor, ct);
            }
            if (handoff || field || settlement) await App03HandoffEvidence.Prepare(run, ct);
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
            if (settlement)
            {
                start.Environment["APP03_CASHIER_TOKEN"] = active.CashierToken;
                start.Environment["APP03_QA_TOKEN"] = active.QaToken;
            }
            start.Environment["APP03_EVIDENCE_DIR"] = evidence.FullName;
            using var browser = Process.Start(start) ?? throw new InvalidOperationException("Cannot start APP-03 browser verification.");
            try
            {
                if (!handoff && !field && !settlement) await RevokeSales(browser);
                await browser.WaitForExitAsync(ct);
                if (browser.ExitCode != 0) throw new InvalidOperationException($"APP-03 Chromium exited {browser.ExitCode}.");
                if (settlement)
                {
                    await using var observer = catalogWitness is null ? null
                        : await App03SettlementSqlObserver.Start(root, run.Id, evidence.FullName, source, ct);
                    await Stop();
                    await App03DevSession.Run("NextDay", root, source, run.Id,
                        businessDate: App03DayBootstrap.FirstDay.AddDays(1), settlement: true);
                    active = await Open("Resume", enableSettlement: true, enableCatalogWitness: catalogWitness is not null);
                    await App03SettlementEvidence.Review(run, active, evidence.FullName, source, upgrade!, ct);
                    var secondTrip = await App03SettlementEvidence.PrepareB(active, "DEV-20261124-A", load: true, ct, catalogWitness);
                    await Stop();
                    await App03DevSession.Run("NextDay", root, source, run.Id,
                        businessDate: App03DayBootstrap.FirstDay.AddDays(2), settlement: true);
                    active = await Open("Resume", enableSettlement: true, enableFinance: true, enableCatalogWitness: catalogWitness is not null);
                    var thirdTrip = await App03SettlementEvidence.PrepareB(active, "DEV-20261125-A", load: true, ct, catalogWitness);
                    await App03OwnerEvidence.ReviewTripCoverage(run, active, secondTrip, thirdTrip,
                        evidence.FullName, source, ct);
                    await App03SettlementEvidence.VerifyVehicleReuse(run, active, secondTrip, thirdTrip, evidence.FullName, ct);
                    await Stop();
                    await App03DevSession.Run("NextDay", root, source, run.Id,
                        businessDate: App03DayBootstrap.FirstDay.AddDays(3), settlement: true);
                    active = await Open("Resume", enableSettlement: true, enableFinance: true, enableCatalogWitness: catalogWitness is not null);
                    var quotaOnly = await App03SettlementEvidence.PrepareB(active, "DEV-20261126-A", load: false, ct, catalogWitness);
                    await App03SettlementEvidence.VerifyQuotaOnly(run, active, quotaOnly, evidence.FullName, ct);
                    await Stop();
                    active = await Open("Resume", enableSettlement: true, enableFinance: true);
                    await App03OwnerEvidence.ReviewSettlement(run, active, evidence.FullName, source, ct);
                }
                else if (field)
                {
                    await Stop();
                    await App03DevSession.Run("NextDay", root, source, run.Id,
                        businessDate: App03DayBootstrap.FirstDay.AddDays(1));
                    active = await Open("Resume");
                    var recovery = await App03FieldEvidence.CheckRecovery(run, active, ct);
                    await App03FieldEvidence.Review(run, evidence.FullName, source, recovery, upgrade!, ct);
                }
                else if (handoff) await App03HandoffEvidence.Review(run, evidence.FullName, source, ct);
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
            // The observer is disposed before this receipt; never change the business outcome.
            if (catalogWitness is not null) App03SettlementWitnessCoverage.Write(catalogWitness);
            try { await Stop(); }
            finally
            {
                if (App03DevSession.HasManifest(run.Id)) await App03DevSession.Run("Remove", root, source, run.Id);
            }
        }
        Console.WriteLine($"{(settlement ? "APP-03.6" : field ? "APP-03.5" : handoff ? "APP-03.4" : "APP-03.3")} workspace PASS: {evidence.FullName}");

        async Task<App03DevHost> Open(string operation, bool verifyField = false, bool startBeforeField = false,
            bool enableSettlement = false, bool enableFinance = false, bool enableCatalogWitness = false)
        {
            var ready = new TaskCompletionSource<App03DevHost>(TaskCreationOptions.RunContinuationsAsynchronously);
            host = App03DevSession.Run(operation, root, source, run.Id, active =>
            {
                ready.SetResult(active);
                return Task.CompletedTask;
            }, verifyHandoff: handoff, verifyField: verifyField, startBeforeField: startBeforeField,
                settlement: enableSettlement, finance: enableFinance, catalogWitness: enableCatalogWitness);
            if (await Task.WhenAny(ready.Task, host).WaitAsync(ct) == host) await host;
            return await ready.Task.WaitAsync(ct);
        }

        async Task Stop()
        {
            if (host is null) return;
            if (!host.IsCompleted && EventWaitHandle.TryOpenExisting($@"Local\FmcgDms.APP03.{run.Id}", out var stop))
            {
                using (stop) stop.Set();
            }
            Task ending = host;
            host = null;
            await ending;
        }

        async Task RevokeSales(Process browser)
        {
            var checkpoint = Path.Combine(evidence.FullName, "revoke.request");
            while (!File.Exists(checkpoint) && !browser.HasExited && host is { IsCompleted: false })
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
