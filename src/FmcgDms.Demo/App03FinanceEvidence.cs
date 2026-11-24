using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using FmcgDms.Api.Persistence;
using Microsoft.Data.SqlClient;

namespace FmcgDms.Demo;

internal static class App03FinanceEvidence
{
    internal static async Task Run(string root, string source)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("APP-03 requires Windows LocalDB.");
        var run = new DemoRun(Guid.NewGuid().ToString("N"));
        string folder = Directory.CreateDirectory(Path.Combine(root, "TestResults", "app03-7", run.Id)).FullName;
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(6));
        var ct = deadline.Token;
        Task? host = null;
        try
        {
            var active = await Open("Start", finance: false);
            var predecessor = await App03FieldEvidence.CheckPredecessor(run, active, ct, withRemainder: true, beforeField: false);
            await Stop();
            active = await Open("Resume", finance: true);
            Guid delivery = await CreateDelivery(active, run, ct);
            await Browser(active, delivery, "refund");
            await Stop();

            var november = await Capture();
            Require(november.Primary, new Review(2, 1, 2, 0, 0, 1, 10000, 4000, 96000, 6000, 0, 0, 0, 0), "November");
            Require(november.Peer, new Review(0, 0, 0, 0, 0, 0, 0, 0, 100000, 0, 0, 0, 0, 0), "peer November");

            await Advance(App03DayBootstrap.FirstDay.AddDays(1));
            active = await Open("Resume", finance: true);
            await Browser(active, delivery, "accounting");
            await Stop();

            var posted = await Capture();
            Require(posted.Primary, november.Primary with { Posting = 2 }, "accounting");
            Require(posted.Peer, november.Peer, "peer accounting");

            await Advance(App03DayBootstrap.FirstDay.AddDays(2));
            active = await Open("Resume", finance: true);
            await Browser(active, delivery, "approval");
            await Stop();
            var beforeRollover = await Capture();
            Require(beforeRollover.Primary, posted.Primary with { Correction = 1 }, "partner committed before rollover");
            Require(beforeRollover.Peer, november.Peer, "peer approval");
            var pendingApproval = await CorrectionState();
            if (pendingApproval is not { Cases: 1, ClosedCases: 0, ApprovalPosts: 1, AppPostingId: null, PartnerPostingId: not null,
                    PartnerAckLost: 1, ClosedReportId: null })
                throw new InvalidOperationException($"APP-03.7 expected committed partner correction with unknown app outcome: {pendingApproval}.");

            await AddHistoricalAlias();

            for (var day = App03DayBootstrap.FirstDay.AddDays(3); day <= App03DevSession.FinanceRollover; day = day.AddDays(1))
                await Advance(day);
            active = await Open("Resume", finance: true);
            await Browser(active, delivery, "recovery");
            var recovered = await CorrectionState();
            if (recovered is not { Cases: 2, ClosedCases: 2, ApprovalPosts: 1, PartnerAckLost: 1, ClosedReportId: not null }
                || recovered.AppPostingId != pendingApproval.PartnerPostingId
                || recovered.PartnerPostingId != pendingApproval.PartnerPostingId
                || recovered.ClosedReportId != recovered.FreshReportId)
                throw new InvalidOperationException($"APP-03.7 correction recovery or closure changed identities: {recovered}.");
            var original = await Original(delivery);
            await Browser(active, delivery, "cost");
            var december = await Capture();
            Require(december.Primary, beforeRollover.Primary with { Closed = 2, LockedPeriod = 1, CostAdjustment = 1, PriorAdjustment = 100 }, "December");
            Require(december.Peer, november.Peer with { LockedPeriod = 1 }, "peer December");
            var originalAfter = await Original(delivery);
            if (original != originalAfter || original is not { Outstanding: 0, SaleCost: 6000, ReleasedCost: 3000,
                    Gross: 20000, CorrectedGross: 20000, CorrectedAr: 20000, CorrectedCost: 6000, CorrectedQuantity: 2,
                    FinanceActors: 4, AccountingActors: 2, Reports: 4 })
                throw new InvalidOperationException($"APP-03.7 original source mismatch: before {original}; after {originalAfter}.");

            await File.WriteAllTextAsync(Path.Combine(folder, "sql-review.json"),
                JsonSerializer.Serialize(new { source, runId = run.Id, predecessor = predecessor.Ar.SourceId, delivery, november,
                    posted, beforeRollover, pendingApproval, recovered, december, original, originalAfter }, new JsonSerializerOptions { WriteIndented = true }), ct);
        }
        finally
        {
            try { await Stop(); }
            finally { if (App03DevSession.HasManifest(run.Id)) await App03DevSession.Run("Remove", root, source, run.Id); }
        }
        Console.WriteLine($"APP-03.7 Finance PASS: {folder}");

        async Task<App03DevHost> Open(string operation, bool finance)
        {
            var ready = new TaskCompletionSource<App03DevHost>(TaskCreationOptions.RunContinuationsAsynchronously);
            host = App03DevSession.Run(operation, root, source, run.Id, active =>
            {
                ready.SetResult(active);
                return Task.CompletedTask;
            }, settlement: true, finance: finance);
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

        Task Advance(DateOnly day) => App03DevSession.Run("NextDay", root, source, run.Id, businessDate: day, settlement: true, finance: true);

        async Task Browser(App03DevHost active, Guid delivery, string stage)
        {
            var start = new ProcessStartInfo("node")
            {
                WorkingDirectory = Path.Combine(root, "web"), UseShellExecute = false, CreateNoWindow = true
            };
            foreach (var arg in new[] { "node_modules/@playwright/test/cli.js", "test", "--config", "playwright.app03-7.config.ts", "--grep", stage })
                start.ArgumentList.Add(arg);
            start.Environment["APP02C1_WEB_URL"] = active.WebUrl;
            start.Environment["APP02C1_RUN_ID"] = run.Id;
            start.Environment["APP03_FINANCE_STAGE"] = stage;
            start.Environment["APP03_FINANCE_EVIDENCE_DIR"] = folder;
            if (stage == "recovery")
                start.Environment["APP03_FINANCE_PENDING"] = await File.ReadAllTextAsync(Path.Combine(folder, "pending-approval.json"), ct);
            start.Environment["APP03_FINANCE_DELIVERY"] = delivery.ToString();
            start.Environment["APP03_SOURCE_TOKEN"] = active.SourceToken;
            start.Environment["APP03_RECEIVER_TOKEN"] = active.ReceiverToken;
            start.Environment["APP03_FINANCE_TOKEN"] = active.FinanceToken;
            start.Environment["APP03_QA_TOKEN"] = active.QaToken;
            start.Environment["APP03_CASHIER_TOKEN"] = active.CashierToken;
            foreach (var (role, token) in active.FinanceCredentials!)
                start.Environment["APP03_" + role + "_TOKEN"] = token;
            using var browser = Process.Start(start) ?? throw new InvalidOperationException("Cannot start APP-03.7 Chromium.");
            try
            {
                await browser.WaitForExitAsync(ct);
                if (browser.ExitCode != 0) throw new InvalidOperationException($"APP-03.7 stage {stage} exited {browser.ExitCode}.");
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

        async Task<Snapshot> Capture() => new(await ReadSql("tenant-a"), await ReadSql("tenant-b"));

        async Task AddHistoricalAlias()
        {
            // Simulate a case already recorded by the earlier Finance runtime for the same posting.
            await run.Verify("tenant-a", app03Session: true);
            await using var db = new SqlConnection(run.Connection("tenant-a"));
            int inserted = await db.ExecuteAsync(new CommandDefinition("""
                INSERT d4app.AccountingCase(Id,ReportId,SourceLineId,OwnerRole,NextAction)
                SELECT NEWID(),other.ReportId,other.SourceLineId,'reconciler','REVIEW_POSTING_DIFFERENCE'
                FROM d4app.AccountingCase c
                JOIN d4app.AccountingReportLine original ON original.ReportId=c.ReportId AND original.SourceLineId=c.SourceLineId
                JOIN d4app.AccountingReportLine other ON other.ReportId<>c.ReportId AND other.SourceLineId=original.SourceLineId
                    AND other.PostingId=original.PostingId AND other.SourceHash=original.SourceHash AND other.Reason=original.Reason;
                """, cancellationToken: ct));
            if (inserted != 1) throw new InvalidOperationException($"Expected one historical case alias, got {inserted}.");
        }

        async Task<ApprovalState> CorrectionState()
        {
            await run.Verify("tenant-a", app03Session: true);
            await using var db = new SqlConnection(run.Connection("tenant-a"));
            return await db.QuerySingleAsync<ApprovalState>(new CommandDefinition("""
                SELECT c.PostingId AS AppPostingId,p.Id AS PartnerPostingId,x.ClosedReportId,
                    (SELECT TOP(1) Id FROM d4app.AccountingReport WHERE Status='RECONCILED_OK'
                        ORDER BY AsOf DESC,FinanceSequence DESC) AS FreshReportId,
                    (SELECT COUNT(*) FROM d4app.AccountingCase) AS Cases,
                    (SELECT COUNT(*) FROM d4app.AccountingCase WHERE ClosedReportId=x.ClosedReportId) AS ClosedCases,
                    (SELECT COUNT(*) FROM d4accounting.Posting WHERE Kind='CORRECTION' AND ApprovalId=c.ApprovalId) AS ApprovalPosts,
                    (SELECT COUNT(*) FROM d4accounting.Journal WHERE Operation='CORRECTION'
                        AND Outcome='COMMITTED_NO_ACK' AND SourceLineId=c.SourceLineId) AS PartnerAckLost
                FROM d4app.AccountingCorrection c
                JOIN d4app.AccountingCase x ON x.Id=c.CaseId
                LEFT JOIN d4accounting.Posting p ON p.CorrectionId=c.Id;
                """, cancellationToken: ct));
        }

        async Task<OriginalSource> Original(Guid delivery)
        {
            await run.Verify("tenant-a", app03Session: true);
            await using var db = new SqlConnection(run.Connection("tenant-a"));
            return await db.QuerySingleAsync<OriginalSource>(new CommandDefinition("""
                SELECT i.OutstandingAmount AS Outstanding,d.CostAmount AS SaleCost,release.AcceptedCost AS ReleasedCost,
                    price.GrossAmount AS Gross,posting.Gross AS CorrectedGross,posting.Ar AS CorrectedAr,
                    posting.Cost AS CorrectedCost,posting.Quantity AS CorrectedQuantity,
                    (SELECT COUNT(DISTINCT actor.Id) FROM (VALUES(r.ActorId),(a.ActorId),(w.ActorId),(p.ActorId)) actor(Id)) AS FinanceActors,
                    (SELECT COUNT(DISTINCT actor.Id) FROM (VALUES(c.Maker),(c.Checker)) actor(Id)) AS AccountingActors,
                    (SELECT COUNT(*) FROM d4app.AccountingReport) AS Reports,
                    CONVERT(varchar(64),HASHBYTES('SHA2_256',(
                        SELECT (SELECT d.* FOR JSON PATH,WITHOUT_ARRAY_WRAPPER) AS Sale,
                            (SELECT i.* FOR JSON PATH,WITHOUT_ARRAY_WRAPPER) AS Receivable,
                            (SELECT price.* FOR JSON PATH,WITHOUT_ARRAY_WRAPPER) AS Price,
                            (SELECT release.* FOR JSON PATH,WITHOUT_ARRAY_WRAPPER) AS AcceptedReturn
                        FOR JSON PATH,WITHOUT_ARRAY_WRAPPER
                    )),2) AS Digest
                FROM dbo.SaleDelivery d
                JOIN dbo.ReceivableOpenItem i ON i.TenantId=d.TenantId AND i.SaleDeliveryId=d.Id
                JOIN dbo.F04DeliveryPricing price ON price.TenantId=d.TenantId AND price.DeliveryId=d.Id
                JOIN dbo.F05ReturnCase x ON x.TenantId=d.TenantId AND x.DeliveryId=d.Id
                JOIN dbo.F05ReturnStage release ON release.TenantId=x.TenantId AND release.CaseId=x.Id AND release.Stage='RELEASE'
                JOIN dbo.F08ReturnCredit credit ON credit.TenantId=x.TenantId AND credit.CaseId=x.Id
                JOIN dbo.F08Refund r ON r.TenantId=credit.TenantId AND r.CreditId=credit.Id
                JOIN dbo.F08RefundApproval a ON a.TenantId=r.TenantId AND a.RefundId=r.Id
                JOIN dbo.F08PayoutWitness w ON w.TenantId=r.TenantId AND w.RefundId=r.Id
                JOIN dbo.F08Disbursement p ON p.TenantId=r.TenantId AND p.RefundId=r.Id
                JOIN d4app.AccountingCorrection c ON c.SourceLineId=d.BusinessSourceLineId
                CROSS APPLY (SELECT SUM(Gross) AS Gross,SUM(Ar) AS Ar,SUM(Cost) AS Cost,SUM(Quantity) AS Quantity
                    FROM d4accounting.Posting WHERE SourceLineId=d.BusinessSourceLineId) posting
                WHERE d.TenantId='tenant-a' AND d.Id=@delivery;
                """, new { delivery }, cancellationToken: ct));
        }

        async Task<Review> ReadSql(string tenant)
        {
            await run.Verify(tenant, app03Session: true);
            await using var db = new SqlConnection(run.Connection(tenant));
            return await db.QuerySingleAsync<Review>(new CommandDefinition("""
                SELECT (SELECT COUNT(*) FROM dbo.SaleDelivery) AS Deliveries,
                    (SELECT COUNT(*) FROM dbo.F05ReturnStage WHERE Stage='RELEASE') AS Releases,
                    (SELECT COUNT(*) FROM d4accounting.Receipt) AS Receipt,
                    (SELECT COUNT(*) FROM d4accounting.Posting WHERE Kind='BASE') AS Posting,
                    (SELECT COUNT(*) FROM d4accounting.Posting WHERE Kind='CORRECTION') AS Correction,
                    (SELECT COUNT(*) FROM dbo.F08Disbursement) AS Disbursement,
                    (SELECT COALESCE(SUM(GrossAmount),0) FROM dbo.F08ReturnCredit) AS Credit,
                    (SELECT COALESCE(SUM(Amount),0) FROM dbo.F08Disbursement) AS Paid,
                    (SELECT AvailableAmount FROM dbo.F08Treasury WHERE TenantId=@tenant) AS Treasury,
                    (SELECT COALESCE(SUM(d.CostAmount),0) FROM dbo.SaleDelivery d JOIN dbo.Sku s ON s.Id=d.SkuId
                        WHERE s.Code='Z-REFUND-EA') AS OriginalCost,
                    (SELECT COUNT(*) FROM d4app.AccountingCase WHERE ClosedReportId IS NOT NULL) AS Closed,
                    (SELECT COUNT(*) FROM dbo.F06Period WHERE State='LOCKED') AS LockedPeriod,
                    (SELECT COUNT(*) FROM dbo.F06CostAdjustment) AS CostAdjustment,
                    (SELECT COALESCE(SUM(PriorAdjustment),0) FROM dbo.F06CostAdjustment) AS PriorAdjustment;
                """, new { tenant }, cancellationToken: ct));
        }
    }

    private static async Task<Guid> CreateDelivery(App03DevHost host, DemoRun run, CancellationToken ct)
    {
        const string c1 = "/api/synthetic/app02c1", c2 = "/api/synthetic/app02c2", c3 = "/api/synthetic/app02c3", c4 = "/api/synthetic/app02c4";
        var catalog = await Send<C2Catalog>(c2 + "/catalog");
        var customer = catalog.Customers.Single(row => row.Label.StartsWith("C2-FIXTURE", StringComparison.Ordinal));
        var trip = catalog.Trips.Single(row => row.Label == "DEV-20261123-B");
        var sku = catalog.Skus.Single(row => row.Label.StartsWith("Z-REFUND-EA ·", StringComparison.Ordinal));
        var warehouse = catalog.Warehouses.Single(row => row.Label == "C2-MAIN");
        var detail = await Send<C1Detail>(c1 + $"/customers/{customer.Id}");
        var address = detail.RegisteredShipTos.Single();
        var shipTo = await Send<C1Outcome>(c1 + "/ship-to-selections",
            new C1SelectionInput(Guid.NewGuid(), customer.Id, null, address.Id, address.Revision, null));
        Guid order = (await Command(c2 + "/drafts",
            new C2DraftInput(Guid.NewGuid(), customer.Id, trip.Id, shipTo.Id!.Value, [new(sku.Id, 2)]))).SourceId!.Value;
        Guid confirmation = (await Command(c2 + "/confirm", new C2ConfirmInput(Guid.NewGuid(), order))).SourceId!.Value;
        var line = (await Send<C2OrderView[]>(c2 + "/orders")).Single(row => row.Id == order).Lines.Single().LineId!.Value;
        await Command(c2 + "/reserve", new C2ReserveInput(Guid.NewGuid(), order, line, warehouse.Id, 2, confirmation), host.ReceiverToken);
        var right = (await Send<Right[]>(c3 + $"/trips/{trip.Id}/rights", token: host.ReceiverToken)).Single();
        await Command(c3 + "/load", new C3LoadInput(Guid.NewGuid(), trip.Id, [new(right.Id, 2, 2)]), host.ReceiverToken);
        var loaded = (await Send<DeliveryLine[]>(c4 + $"/trips/{trip.Id}/lines")).Single();
        var delivery = await Command(c4 + "/deliver", new C4DeliveryInput(Guid.NewGuid(), trip.Id, loaded.Id, loaded.ShipToVersion, 2));
        await run.Verify("tenant-a", app03Session: true);
        await using var db = new SqlConnection(run.Connection("tenant-a"));
        return await db.QuerySingleAsync<Guid>(new CommandDefinition(
            "SELECT Id FROM dbo.SaleDelivery WHERE TenantId='tenant-a' AND BusinessSourceId=@SourceId",
            new { delivery.SourceId }, cancellationToken: ct));

        async Task<BusinessCommandOutcome> Command(string path, object body, string? token = null)
        {
            var outcome = await Send<BusinessCommandOutcome>(path, body, token);
            if (outcome.Status != "APPLIED") throw new InvalidOperationException($"{path}: {outcome.ReasonCode}");
            return outcome;
        }

        async Task<T> Send<T>(string path, object? body = null, string? token = null)
        {
            using var request = new HttpRequestMessage(body is null ? HttpMethod.Get : HttpMethod.Post, host.WebUrl + path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token ?? host.SourceToken);
            if (body is not null) request.Content = JsonContent.Create(body);
            using var response = await host.Client.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"{path}: {response.StatusCode} {await response.Content.ReadAsStringAsync(ct)}");
            return (await response.Content.ReadFromJsonAsync<T>(ct))!;
        }
    }

    private static void Require(Review actual, Review expected, string scope)
    {
        if (actual != expected) throw new InvalidOperationException($"APP-03.7 {scope}: expected {expected}; got {actual}.");
    }

    private sealed record Right(Guid Id);
    private sealed record DeliveryLine(Guid Id, int ShipToVersion);
    private sealed record Snapshot(Review Primary, Review Peer);
    private sealed record ApprovalState(Guid? AppPostingId, Guid? PartnerPostingId, Guid? ClosedReportId,
        Guid? FreshReportId, int Cases, int ClosedCases, int ApprovalPosts, int PartnerAckLost);
    private sealed record OriginalSource(decimal Outstanding, decimal SaleCost, decimal ReleasedCost,
        long Gross, decimal CorrectedGross, decimal CorrectedAr, decimal CorrectedCost, decimal CorrectedQuantity,
        int FinanceActors, int AccountingActors, int Reports, string Digest);
    private sealed record Review(int Deliveries, int Releases, int Receipt, int Posting, int Correction, int Disbursement,
        long Credit, long Paid, long Treasury, decimal OriginalCost, int Closed, int LockedPeriod, int CostAdjustment, long PriorAdjustment);
}
