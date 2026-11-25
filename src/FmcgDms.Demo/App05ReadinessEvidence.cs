using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;

namespace FmcgDms.Demo;

[SupportedOSPlatform("windows")]
internal static class App05ReadinessEvidence
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    internal static async Task Run(string root, string source)
    {
        var run = new DemoRun(Guid.NewGuid().ToString("N"));
        string folder = Directory.CreateDirectory(Path.Combine(root, "TestResults", "app05-1", run.Id)).FullName;
        Console.WriteLine($"APP-05.1 evidence: {folder}");
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(6));
        var ct = deadline.Token;
        var checks = new List<Comparison>();
        Task? session = null;
        try
        {
            var active = await Open("Start");
            var initial = await Image(run, "tenant-a", ct);
            var peer = await Image(run, "tenant-b", ct);
            Compare("bootstrap", "tenant-a", 60m, initial.Main);
            Compare("bootstrap", "tenant-a", 180000m, initial.MainValue);
            Compare("bootstrap", "tenant-b", 60m, peer.Main);
            Compare("bootstrap", "tenant-b", 180000m, peer.MainValue);
            Compare("bootstrap assignments", "tenant-a", 0, initial.Assignments);
            Compare("bootstrap assignments", "tenant-b", 0, peer.Assignments);

            var start = new ProcessStartInfo("node")
            {
                WorkingDirectory = Path.Combine(root, "web"), UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            foreach (var argument in new[] { "node_modules/@playwright/test/cli.js", "test", "--config", "playwright.app05.config.ts" })
                start.ArgumentList.Add(argument);
            start.Environment["APP02C1_WEB_URL"] = active.WebUrl;
            start.Environment["APP02C1_RUN_ID"] = run.Id;
            start.Environment["APP05_EVIDENCE_DIR"] = folder;
            foreach (var (role, token) in active.CloseCredentials!)
                start.Environment["APP03_" + role + "_TOKEN"] = token;
            start.Environment["APP03_ACCOUNTANT_TOKEN"] = active.FinanceCredentials!["ACCOUNTANT"];
            start.Environment["APP03_SOURCE_TOKEN"] = active.SourceToken;
            start.Environment["APP03_RECEIVER_TOKEN"] = active.ReceiverToken;

            using var browser = Process.Start(start) ?? throw new InvalidOperationException("Cannot start APP-05.1 Chromium.");
            var stdout = browser.StandardOutput.ReadToEndAsync();
            var stderr = browser.StandardError.ReadToEndAsync();
            var exited = browser.WaitForExitAsync();
            try
            {
                bool allCheckpoints = await Coordinate(exited, peer);
                await exited.WaitAsync(ct);
                if (browser.ExitCode != 0 || !allCheckpoints)
                    throw new InvalidOperationException($"APP-05.1 Chromium exited {browser.ExitCode}, SQL checkpoints complete: {allCheckpoints}. See {folder}.");
            }
            finally
            {
                if (!browser.HasExited)
                {
                    browser.Kill(entireProcessTree: true);
                    await exited;
                }
                await File.WriteAllTextAsync(Path.Combine(folder, "browser.log"), await stdout + Environment.NewLine + await stderr);
            }

            await Stop();
            await VerifyVehicleAndCutoff();
        }
        finally
        {
            try { await Stop(); }
            finally
            {
                if (App03DevSession.HasManifest(run.Id))
                    await App03DevSession.Run("Remove", root, source, run.Id);
                await File.WriteAllTextAsync(Path.Combine(folder, "expected-actual-diff.json"), JsonSerializer.Serialize(checks, Json));
            }
        }

        var artifacts = new List<Artifact>();
        foreach (var path in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            await using var content = File.OpenRead(path);
            artifacts.Add(new(Path.GetRelativePath(folder, path), Convert.ToHexString(await SHA256.HashDataAsync(content, ct))));
        }
        await File.WriteAllTextAsync(Path.Combine(folder, "run.json"), JsonSerializer.Serialize(new
        {
            source, tree = Environment.GetEnvironmentVariable("APP05_EVIDENCE_TREE"), runId = run.Id,
            command = "pwsh -NoProfile -File ./scripts/Run-App03Dev.ps1 -Action VerifyCloseReadiness",
            os = Environment.OSVersion.VersionString, runtime = Environment.Version.ToString(),
            instance = DemoRun.Instance, scope = "synthetic DEV", result = "PASS", artifacts
        }, Json), ct);
        Console.WriteLine($"APP-05.1 UI/API/SQL readiness verified: {folder}");

        async Task<App03DevHost> Open(string operation)
        {
            var ready = new TaskCompletionSource<App03DevHost>(TaskCreationOptions.RunContinuationsAsynchronously);
            session = App03DevSession.Run(operation, root, source, run.Id, host =>
            {
                ready.SetResult(host);
                return Task.CompletedTask;
            }, settlement: true, finance: true, closeReadiness: true);
            if (await Task.WhenAny(session, ready.Task).WaitAsync(ct) == session) await session;
            return await ready.Task.WaitAsync(ct);
        }

        async Task Stop()
        {
            if (session is not { } ending) return;
            session = null;
            if (!ending.IsCompleted && EventWaitHandle.TryOpenExisting($@"Local\FmcgDms.APP03.{run.Id}", out var signal))
                using (signal) signal.Set();
            await ending;
        }

        async Task VerifyVehicleAndCutoff()
        {
            var fixture = await Read<Fixture>("before-assignment");
            await App03DevSession.Run("NextDay", root, source, run.Id, businessDate: App03DayBootstrap.FirstDay.AddDays(1),
                settlement: true, finance: true, closeReadiness: true);
            var next = await Open("Resume");
            Guid nextTrip = await App03SettlementEvidence.PrepareB(next, "DEV-20261124-A", load: true, ct);
            const string readiness = "/api/synthetic/app05/trip-close/trips/";
            var old = await Request(next, readiness + fixture.Trip, next.CloseCredentials!["ASSIGNEE"], ct);
            var current = await Request(next, readiness + nextTrip, next.CloseCredentials!["OPS"], ct);
            Compare("prior trip excludes vehicle stock from the new trip", "tenant-a", false, HasVehicleBlocker(old));
            Compare("new trip includes its own vehicle stock", "tenant-a", true, HasVehicleBlocker(current));

            Guid requestId = Guid.NewGuid();
            await Request(next, "/api/synthetic/app02d4/accounting/reconcile", next.FinanceCredentials!["RECONCILER"], ct, new
            {
                requestId, from = "2026-11-23T00:00:00+07:00", cutoff = "2026-11-24T00:00:00+07:00",
                asOf = "2026-11-24T09:00:00+07:00", unknownScope = false
            });
            var after = await Request(next, readiness + fixture.Trip, next.CloseCredentials!["ASSIGNEE"], ct);
            var work = after.GetProperty("work").EnumerateArray().ToArray();
            Compare("covering report removes only the original posted D.4 work", "tenant-a", 1, work.Length);
            Compare("covering report leaves the A obligation", "tenant-a", fixture.Line.OrderLineId,
                work[0].GetProperty("sourceId").GetGuid());
            await using var db = await Own("tenant-a");
            var actual = await db.QuerySingleAsync<DayTwo>(new CommandDefinition("""
                SELECT CONVERT(bit,CASE WHEN older.VehicleLocationId=newer.VehicleLocationId THEN 1 ELSE 0 END) AS SameVehicle,
                    p.OnHandBaseQuantity AS VehicleQuantity,l.Status AS ReportStatus
                FROM dbo.Trip older JOIN dbo.Trip newer ON newer.TenantId=older.TenantId AND newer.Id=@nextTrip
                JOIN dbo.PhysicalStockBalance p ON p.TenantId=newer.TenantId AND p.StockLocationId=newer.VehicleLocationId
                JOIN dbo.Sku k ON k.TenantId=p.TenantId AND k.Id=p.SkuId AND k.Code='Z-FIELD-EA'
                JOIN d4app.AccountingReportLine l ON l.ReportId=@requestId AND l.SourceLineId=@SourceId AND l.DeliveryId=@RelatedId
                JOIN d4app.AccountingReport h ON h.Id=l.ReportId
                WHERE older.TenantId=N'tenant-a' AND older.Id=@Trip AND l.OccurredAt<h.Cutoff
                    AND h.Cutoff<=h.AsOf AND l.ActualGross=l.ExpectedGross AND l.ActualCost=l.ExpectedCost;
                """, new { nextTrip, requestId, fixture.Trip, fixture.D4.SourceId, fixture.D4.RelatedId },
                cancellationToken: ct));
            Compare("same vehicle carries only the next trip and report covers original delivery", "tenant-a",
                new DayTwo(true, 1m, "RECONCILED_OK"), actual);
            await File.WriteAllTextAsync(Path.Combine(folder, "next-day-sources.json"),
                JsonSerializer.Serialize(new { fixture.Trip, nextTrip, requestId, fixture.D4.SourceId, actual }, Json), ct);


            const string b = "/api/synthetic/app02d2";
            var catalog = await Request(next, b + "/trips/" + nextTrip, next.SourceToken, ct);
            var line = catalog.GetProperty("lines").EnumerateArray().Single();
            var customer = catalog.GetProperty("customers").EnumerateArray()
                .Single(row => row.GetProperty("label").GetString()!.StartsWith("C2-FIXTURE", StringComparison.Ordinal));
            var selection = customer.GetProperty("selections").EnumerateArray().Single();
            var cash = catalog.GetProperty("verifiedCash").EnumerateArray()
                .Single(row => row.GetProperty("evidenceRef").GetString() == "APP03-CASH-DEV-20261124-A-1");
            string commandId = Guid.NewGuid().ToString("N");
            var sale = await Request(next, b + "/sales", next.SourceToken, ct, new
            {
                producerId = App03DayBootstrap.Actor, commandId, sourceNumber = "APP05-" + commandId,
                tripId = nextTrip, customerId = customer.GetProperty("customerId"), baseQuantity = 1,
                selectionId = selection.GetProperty("selectionId"), shipToSnapshotId = selection.GetProperty("shipToSnapshotId"),
                tripManifestLineId = line.GetProperty("tripManifestLineId"), skuId = line.GetProperty("skuId"),
                receivedCashAmount = cash.GetProperty("amount"), verificationEvidenceRef = cash.GetProperty("evidenceRef")
            });
            Compare("new day sale", "tenant-a", "APPLIED", sale.GetProperty("status").GetString());
            string offerCommand = Guid.NewGuid().ToString("N");
            var offer = await Request(next, "/api/synthetic/app02d3/offer", next.SourceToken, ct, new
            {
                producerId = App03DayBootstrap.Actor, commandId = offerCommand, sourceNumber = "APP05-" + offerCommand,
                tripId = nextTrip, offeredAmount = cash.GetProperty("amount")
            });
            Compare("new day cash handoff offer", "tenant-a", "APPLIED", offer.GetProperty("status").GetString());
            var pending = await Request(next, readiness + nextTrip, next.CloseCredentials!["OPS"], ct);
            Compare("custody keeps the new trip blocked", "tenant-a", true, HasBlocker(pending, "CUSTODY_OUTSTANDING"));
            Compare("pending offer independently blocks the new trip", "tenant-a", true, HasBlocker(pending, "CASH_OFFER_PENDING"));
            var oldAfterSale = await Request(next, readiness + fixture.Trip, next.CloseCredentials!["ASSIGNEE"], ct);
            Compare("new trip sale and offer do not affect prior trip readiness", "tenant-a", after.GetProperty("revision").GetString(),
                oldAfterSale.GetProperty("revision").GetString());
            await File.WriteAllTextAsync(Path.Combine(folder, "next-day-custody-sources.json"),
                JsonSerializer.Serialize(new { nextTrip, offerCommand, saleSource = sale.GetProperty("sourceId"),
                    offerSource = offer.GetProperty("sourceId"), pending }, Json), ct);

            static bool HasBlocker(JsonElement preview, string code) =>
                preview.GetProperty("blockers").EnumerateArray().Any(item => item.GetProperty("code").GetString() == code);
            static bool HasVehicleBlocker(JsonElement preview) =>
                preview.GetProperty("blockers").EnumerateArray().Any(item =>
                    item.GetProperty("code").GetString() is "VEHICLE_STOCK_OUTSTANDING" or "VEHICLE_SOURCE_DRIFT");
        }

        async Task<bool> Coordinate(Task exited, Snapshot peer)
        {
            if (!await Wait("main-unload", exited)) return false;
            var main = await Image(run, "tenant-a", ct);
            Compare("after main unload stock", "tenant-a", 35m, main.Main);
            Compare("after main unload value", "tenant-a", 105000m, main.MainValue);
            Compare("after main unload vehicle", "tenant-a", 0m, main.Vehicle);
            Compare("after main unload allocated", "tenant-a", 15m, main.Allocated);
            Compare("after main unload ordinary AR", "tenant-a", 250000m, main.Ar);
            Compare("after main unload COGS", "tenant-a", 75000m, main.Cogs);
            await Acknowledge("main-unload");

            if (!await Wait("before-assignment", exited)) return false;
            var fixture = await Read<Fixture>("before-assignment");
            var before = await Image(run, "tenant-a", ct);
            Compare("after both unloads stock", "tenant-a", 34m, before.Main);
            Compare("after both unloads stock value", "tenant-a", 102000m, before.MainValue);
            Compare("after both unloads allocated", "tenant-a", 18m, before.Allocated);
            Compare("before assignment", "tenant-a", 0, before.Assignments);
            Compare("business commands and ledger at contrast tenant", "tenant-b", peer, await Image(run, "tenant-b", ct));
            await using (var db = await Own("tenant-a"))
            {
                await VerifyLine(db, fixture.Trip, fixture.Line, fixture.MainSources, fixture.D4, 40, 40, 40, 25, 15);
                await VerifyLine(db, fixture.Alternate, fixture.OtherLine, fixture.OtherSources, null, 5, 4, 3, 1, 2);

                int accountingMatches = await db.ExecuteScalarAsync<int>(new CommandDefinition("""
                    SELECT COUNT(*) FROM d4accounting.Receipt r
                    JOIN d4accounting.Posting p ON p.ReceiptId=r.Id AND p.SourceLineId=r.SourceLineId AND p.Kind='BASE'
                    WHERE r.Id=@AccountingReceipt AND p.Id=@AccountingPosting AND r.DeliveryId=@RelatedId
                        AND r.SourceLineId=@SourceId AND r.Quantity=25 AND p.Gross=250000 AND p.Cost=75000
                        AND NOT EXISTS(SELECT 1 FROM d4app.AccountingReportLine l WHERE l.SourceLineId=r.SourceLineId);
                    """, new { fixture.AccountingReceipt, fixture.AccountingPosting, fixture.D4.SourceId, fixture.D4.RelatedId },
                    cancellationToken: ct));
                Compare("D.4 posted source at day-one cutoff without a covering report", "tenant-a", 1, accountingMatches);

                int returnMatches = await db.ExecuteScalarAsync<int>(new CommandDefinition("""
                    SELECT COUNT(*) FROM dbo.F05ReturnCase r
                    JOIN dbo.SaleDelivery d ON d.TenantId=r.TenantId AND d.Id=r.DeliveryId
                    WHERE r.TenantId=N'tenant-a' AND r.Id=@SourceId AND d.Id=@RelatedId AND d.TripId=@Alternate
                        AND r.Quantity=1 AND NOT EXISTS(SELECT 1 FROM dbo.F05ReturnStage s
                            WHERE s.TenantId=r.TenantId AND s.CaseId=r.Id);
                    """, new { fixture.ReturnWork.SourceId, fixture.ReturnWork.RelatedId, fixture.Alternate }, cancellationToken: ct));
                Compare("F05 request tied to the other trip's exact delivery", "tenant-a", 1, returnMatches);
            }
            await Acknowledge("before-assignment");

            if (!await Wait("after-assignment", exited)) return false;
            var assignment = await Read<AssignmentCheckpoint>("after-assignment");
            var after = await Image(run, "tenant-a", ct);
            Compare("assignment only adds three typed audit events", "tenant-a", before with { Assignments = 3 }, after);
            Compare("no writes in contrast tenant after foreign write/replay", "tenant-b", peer, await Image(run, "tenant-b", ct));

            await using (var db = await Own("tenant-a"))
            {
                int matches = await db.ExecuteScalarAsync<int>(new CommandDefinition("""
                    SELECT COUNT(*) FROM dbo.App05FollowUpAssignment
                    WHERE TenantId=N'tenant-a' AND TripId=@Trip AND FollowUpId=@FollowUpId
                      AND CommandId=@CommandId AND ActorId=@Ops AND SourceKind=N'A_REMAINDER'
                      AND SourceId=@SourceId AND Assignee=@Assignee AND DueAt=@DueAt
                      AND NextAction=@NextAction AND Reason=@Reason AND ExpectedRevision=@ExpectedRevision;
                    """, new { fixture.Trip, App05CloseBootstrap.Ops, assignment.Assigned.FollowUpId, assignment.Command.CommandId,
                        assignment.Command.SourceId, assignment.Command.Assignee, assignment.Command.DueAt,
                        assignment.Command.NextAction, assignment.Command.Reason, assignment.Command.ExpectedRevision }, cancellationToken: ct));
                Compare("source / idempotency ID / assignee / UTC deadline / revision audit", "tenant-a", 1, matches);


                foreach (var source in new[] { assignment.AccountingAssignment, assignment.ReturnAssignment })
                {
                    int matchesSource = await db.ExecuteScalarAsync<int>(new CommandDefinition("""
                        SELECT COUNT(*) FROM dbo.App05FollowUpAssignment
                        WHERE TenantId=N'tenant-a' AND TripId=@TripId AND FollowUpId=@FollowUpId
                          AND SourceKind=@SourceKind AND SourceId=@SourceId AND ActorId=@Ops AND Assignee=@Assignee;
                        """, new { source.TripId, source.FollowUpId, source.SourceKind, source.SourceId,
                            App05CloseBootstrap.Ops, App05CloseBootstrap.Assignee }, cancellationToken: ct));
                    Compare("exact follow-up source " + source.SourceKind, "tenant-a", 1, matchesSource);
                }
                // Revoke only the exact assignment in this freshly created, verified DEV database.
                int revoked = await db.ExecuteAsync(new CommandDefinition("""
                    UPDATE o SET IsActive=0 FROM dbo.App05TripOperator o
                    JOIN dbo.LocalApp03Session s ON s.TenantId=o.TenantId AND s.RunId=@Nonce
                    WHERE o.TenantId=N'tenant-a' AND o.TripId=@Trip AND o.ActorId=@Ops
                      AND DB_NAME()=@Name AND o.IsActive=1;
                    """, new { fixture.Trip, App05CloseBootstrap.Ops, Nonce = run.Id, Name = run.Name("tenant-a") }, cancellationToken: ct));
                Compare("revoked exactly one operator scope", "tenant-a", 1, revoked);
            }
            await Acknowledge("after-assignment");

            if (!await Wait("after-revocation", exited)) return false;
            Compare("revoked read/replay have no effect", "tenant-a", after, await Image(run, "tenant-a", ct));
            Compare("contrast tenant unchanged after revoked replay", "tenant-b", peer, await Image(run, "tenant-b", ct));
            await Acknowledge("after-revocation");
            return true;
        }

        async Task VerifyLine(SqlConnection db, Guid trip, Line line, Sources sources, AccountingWork? accounting,
            decimal confirmed, decimal granted, decimal loaded, decimal delivered, decimal returned)
        {
            var actual = await db.QuerySingleAsync<LineAmounts>(new CommandDefinition("""
                SELECT o.Status, c.ConfirmedBaseQuantity AS Confirmed,
                    (SELECT SUM(r.GrantedBaseQuantity) FROM dbo.StockRight r
                        JOIN dbo.AReservation a ON a.TenantId=r.TenantId AND a.StockRightId=r.Id
                        WHERE a.TenantId=l.TenantId AND a.AOrderLineId=l.Id) AS Granted,
                    m.LoadedBaseQuantity AS Loaded,d.BaseQuantity AS Delivered,u.BaseQuantity AS Returned,
                    ar.OriginalAmount AS Ar,d.CostAmount AS Cogs,ar.DueBusinessDate,
                    d.InvoiceEligible, d.BusinessSourceLineId, d.Id AS DeliveryId
                FROM dbo.AOrderLine l
                JOIN dbo.AOrder a ON a.TenantId=l.TenantId AND a.Id=l.AOrderId
                JOIN dbo.Trip o ON o.TenantId=a.TenantId AND o.Id=a.TripId
                JOIN dbo.AConfirmedLineSnapshot c ON c.TenantId=l.TenantId AND c.AOrderLineId=l.Id
                JOIN dbo.SaleDelivery d ON d.TenantId=l.TenantId AND d.AOrderLineId=l.Id AND d.TripId=o.Id
                JOIN dbo.ReceivableOpenItem ar ON ar.TenantId=d.TenantId AND ar.SaleDeliveryId=d.Id AND ar.AppliedAmount=0
                JOIN dbo.TripManifestLine m ON m.TenantId=d.TenantId AND m.Id=d.TripManifestLineId
                JOIN dbo.D3TripUnloadLine u ON u.TenantId=m.TenantId AND u.TripManifestLineId=m.Id AND u.TripId=o.Id
                WHERE l.TenantId=N'tenant-a' AND l.Id=@OrderLineId AND o.Id=@trip AND a.Id=@OrderId
                    AND c.ConfirmBusinessSourceId=@ConfirmSourceId AND d.BusinessSourceId=@DeliverySource
                    AND u.TripUnloadId=@UnloadSource;
                """, new { trip, line.OrderId, line.OrderLineId, line.ConfirmSourceId, sources.DeliverySource, sources.UnloadSource },
                cancellationToken: ct));
            Compare($"A source chain {trip}", "tenant-a",
                new LineAmounts("RECONCILING", confirmed, granted, loaded, delivered, returned, delivered * 10000, delivered * 3000,
                    new DateTime(2026, 11, 30), false, accounting?.SourceId ?? actual.BusinessSourceLineId,
                    accounting?.RelatedId ?? actual.DeliveryId), actual);
        }

        async Task<SqlConnection> Own(string tenant)
        {
            await run.Verify(tenant, app03Session: true);
            return new SqlConnection(run.Connection(tenant));
        }

        async Task<bool> Wait(string name, Task exited)
        {
            while (!File.Exists(Path.Combine(folder, name + ".ready")))
            {
                if (exited.IsCompleted) return false;
                await Task.Delay(100, ct);
            }
            return true;
        }

        Task Acknowledge(string name) => File.WriteAllTextAsync(Path.Combine(folder, name + ".checked"), "", ct);

        async Task<T> Read<T>(string name)
        {
            await using var file = File.OpenRead(Path.Combine(folder, name + ".json"));
            return await JsonSerializer.DeserializeAsync<T>(file, Json, ct) ?? throw new InvalidOperationException($"Missing {name} payload.");
        }

        void Compare<T>(string metric, string tenant, T expected, T actual)
        {
            bool equal = EqualityComparer<T>.Default.Equals(expected, actual);
            checks.Add(new(metric, tenant, expected!, actual!, equal ? null : "Values differ"));
            if (!equal) throw new InvalidOperationException($"{metric} for {tenant}: expected {expected}, actual {actual}.");
        }
    }

    private static async Task<JsonElement> Request(App03DevHost host, string path, string token, CancellationToken ct,
        object? payload = null)
    {
        using var request = new HttpRequestMessage(payload is null ? HttpMethod.Get : HttpMethod.Post, host.WebUrl + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (payload is not null) request.Content = JsonContent.Create(payload);
        using var response = await host.Client.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
    }

    private static async Task<Snapshot> Image(DemoRun run, string tenant, CancellationToken ct)
    {
        await run.Verify(tenant, app03Session: true);
        await using var db = new SqlConnection(run.Connection(tenant));
        return await db.QuerySingleAsync<Snapshot>(new CommandDefinition("""
            DECLARE @sku uniqueidentifier=(SELECT Id FROM dbo.Sku WHERE TenantId=@tenant AND Code=N'DAY-A-EA');
            SELECT
                (SELECT b.OnHandBaseQuantity FROM dbo.PhysicalStockBalance b JOIN dbo.StockLocation l ON l.TenantId=b.TenantId AND l.Id=b.StockLocationId
                    WHERE b.TenantId=@tenant AND b.SkuId=@sku AND l.Code=N'C2-MAIN') AS Main,
                (SELECT b.OnHandCostAmount FROM dbo.PhysicalStockBalance b JOIN dbo.StockLocation l ON l.TenantId=b.TenantId AND l.Id=b.StockLocationId
                    WHERE b.TenantId=@tenant AND b.SkuId=@sku AND l.Code=N'C2-MAIN') AS MainValue,
                COALESCE((SELECT SUM(b.OnHandBaseQuantity) FROM dbo.PhysicalStockBalance b
                    JOIN dbo.StockLocation l ON l.TenantId=b.TenantId AND l.Id=b.StockLocationId
                    WHERE b.TenantId=@tenant AND b.SkuId=@sku AND l.Kind=N'VEHICLE'),0) AS Vehicle,
                COALESCE((SELECT SUM(p.AllocatedBaseQuantity) FROM dbo.StockPool p WHERE p.TenantId=@tenant AND p.SkuId=@sku),0) AS Allocated,
                COALESCE((SELECT SUM(a.OriginalAmount) FROM dbo.ReceivableOpenItem a
                    JOIN dbo.SaleDelivery d ON d.TenantId=a.TenantId AND d.Id=a.SaleDeliveryId
                    WHERE a.TenantId=@tenant AND d.SkuId=@sku),0) AS Ar,
                COALESCE((SELECT SUM(i.CostAmount) FROM dbo.DeliveryStockIssue i WHERE i.TenantId=@tenant AND i.SkuId=@sku),0) AS Cogs,
                (SELECT COUNT(*) FROM dbo.App05FollowUpAssignment WHERE TenantId=@tenant) AS Assignments,
                CONVERT(varchar(64),HASHBYTES('SHA2_256',(
                    SELECT
                        JSON_QUERY((SELECT c.* FROM dbo.BusinessCommand c WHERE c.TenantId=@tenant ORDER BY c.Id FOR JSON PATH)) AS Commands,
                        JSON_QUERY((SELECT s.* FROM dbo.BusinessSource s WHERE s.TenantId=@tenant ORDER BY s.Id FOR JSON PATH)) AS Sources,
                        JSON_QUERY((SELECT e.* FROM dbo.LedgerEffect e WHERE e.TenantId=@tenant ORDER BY e.Id FOR JSON PATH)) AS Effects,
                        JSON_QUERY((SELECT t.* FROM dbo.Trip t WHERE t.TenantId=@tenant ORDER BY t.Id FOR JSON PATH)) AS Trips
                    FOR JSON PATH, WITHOUT_ARRAY_WRAPPER)),2) AS HistoryDigest;
            """, new { tenant }, cancellationToken: ct));
    }

    private sealed record Snapshot(decimal Main, decimal MainValue, decimal Vehicle, decimal Allocated, decimal Ar, decimal Cogs,
        int Assignments, string HistoryDigest);
    private sealed record Line(Guid OrderId, Guid OrderLineId, Guid ConfirmSourceId);
    private sealed record Sources(Guid DeliverySource, Guid UnloadSource);
    private sealed record AccountingWork(Guid SourceId, Guid RelatedId);
    private sealed record Fixture(Guid Trip, Guid Alternate, Line Line, Line OtherLine, AccountingWork D4,
        Sources MainSources, Sources OtherSources, Guid AccountingReceipt, Guid AccountingPosting, AccountingWork ReturnWork);
    private sealed record LineAmounts(string Status, decimal Confirmed, decimal Granted, decimal Loaded, decimal Delivered,
        decimal Returned, decimal Ar, decimal Cogs, DateTime DueBusinessDate, bool InvoiceEligible, Guid BusinessSourceLineId, Guid DeliveryId);
    private sealed record FollowUpCommand(Guid CommandId, Guid SourceId, string Assignee, DateTimeOffset DueAt, string NextAction,
        string Reason, string ExpectedRevision);
    private sealed record FollowUpResult(Guid FollowUpId);
    private sealed record DayTwo(bool SameVehicle, decimal VehicleQuantity, string ReportStatus);
    private sealed record TypedAssignment(Guid TripId, Guid FollowUpId, string SourceKind, Guid SourceId);
    private sealed record AssignmentCheckpoint(FollowUpCommand Command, FollowUpResult Assigned,
        TypedAssignment AccountingAssignment, TypedAssignment ReturnAssignment);
    private sealed record Comparison(string Metric, string Tenant, object Expected, object Actual, string? Diff);
    private sealed record Artifact(string File, string Sha256);
}
