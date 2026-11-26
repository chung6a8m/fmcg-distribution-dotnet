using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;

namespace FmcgDms.Demo;

[SupportedOSPlatform("windows")]
internal static class App05TripClosureEvidence
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private const string Primary = "tenant-a";
    private const string Peer = "tenant-b";

    internal static async Task Run(string root, string source, bool successor = false)
    {
        var run = new DemoRun(Guid.NewGuid().ToString("N"));
        string folder = Directory.CreateDirectory(Path.Combine(root, "TestResults", successor ? "app05-3" : "app05-2", run.Id)).FullName;
        Console.WriteLine($"APP-05.{(successor ? 3 : 2)} evidence: {folder}");
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(successor ? 10 : 6));
        var ct = deadline.Token;
        var checks = new List<Comparison>();
        Task? session = null;
        ArmFailure? fixture = null;
        var sources = new App05ClosureSourceEvidence(run, folder, ct, Wait, Acknowledge,
            includeIntakeEvidence => EffectDigest(Primary, includeIntakeEvidence: includeIntakeEvidence));
        try
        {
            var first = await Open("Start");
            string originalPeer = await EffectDigest(Peer);
            await Browser(first, "day-one", DayOne);
            await Stop();

            await App03DevSession.Run("NextDay", root, source, run.Id, businessDate: App03DayBootstrap.FirstDay.AddDays(1),
                tripClosure: true);
            var next = await Open("Resume");
            await Browser(next, "day-two", DayTwo);

            await Stop();
            await App03DevSession.Run("NextDay", root, source, run.Id, businessDate: App03DayBootstrap.FirstDay.AddDays(2),
                tripClosure: true);
            var third = await Open("Resume");
            Guid thirdTrip = await App03SettlementEvidence.PrepareB(third, "DEV-20261125-A", load: true, ct);
            await Browser(third, "day-three", exited => sources.VerifySourceRaceAndCash(exited, thirdTrip));
            if (successor)
            {
                await Stop();
                await App03DevSession.Run("NextDay", root, source, run.Id, businessDate: App03DayBootstrap.FirstDay.AddDays(3),
                    tripClosure: true);
                var upgraded = await Open("Resume", upgrade: true);
                await Browser(upgraded, "successor", VerifySuccessor);
                await Stop();
                await App03DevSession.Run("NextDay", root, source, run.Id, businessDate: App03DayBootstrap.FirstDay.AddDays(4));
                var approval = await Open("Resume", upgrade: true);
                await Browser(approval, "approval", VerifyApproval);
                await Stop();
                await App03DevSession.Run("NextDay", root, source, run.Id, businessDate: App03DayBootstrap.FirstDay.AddDays(5));
                var competing = await Open("Resume", upgrade: true);
                await Browser(competing, "approval-race", VerifyApprovalRace);
            }
            Check("contrast tenant business rows preserved across the synthetic journey", Peer, originalPeer, await EffectDigest(Peer));
        }
        finally
        {
            try { await Stop(); }
            finally
            {
                if (App03DevSession.HasManifest(run.Id))
                    await App03DevSession.Run("Remove", root, source, run.Id);
                await File.WriteAllTextAsync(Path.Combine(folder, "expected-actual-diff.json"), JsonSerializer.Serialize(checks, Json));
                await sources.WriteResults();
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
            command = "pwsh -NoProfile -File ./scripts/Run-App03Dev.ps1 -Action " + (successor ? "VerifyASuccessor" : "VerifyTripClosure"),
            os = Environment.OSVersion.VersionString, runtime = Environment.Version.ToString(),
            instance = DemoRun.Instance, scope = "synthetic DEV", result = "PASS", artifacts
        }, Json), ct);
        Console.WriteLine($"APP-05.{(successor ? 3 : 2)} evidence verified: {folder}");

        async Task<App03DevHost> Open(string operation, bool upgrade = false)
        {
            var ready = new TaskCompletionSource<App03DevHost>(TaskCreationOptions.RunContinuationsAsynchronously);
            session = App03DevSession.Run(operation, root, source, run.Id, host =>
            {
                ready.SetResult(host);
                return Task.CompletedTask;
            }, tripClosure: true, successor: upgrade, splitSuccessorLots: successor && operation == "Start");
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

        async Task Browser(App03DevHost active, string stage, Func<Task, Task<bool>> coordinate)
        {
            var start = new ProcessStartInfo("node")
            {
                WorkingDirectory = Path.Combine(root, "web"), UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            foreach (var argument in new[] { "node_modules/@playwright/test/cli.js", "test", "--config", "playwright.app05-2.config.ts",
                "--grep", stage + " " })
                start.ArgumentList.Add(argument);
            start.Environment["APP02C1_WEB_URL"] = active.WebUrl;
            start.Environment["APP02C1_RUN_ID"] = run.Id;
            start.Environment["APP05_CLOSURE_STAGE"] = stage;
            if (successor) start.Environment["APP05_SUCCESSOR_EVIDENCE"] = "1";
            start.Environment["APP05_EVIDENCE_DIR"] = folder;
            foreach (var (role, token) in active.CloseCredentials!)
                start.Environment["APP03_" + role + "_TOKEN"] = token;
            start.Environment["APP03_SOURCE_TOKEN"] = active.SourceToken;
            start.Environment["APP03_RECEIVER_TOKEN"] = active.ReceiverToken;
            start.Environment["APP03_PEER_WAREHOUSE_TOKEN"] = active.PeerToken;
            start.Environment["APP03_CASHIER_TOKEN"] = active.CashierToken;
            start.Environment["APP03_ACCOUNTANT_TOKEN"] = active.FinanceCredentials!["ACCOUNTANT"];
            start.Environment["APP03_RECONCILER_TOKEN"] = active.FinanceCredentials["RECONCILER"];
            start.Environment["APP03_OWNER_TOKEN"] = active.FinanceCredentials["OWNER"];

            using var browser = Process.Start(start) ?? throw new InvalidOperationException("Cannot start APP-05.2 Chromium.");
            var stdout = browser.StandardOutput.ReadToEndAsync();
            var stderr = browser.StandardError.ReadToEndAsync();
            var exited = browser.WaitForExitAsync();
            try
            {
                bool allCheckpoints = await coordinate(exited);
                await exited.WaitAsync(ct);
                if (browser.ExitCode != 0 || !allCheckpoints)
                    throw new InvalidOperationException($"APP-05.2 Chromium {stage} exited {browser.ExitCode}, SQL checkpoints complete: {allCheckpoints}. See {folder}.");
            }
            finally
            {
                if (!browser.HasExited)
                {
                    browser.Kill(entireProcessTree: true);
                    await exited;
                }
                await File.WriteAllTextAsync(Path.Combine(folder, stage + ".log"), await stdout + Environment.NewLine + await stderr);
            }
        }

        async Task<bool> DayOne(Task exited)
        {
            if (!await sources.VerifyPendingDelivery(exited)) return false;
            if (!await VerifyPoolFault("main-pool", "RECONCILING", exited)) return false;
            if (!await Wait("arm-failure", exited)) return false;
            fixture = await Read<ArmFailure>("arm-failure");
            string before = await EffectDigest(Primary);
            var original = await TripImage(fixture.Main);
            Check("no decision and no premature state change", Primary, new TripImageRow("RECONCILING", 0), original);
            await Fault(fixture.Main, fixture.Failure.CommandId, true);
            await Acknowledge("arm-failure");

            if (!await Wait("rolled-back", exited)) return false;
            Check("precommit failure rolls back closure and status", Primary, original, await TripImage(fixture.Main));
            Check("precommit failure has no ledger or source writes", Primary, before, await EffectDigest(Primary));
            await Fault(fixture.Main, fixture.Failure.CommandId, false);
            await Acknowledge("rolled-back");

            if (!await Wait("after-open-close", exited)) return false;
            var open = await Read<OpenReceipt>("after-open-close");
            Check("one atomic decision with open cases", Primary, new TripImageRow("CLOSED_WITH_OPEN_CASES", 1), await TripImage(fixture.Main));
            Check("closure, replay and rejected legacy writes preserve source, quantity and money", Primary, before, await EffectDigest(Primary));
            await VerifyReceipt(open.Receipt, "CLOSED_WITH_OPEN_CASES", 2, fixture.Unload.SourceId);
            await using var db = await OpenDatabase(Primary);
            var inventory = await db.QuerySingleAsync<OpenInventory>(new CommandDefinition("""
                SELECT t.Status, b.OnHandBaseQuantity AS MainStock, b.OnHandCostAmount AS MainCost,
                    d.BaseQuantity AS Delivered,d.CostAmount AS Cogs,ar.OriginalAmount AS Ar,
                    u.BaseQuantity AS Returned, r.GrantedBaseQuantity-r.ConsumedBaseQuantity-r.ReleasedBaseQuantity AS Held
                FROM dbo.Trip t
                JOIN dbo.SaleDelivery d ON d.TenantId=t.TenantId AND d.TripId=t.Id AND d.BusinessSourceId=@Delivery
                JOIN dbo.ReceivableOpenItem ar ON ar.TenantId=d.TenantId AND ar.SaleDeliveryId=d.Id
                JOIN dbo.StockRight r ON r.TenantId=d.TenantId AND r.Id=d.StockRightId
                JOIN dbo.D3TripUnloadLine u ON u.TenantId=d.TenantId AND u.TripManifestLineId=d.TripManifestLineId
                JOIN dbo.PhysicalStockBalance b ON b.TenantId=u.TenantId AND b.StockLocationId=u.WarehouseLocationId AND b.SkuId=d.SkuId
                WHERE t.TenantId=@Tenant AND t.Id=@Trip AND u.TripUnloadId=@Handoff
                  AND d.AOrderId=@OrderId AND d.AOrderLineId=@OrderLineId;
                """, new { Tenant = Primary, Trip = fixture.Main, Delivery = open.Delivery.SourceId, Handoff = fixture.Unload.SourceId,
                    fixture.Order.OrderId, fixture.Order.OrderLineId }, cancellationToken: ct));
            Check("A40/25/15 source and MAIN stock after both day-one trips", Primary,
                new OpenInventory("CLOSED_WITH_OPEN_CASES", 34, 102000, 25, 75000, 250000, 15, 15), inventory);
            await Acknowledge("after-open-close");
            return true;
        }

        async Task<bool> DayTwo(Task exited)
        {
            if (fixture is null) throw new InvalidOperationException("Missing day-one fixture.");
            if (!await Wait("before-clean-close", exited)) return false;
            var report = await Read<ReportCheckpoint>("before-clean-close");
            var dayOne = await Read<OpenReceipt>("after-open-close");
            await VerifyReceipt(dayOne.Receipt, "CLOSED_WITH_OPEN_CASES", 2, fixture.Unload.SourceId);
            await using var db = await OpenDatabase(Primary);
            int covered = await db.ExecuteScalarAsync<int>(new CommandDefinition("""
                SELECT COUNT(*) FROM d4app.AccountingReportLine l
                JOIN d4app.AccountingReport h ON h.Id=l.ReportId
                WHERE l.ReportId=@ReportId AND l.DeliveryId=@RelatedId AND l.SourceLineId=@SourceId
                    AND l.Status='RECONCILED_OK' AND l.OccurredAt<h.Cutoff AND h.Cutoff<=h.AsOf;
                """, new { report.ReportId, fixture.AccountingWork.RelatedId, fixture.AccountingWork.SourceId }, cancellationToken: ct));
            Check("covering D.4 report links the original first-day source", Primary, 1, covered);
            var cleanOriginal = await TripImage(fixture.Clean);
            Check("clean trip is open before contenders", Primary, new TripImageRow("RECONCILING", 0), cleanOriginal);
            string beforeClean = await EffectDigest(Primary);
            await Acknowledge("before-clean-close");

            if (!await Wait("after-clean-close", exited)) return false;
            var clean = await Read<CleanCheckpoint>("after-clean-close");
            await VerifyReceipt(clean.CleanReceipt, "CLOSED", 0, fixture.CleanUnload.SourceId);
            Check("two close IDs leave just one decision", Primary, new TripImageRow("CLOSED", 1), await TripImage(fixture.Clean));
            Check("clean close adds no source stock or financial effects", Primary, beforeClean, await EffectDigest(Primary));
            await Acknowledge("after-clean-close");

            if (!await VerifyPoolFault("shared-pool", "PREPARED", exited)) return false;
            if (!await Wait("before-zero-handoff", exited)) return false;
            var zero = await Read<ZeroFixture>("before-zero-handoff");
            string beforeZero = await EffectDigest(Primary, includeHandoffSources: false);
            Check("no manifest, no handoff, no closure for new trip", Primary, new ZeroState(0, 0, 0, zero.HeldQuantity, 0), await ZeroImage(zero));
            await Acknowledge("before-zero-handoff");

            if (!await Wait("after-zero-close", exited)) return false;
            var zeroReceipt = await Read<ZeroCheckpoint>("after-zero-close");
            await VerifyReceipt(zeroReceipt.ZeroReceipt, "CLOSED_WITH_OPEN_CASES", zero.Shared is null ? 2 : 3, zeroReceipt.ZeroReceipt.HandoffId);
            Check("one source-bound handoff and closure, with no manifest or stock/AR effect", Primary,
                new TripImageRow("CLOSED_WITH_OPEN_CASES", 1), await TripImage(zero.Zero));
            Check("zero handoff leaves actual HELD and no right for the unreserved Confirm", Primary,
                new ZeroState(0, 1, 0, zero.HeldQuantity, 0), await ZeroImage(zero));
            Check("zero handoff and close preserve all physical and financial rows", Primary, beforeZero,
                await EffectDigest(Primary, includeHandoffSources: false));

            var proofs = (await db.QueryAsync<ZeroProof>(new CommandDefinition("""
                SELECT OrderLineId,ConfirmSourceId,HeldBaseQuantity
                FROM dbo.App05ZeroStockHandoffLine WHERE TenantId=@Tenant AND TripId=@Zero AND HandoffId=@HandoffId;
                """, new { Tenant = Primary, zero.Zero, zeroReceipt.ZeroReceipt.HandoffId }, cancellationToken: ct))).ToDictionary(row => row.OrderLineId);
            Check("zero handoff references the exact source Confirm lines", Primary, zero.Shared is null ? 2 : 3, proofs.Count);
            Check("zero handoff proves MAIN HELD for the first original Confirm", Primary,
                new ZeroProof(zero.Held.OrderLineId, zero.Held.ConfirmSourceId, zero.HeldQuantity), proofs.GetValueOrDefault(zero.Held.OrderLineId));
            Check("zero handoff proves no Reserve for the second original Confirm", Primary,
                new ZeroProof(zero.Unreserved.OrderLineId, zero.Unreserved.ConfirmSourceId, 0), proofs.GetValueOrDefault(zero.Unreserved.OrderLineId));
            await Acknowledge("after-zero-close");

            if (!await Wait("revoke-close-scope", exited)) return false;
            int revoked = await db.ExecuteAsync(new CommandDefinition("""
                UPDATE o SET IsActive=0 FROM dbo.App05TripOperator o
                JOIN dbo.LocalApp03Session s ON s.TenantId=o.TenantId AND s.RunId=@Nonce
                WHERE o.TenantId=@Tenant AND o.TripId=@Trip AND o.ActorId=@Ops AND DB_NAME()=@Name AND o.IsActive=1;
                """, new { Tenant = Primary, Trip = fixture.Main, App05CloseBootstrap.Ops, Nonce = run.Id, Name = run.Name(Primary) },
                cancellationToken: ct));
            Check("revoked exact close scope", Primary, 1, revoked);
            await Acknowledge("revoke-close-scope");

            if (!await Wait("after-revocation", exited)) return false;
            await VerifyReceipt(dayOne.Receipt, "CLOSED_WITH_OPEN_CASES", 2, fixture.Unload.SourceId);
            await using var peer = await OpenDatabase(Peer);
            Check("no closure in contrast tenant", Peer, 0,
                await peer.ExecuteScalarAsync<int>(new CommandDefinition("SELECT COUNT(*) FROM dbo.App05TripClosure;", cancellationToken: ct)));
            if (successor)
                Check("restore fixture scope after checking revocation", Primary, 1,
                    await db.ExecuteAsync(new CommandDefinition("""
                        UPDATE o SET IsActive=1 FROM dbo.App05TripOperator o
                        JOIN dbo.LocalApp03Session s ON s.TenantId=o.TenantId AND s.RunId=@Nonce
                        WHERE o.TenantId=@Tenant AND o.TripId=@Trip AND o.ActorId=@Ops AND DB_NAME()=@Name AND o.IsActive=0;
                        """, new { Tenant = Primary, Trip = fixture.Main, App05CloseBootstrap.Ops, Nonce = run.Id,
                            Name = run.Name(Primary) }, cancellationToken: ct)));
            await Acknowledge("after-revocation");
            return true;
        }

        async Task VerifyReceipt(Receipt receipt, string status, int count, Guid handoff)
        {
            await using var db = await OpenDatabase(Primary);
            var persisted = await db.QuerySingleOrDefaultAsync<Persisted>(new CommandDefinition("""
                SELECT ClosureId,ActorId,CommandId,Status,ClosedAt,OpenWorkCount,HandoffId,Snapshot
                FROM dbo.App05TripClosure WHERE TenantId=@Tenant AND TripId=@TripId;
                """, new { Tenant = Primary, receipt.TripId }, cancellationToken: ct))
                ?? throw new InvalidOperationException("Closure receipt not found in the owned database.");
            Check("durable receipt " + receipt.TripId, Primary, new Persisted(receipt.ClosureId, App05CloseBootstrap.Ops,
                receipt.CommandId, status, receipt.ClosedAt, count, handoff, ""), persisted with { Snapshot = "" });
            using var snapshot = JsonDocument.Parse(persisted.Snapshot);
            Check("immutable snapshot matches original response " + receipt.TripId, Primary, true,
                JsonElement.DeepEquals(receipt.Snapshot, snapshot.RootElement));
        }

        async Task<TripImageRow> TripImage(Guid trip)
        {
            await using var db = await OpenDatabase(Primary);
            return await db.QuerySingleAsync<TripImageRow>(new CommandDefinition("""
                SELECT t.Status,(SELECT COUNT(*) FROM dbo.App05TripClosure c WHERE c.TenantId=t.TenantId AND c.TripId=t.Id) AS Decisions
                FROM dbo.Trip t WHERE t.TenantId=@Tenant AND t.Id=@Trip;
                """, new { Tenant = Primary, Trip = trip }, cancellationToken: ct));
        }

        async Task<ZeroState> ZeroImage(ZeroFixture zero)
        {
            await using var db = await OpenDatabase(Primary);
            return await db.QuerySingleAsync<ZeroState>(new CommandDefinition("""
                SELECT
                  (SELECT COUNT(*) FROM dbo.TripManifest m WHERE m.TenantId=@Tenant AND m.TripId=@Zero) AS Manifests,
                  (SELECT COUNT(*) FROM dbo.D3TripUnload u JOIN dbo.BusinessSource s ON s.TenantId=u.TenantId AND s.Id=u.Id
                   WHERE u.TenantId=@Tenant AND u.TripId=@Zero AND s.SourceKind='TRIP_UNLOAD') AS Handoffs,
                  (SELECT COUNT(*) FROM dbo.D3TripUnloadLine u WHERE u.TenantId=@Tenant AND u.TripId=@Zero) AS ReturnLines,
                  COALESCE((SELECT SUM(r.GrantedBaseQuantity-r.ConsumedBaseQuantity-r.ReleasedBaseQuantity)
                   FROM dbo.StockRight r JOIN dbo.AReservation a ON a.TenantId=r.TenantId AND a.StockRightId=r.Id
                   WHERE r.TenantId=@Tenant AND r.TripId=@Zero AND a.AOrderLineId=@HeldLine),0) AS Held,
                  (SELECT COUNT(*) FROM dbo.AReservation a WHERE a.TenantId=@Tenant AND a.AOrderLineId=@UnreservedLine) AS UnreservedRights;
                """, new { Tenant = Primary, zero.Zero, HeldLine = zero.Held.OrderLineId, UnreservedLine = zero.Unreserved.OrderLineId },
                cancellationToken: ct));
        }

        async Task<bool> VerifyPoolFault(string phase, string status, Task exited)
        {
            if (!await Wait(phase + "-fault", exited)) return false;
            var fault = await Read<PoolFault>(phase + "-fault");
            await SetPool(fault, fault.Original, fault.Fault);
            try
            {
                string baseline = await EffectDigest(Primary);
                await Acknowledge(phase + "-fault");
                if (!await Wait(phase + "-rejected", exited)) return false;
                Check(phase + " rejection preserves status and no closure", Primary, new TripImageRow(status, 0), await TripImage(fault.Trip));
                Check(phase + " rejection creates no effects or new handoff", Primary, baseline, await EffectDigest(Primary));
            }
            finally
            {
                await SetPool(fault, fault.Fault, fault.Original);
            }
            await Acknowledge(phase + "-rejected");
            return true;
        }

        async Task<bool> VerifySuccessor(Task exited)
        {
            if (!await Wait("successor-before", exited)) return false;
            var fixture = await Read<SuccessorFixture>("successor-before");
            string before = await EffectDigest(Primary);
            await using var db = await OpenDatabase(Primary);
            var scope = new { Tenant = Primary, Name = run.Name(Primary), Nonce = run.Id,
                MainLine = fixture.Order.OrderLineId, HeldLine = fixture.Held.OrderLineId,
                UnreservedLine = fixture.Unreserved.OrderLineId };
            Check("forward migration preserves closed sources and starts with no bindings", Primary, 0,
                await db.ExecuteScalarAsync<int>(new CommandDefinition("SELECT COUNT(*) FROM dbo.App05ASuccessor;", scope, cancellationToken: ct)));
            await Acknowledge("successor-before");

            if (!await Wait("successor-arm", exited)) return false;
            var fault = await Read<SuccessorFault>("successor-arm");
            const string verify = """
                IF DB_NAME()<>@Name OR NOT EXISTS(SELECT 1 FROM dbo.LocalDemoOwner WHERE RunId=@Nonce)
                    OR NOT EXISTS(SELECT 1 FROM dbo.App00ScratchMarker WHERE Nonce=@Nonce)
                    OR NOT EXISTS(SELECT 1 FROM dbo.LocalApp03Session WHERE RunId=@Nonce AND TenantId=@Tenant)
                    THROW 51925,'APP-05.3 evidence scope mismatch.',1;
                """;
            string statement = $"""
                CREATE TRIGGER dbo.TR_App05EvidenceFailSuccessor ON dbo.App05ASuccessorCommand AFTER INSERT AS
                BEGIN
                    SET NOCOUNT ON;
                    IF EXISTS(SELECT 1 FROM inserted WHERE TenantId=N'tenant-a' AND CommandId='{fault.CommandId:D}')
                        THROW 51926,'APP-05.3 synthetic precommit fault.',1;
                END;
                """;
            await db.ExecuteAsync(new CommandDefinition(verify + " EXEC(@Statement);",
                new { scope.Tenant, scope.Name, scope.Nonce, Statement = statement }, cancellationToken: ct));
            await Acknowledge("successor-arm");

            if (!await Wait("successor-rollback", exited)) return false;
            Check("fault before commit rolls back successor right, lot and receipt", Primary, 0,
                await db.ExecuteScalarAsync<int>(new CommandDefinition("""
                    SELECT (SELECT COUNT(*) FROM dbo.App05AHeldTransfer)
                         + (SELECT COUNT(*) FROM dbo.App05AHeldTransferLot)
                         + (SELECT COUNT(*) FROM dbo.App05ASuccessorCommand WHERE CommandId=@CommandId);
                    """, fault, cancellationToken: ct)));
            await db.ExecuteAsync(new CommandDefinition(verify + " DISABLE TRIGGER dbo.TR_App05EvidenceFailSuccessor ON dbo.App05ASuccessorCommand;",
                scope, cancellationToken: ct));
            await Acknowledge("successor-rollback");

            if (!await Wait("successor-revoke", exited)) return false;
            var revoke = await Read<SuccessorRevocation>("successor-revoke");
            var accessScope = new { scope.Tenant, scope.Name, scope.Nonce, revoke.TripId, App05CloseBootstrap.Ops };
            var originalTransfer = await ReadOriginalTransfer();
            Check("dropped ACK follows a committed transfer of exactly 4 EA", Primary, 4m, originalTransfer.Quantity);
            Check("destination write assignment revoked", Primary, 1,
                await db.ExecuteAsync(new CommandDefinition(verify + """
                     UPDATE dbo.App05TripOperator SET IsActive=0
                    WHERE TenantId=@Tenant AND TripId=@TripId AND ActorId=@Ops AND IsActive=1;
                    """, accessScope, cancellationToken: ct)));
            await Acknowledge("successor-revoke");

            if (!await Wait("successor-restore", exited)) return false;
            Check("authorization failure preserves the committed transfer receipt", Primary,
                originalTransfer, await ReadOriginalTransfer());
            Check("destination write assignment restored", Primary, 1,
                await db.ExecuteAsync(new CommandDefinition(verify + """
                     UPDATE dbo.App05TripOperator SET IsActive=1
                    WHERE TenantId=@Tenant AND TripId=@TripId AND ActorId=@Ops AND IsActive=0;
                    """, accessScope, cancellationToken: ct)));
            await Acknowledge("successor-restore");

            Task<SuccessorCommitted> ReadOriginalTransfer() =>
                db.QuerySingleAsync<SuccessorCommitted>(new CommandDefinition("""
                    SELECT x.SuccessorRightId,x.NeverLoaded+x.Returned AS Quantity,CONVERT(varchar(64),c.PayloadHash,2) AS PayloadHash,c.Receipt
                    FROM dbo.App05ASuccessorCommand c
                    JOIN dbo.App05AHeldTransfer x ON x.TenantId=c.TenantId
                        AND x.SuccessorRightId=TRY_CONVERT(uniqueidentifier,JSON_VALUE(c.Receipt,'$.successorRightId'))
                    WHERE c.TenantId=@Tenant AND c.ActorId=@Ops AND c.CommandId=@CommandId AND c.Applied=1;
                    """, new { scope.Tenant, App05CloseBootstrap.Ops, revoke.CommandId }, cancellationToken: ct));

            if (!await Wait("successor-after", exited)) return false;
            Check("same-ID recovery after invalid ACKs preserves the exact committed receipt", Primary,
                originalTransfer, await ReadOriginalTransfer());
            var result = await Read<SuccessorRace>("successor-after");
            var actual = (await db.QueryAsync<SuccessorOracle>(new CommandDefinition("""
                SELECT b.OrderLineId,SUM(b.Quantity) AS Scheduled,SUM(t.Returned) AS Returned,
                    SUM(t.NeverLoaded) AS NeverLoaded,SUM(t.LotQuantity) AS LotQuantity,SUM(t.Rights) AS Rights
                FROM dbo.App05ASuccessor b
                CROSS APPLY (
                    SELECT COALESCE(SUM(x.Returned),0) AS Returned,COALESCE(SUM(x.NeverLoaded),0) AS NeverLoaded,
                        COALESCE(SUM(l.Quantity),0) AS LotQuantity,COUNT(*) AS Rights
                    FROM dbo.App05AHeldTransfer x CROSS APPLY(
                        SELECT SUM(l.NeverLoaded+l.Returned) AS Quantity FROM dbo.App05AHeldTransferLot l
                        WHERE l.TenantId=x.TenantId AND l.SuccessorRightId=x.SuccessorRightId
                    ) l WHERE x.TenantId=b.TenantId AND x.BindingId=b.BindingId
                ) t WHERE b.TenantId=@Tenant GROUP BY b.OrderLineId;
                """, scope, cancellationToken: ct))).ToDictionary(row => row.OrderLineId);
            Check("returned right transferred exactly twice with matching lot provenance", Primary,
                new SuccessorOracle(scope.MainLine, 15, 9, 0, 9, 2), actual[scope.MainLine]);
            Check("never-loaded right race creates one conserved generation", Primary,
                new SuccessorOracle(scope.HeldLine, 5, 0, result.Winner, result.Winner, 1), actual[scope.HeldLine]);
            Check("unreserved Confirm scheduled with no right or lot", Primary,
                new SuccessorOracle(scope.UnreservedLine, 5, 0, 0, 0, 0), actual[scope.UnreservedLine]);
            Check("allocation, original rights, confirmed snapshots, physical stock and financial ledger conserved", Primary,
                before, await EffectDigest(Primary));
            await using var peer = await OpenDatabase(Peer);
            Check("no bindings or successor rights in contrast tenant", Peer, 0,
                await peer.ExecuteScalarAsync<int>(new CommandDefinition("""
                    SELECT (SELECT COUNT(*) FROM dbo.App05ASuccessor)+(SELECT COUNT(*) FROM dbo.App05AHeldTransfer);
                    """, cancellationToken: ct)));
            await Acknowledge("successor-after");
            if (!await Wait("approval-transfer-before", exited)) return false;
            string beforeTransferRace = await EffectDigest(Primary);
            await Acknowledge("approval-transfer-before");
            if (!await Wait("approval-transfer-after", exited)) return false;
            var transferRace = await Read<ApprovalTransferRace>("approval-transfer-after");
            Check("racing a rejected approval and transfer has no stock or financial effect", Primary,
                beforeTransferRace, await EffectDigest(Primary));
            Check("transfer race retains the exact HELD and lot generation", Primary, 1,
                await db.ExecuteScalarAsync<int>(new CommandDefinition("""
                    SELECT COUNT(*) FROM dbo.App05AHeldTransfer h
                    JOIN dbo.App05AHeldTransferLot l ON l.TenantId=h.TenantId AND l.SuccessorRightId=h.SuccessorRightId
                    WHERE h.TenantId=@Tenant AND h.OrderLineId=@LineId AND h.SuccessorRightId=@RightId
                        AND h.NeverLoaded=1 AND h.Returned=2 AND l.NeverLoaded=1 AND l.Returned=2
                        AND NOT EXISTS(SELECT 1 FROM dbo.App05ARemainderApproval a
                            WHERE a.TenantId=h.TenantId AND a.OrderLineId=h.OrderLineId);
                    """, new { scope.Tenant, transferRace.LineId, transferRace.RightId }, cancellationToken: ct)));
            await Acknowledge("approval-transfer-after");

            if (!await Wait("reserve-before", exited)) return false;
            var pool = await Read<ReserveBefore>("reserve-before");
            string oldFinancial = await ReserveFinancialDigest();
            string peerBefore = await EffectDigest(Peer);
            var lotsBefore = (await db.QueryAsync<ReserveLotBalance>(new CommandDefinition("""
                SELECT l.Id AS LotId,l.Code,l.ExpiresOn,l.SkuId,o.LocationId AS WarehouseId,o.Quantity AS Opening,
                    COALESCE(h.Held,0) AS Held
                FROM dbo.F03Lot l
                JOIN dbo.F03LotOpening o ON o.TenantId=l.TenantId AND o.LotId=l.Id AND o.LocationId=@WarehouseId
                OUTER APPLY(SELECT SUM(h.Quantity) AS Held FROM dbo.F03LotHold h
                    JOIN dbo.StockRight r ON r.TenantId=h.TenantId AND r.Id=h.StockRightId AND r.StockLocationId=o.LocationId
                    WHERE h.TenantId=l.TenantId AND h.LotId=l.Id) h
                WHERE l.TenantId=@Tenant AND l.SkuId=@SkuId;
                """, new { scope.Tenant, pool.WarehouseId, pool.SkuId }, cancellationToken: ct))).ToDictionary(lot => lot.Code);
            var day = await db.ExecuteScalarAsync<DateTime>(new CommandDefinition("""
                SELECT BusinessDate FROM dbo.Trip WHERE TenantId=@Tenant AND Id=@FirstTrip;
                """, new { scope.Tenant, pool.FirstTrip }, cancellationToken: ct));
            var originalLot = lotsBefore["DAY-A-ORIGIN"];
            var earlyLot = lotsBefore["DAY-A-EARLY"];
            var lateLot = lotsBefore["DAY-A-LATE"];
            var expiredLot = lotsBefore["DAY-A-EXPIRED"];
            Check("fixture has four warehouse opening lots", Primary, 4, lotsBefore.Count);
            Check("original lot is exhausted by predecessor reservations", Primary, 0m, originalLot.Opening - originalLot.Held);
            Check("earliest eligible lot has two free units", Primary, 2m, earlyLot.Opening - earlyLot.Held);
            Check("later eligible lot has four free units", Primary, 4m, lateLot.Opening - lateLot.Held);
            Check("five unheld units are already expired on the successor day", Primary, 5m, expiredLot.Opening - expiredLot.Held);
            Check("fixture FEFO order and expiration are anchored to destination date", Primary, true,
                expiredLot.ExpiresOn < day && day <= earlyLot.ExpiresOn && earlyLot.ExpiresOn < lateLot.ExpiresOn);
            Check("scheduling and transferring do not grow the MAIN allocation", Primary,
                new ReservePool(33, 22), await ReadPool(pool.SkuId));
            await Acknowledge("reserve-before");

            if (!await Wait("reserve-revoke-origin", exited)) return false;
            var reserveAck = await Read<SuccessorFault>("reserve-revoke-origin");
            var committedReserve = await ReadReserveReceipt();
            Check("lost ACK follows one committed Reserve of one unit", Primary, 1m, committedReserve.Quantity);
            await ChangeReserveWriter(pool.MixedTrip, DevF05Evidence.Receiver, App03DayBootstrap.Actor, "origin warehouse scope revoked");
            await Acknowledge("reserve-revoke-origin");

            if (!await Wait("reserve-revoke-destination", exited)) return false;
            Check("origin denial preserves the committed Reserve receipt", Primary, committedReserve, await ReadReserveReceipt());
            await ChangeReserveWriter(pool.MixedTrip, App03DayBootstrap.Actor, DevF05Evidence.Receiver, "origin warehouse scope restored");
            await ChangeReserveWriter(pool.FirstTrip, DevF05Evidence.Receiver, App03DayBootstrap.Actor, "destination warehouse scope revoked");
            await Acknowledge("reserve-revoke-destination");

            if (!await Wait("reserve-restore", exited)) return false;
            Check("destination denial preserves the committed Reserve receipt", Primary, committedReserve, await ReadReserveReceipt());
            await ChangeReserveWriter(pool.FirstTrip, App03DayBootstrap.Actor, DevF05Evidence.Receiver, "destination warehouse scope restored");
            await Acknowledge("reserve-restore");

            async Task ChangeReserveWriter(Guid tripId, string from, string to, string metric) =>
                Check(metric, Primary, 1, await db.ExecuteAsync(new CommandDefinition(verify + """
                    UPDATE dbo.C3TripWriter SET ActorId=@To
                    WHERE TenantId=@Tenant AND TripId=@TripId AND ActorId=@From;
                    """, new { scope.Tenant, scope.Name, scope.Nonce, TripId = tripId, From = from, To = to }, cancellationToken: ct)));

            Task<SuccessorCommitted> ReadReserveReceipt() =>
                db.QuerySingleAsync<SuccessorCommitted>(new CommandDefinition("""
                    SELECT r.Id AS SuccessorRightId,r.GrantedBaseQuantity AS Quantity,
                        CONVERT(varchar(64),c.PayloadHash,2) AS PayloadHash,c.Receipt
                    FROM dbo.App05ASuccessorCommand c
                    JOIN dbo.App05ASuccessorReserve a ON a.TenantId=c.TenantId
                        AND a.RightId=TRY_CONVERT(uniqueidentifier,JSON_VALUE(c.Receipt,'$.rightId'))
                    JOIN dbo.StockRight r ON r.TenantId=a.TenantId AND r.Id=a.RightId
                    WHERE c.TenantId=@Tenant AND c.ActorId=@Receiver AND c.CommandId=@CommandId AND c.Applied=1;
                    """, new { scope.Tenant, DevF05Evidence.Receiver, reserveAck.CommandId }, cancellationToken: ct));

            if (!await Wait("reserve-arm", exited)) return false;
            var reserveFault = await Read<SuccessorFault>("reserve-arm");
            string reserveStatement = $"""
                CREATE OR ALTER TRIGGER dbo.TR_App05EvidenceFailSuccessor ON dbo.App05ASuccessorCommand AFTER INSERT AS
                BEGIN
                    SET NOCOUNT ON;
                    IF EXISTS(SELECT 1 FROM inserted WHERE TenantId=N'tenant-a' AND CommandId='{reserveFault.CommandId:D}')
                        THROW 51927,'APP-05.4a synthetic reserve precommit fault.',1;
                END;
                """;
            await db.ExecuteAsync(new CommandDefinition(verify + """
                EXEC(@Statement);
                ENABLE TRIGGER dbo.TR_App05EvidenceFailSuccessor ON dbo.App05ASuccessorCommand;
                """, new { scope.Tenant, scope.Name, scope.Nonce, Statement = reserveStatement }, cancellationToken: ct));
            await Acknowledge("reserve-arm");

            if (!await Wait("reserve-rollback", exited)) return false;
            Check("reserve rollback leaves only the already committed one-unit allocation", Primary,
                new ReservePool(33, 23), await ReadPool(pool.SkuId));
            Check("reserve rollback leaves no right or durable receipt for the failed command", Primary, 0,
                await db.ExecuteScalarAsync<int>(new CommandDefinition("""
                    SELECT (SELECT COUNT(*) FROM dbo.App05ASuccessorReserve WHERE TenantId=@Tenant AND OrderLineId=@Line)
                         + (SELECT COUNT(*) FROM dbo.App05ASuccessorCommand WHERE TenantId=@Tenant AND CommandId=@CommandId);
                    """, new { scope.Tenant, Line = pool.UnreservedLine, reserveFault.CommandId }, cancellationToken: ct)));
            await db.ExecuteAsync(new CommandDefinition(verify + " DISABLE TRIGGER dbo.TR_App05EvidenceFailSuccessor ON dbo.App05ASuccessorCommand;",
                scope, cancellationToken: ct));
            await Acknowledge("reserve-rollback");

            if (!await Wait("reserve-lot-before", exited)) return false;
            string beforeLotShortage = await ReadAllocationEvidence();
            await Acknowledge("reserve-lot-before");

            if (!await Wait("reserve-lot-shortage", exited)) return false;
            var lotShortage = await Read<SuccessorFault>("reserve-lot-shortage");
            Check("MAIN still has five free units when all eligible lots are exhausted", Primary,
                new ReservePool(33, 28), await ReadPool(pool.SkuId));
            Check("lot shortage writes one rejection and no stock right for the line", Primary, 1,
                await db.ExecuteScalarAsync<int>(new CommandDefinition("""
                    SELECT COUNT(*) FROM dbo.App05ASuccessorCommand c
                    WHERE c.TenantId=@Tenant AND c.ActorId=@Receiver AND c.CommandId=@CommandId AND c.Applied=0
                        AND JSON_VALUE(c.Receipt,'$.code')='F03_LOT_STOCK_INSUFFICIENT'
                        AND NOT EXISTS(SELECT 1 FROM dbo.App05ASuccessorReserve
                            WHERE TenantId=@Tenant AND OrderLineId=@HeldLine);
                    """, new { scope.Tenant, DevF05Evidence.Receiver, lotShortage.CommandId, pool.HeldLine }, cancellationToken: ct)));
            Check("lot rejection leaves rights, holds, stock sources and allocation ledger unchanged", Primary,
                beforeLotShortage, await ReadAllocationEvidence());

            Task<string> ReadAllocationEvidence() => db.ExecuteScalarAsync<string>(new CommandDefinition("""
                SELECT CONVERT(varchar(64),HASHBYTES('SHA2_256',(
                    SELECT
                        JSON_QUERY((SELECT * FROM dbo.StockRight WHERE TenantId=@Tenant ORDER BY Id FOR JSON PATH)) AS Rights,
                        JSON_QUERY((SELECT * FROM dbo.F03LotHold WHERE TenantId=@Tenant ORDER BY StockRightId,LotId FOR JSON PATH)) AS Holds,
                        JSON_QUERY((SELECT * FROM dbo.BusinessSource WHERE TenantId=@Tenant AND SourceKind='STOCK_ALLOC'
                            ORDER BY Id FOR JSON PATH)) AS Sources,
                        JSON_QUERY((SELECT * FROM dbo.LedgerEffect WHERE TenantId=@Tenant AND LedgerKind='RIGHT_A'
                            ORDER BY Id FOR JSON PATH)) AS Ledger
                    FOR JSON PATH, WITHOUT_ARRAY_WRAPPER)),2);
                """, scope, cancellationToken: ct))!;
            await Acknowledge("reserve-lot-shortage");

            if (!await Wait("reserve-cross-trip", exited)) return false;
            Check("independent B warehouse actor receives the second race trip", Primary, 1,
                await db.ExecuteAsync(new CommandDefinition(verify + """
                    UPDATE w SET ActorId=@Actor FROM dbo.C3TripWriter w
                    JOIN dbo.Trip t ON t.TenantId=w.TenantId AND t.Id=w.TripId AND t.Status='PREPARED'
                    JOIN dbo.App02ActorRoleGrant g ON g.TenantId=t.TenantId AND g.ActorId=@Actor AND g.RoleCode='warehouse' AND g.IsActive=1
                    WHERE w.TenantId=@Tenant AND w.TripId=@SecondTrip AND w.ActorId=@Receiver;
                    """, new { scope.Tenant, scope.Name, scope.Nonce, pool.SecondTrip, App03DayBootstrap.Actor, DevF05Evidence.Receiver }, cancellationToken: ct)));
            await Acknowledge("reserve-cross-trip");

            if (!await Wait("reserve-after", exited)) return false;
            var race = await Read<ReserveRace>("reserve-after");
            Check("lot SKU Reserve allocates exactly six new units", Primary,
                new ReservePool(33, 28), await ReadPool(pool.SkuId));
            Check("shared A/B allocation ends at the capacity of MAIN", Primary,
                new ReservePool(9, 9), await ReadPool(race.SharedSkuId));
            var grants = (await db.QueryAsync<ReserveGrant>(new CommandDefinition("""
                SELECT a.OrderLineId, COUNT(*) AS Rights, SUM(r.GrantedBaseQuantity) AS Quantity,
                    SUM(COALESCE(h.Quantity,0)) AS LotQuantity, SUM(e.QuantityDelta) AS LedgerQuantity
                FROM dbo.App05ASuccessorReserve a
                JOIN dbo.StockRight r ON r.TenantId=a.TenantId AND r.Id=a.RightId AND r.TripId=a.DestinationTripId
                JOIN dbo.LedgerEffect e ON e.TenantId=r.TenantId AND e.BusinessSourceLineId=r.BusinessSourceLineId
                    AND e.LedgerKind='RIGHT_A' AND e.AmountDelta=0
                OUTER APPLY (SELECT SUM(h.Quantity) AS Quantity FROM dbo.F03LotHold h
                    WHERE h.TenantId=r.TenantId AND h.StockRightId=r.Id) h
                WHERE a.TenantId=@Tenant GROUP BY a.OrderLineId;
                """, scope, cancellationToken: ct))).ToDictionary(row => row.OrderLineId);
            Check("mixed line creates one newly reserved right with one lot unit", Primary,
                new ReserveGrant(pool.MixedLine, 1, 1, 1, 1), grants[pool.MixedLine]);
            Check("the first Reserve on the unreserved Confirm creates exactly five lot units", Primary,
                new ReserveGrant(pool.UnreservedLine, 1, 5, 5, 5), grants[pool.UnreservedLine]);
            Check("only the winning A command can create the shared non-lot right", Primary, race.WinnerA ? 1m : 0m,
                grants.GetValueOrDefault(race.SharedLine)?.Quantity ?? 0);
            Check("same-ID Reserve replay preserves exact right and receipt", Primary, committedReserve, await ReadReserveReceipt());
            var picks = (await db.QueryAsync<ReserveLotPick>(new CommandDefinition("""
                SELECT a.OrderLineId,l.Id AS LotId,l.Code,l.ExpiresOn,l.SkuId,r.StockLocationId AS WarehouseId,h.Quantity
                FROM dbo.App05ASuccessorReserve a
                JOIN dbo.StockRight r ON r.TenantId=a.TenantId AND r.Id=a.RightId
                JOIN dbo.F03LotHold h ON h.TenantId=r.TenantId AND h.StockRightId=r.Id
                JOIN dbo.F03Lot l ON l.TenantId=h.TenantId AND l.Id=h.LotId
                WHERE a.TenantId=@Tenant ORDER BY l.ExpiresOn,l.Code,l.Id;
                """, scope, cancellationToken: ct))).ToLookup(pick => pick.OrderLineId);
            var expectedMixed = new ReserveLotPick(pool.MixedLine, earlyLot.LotId, earlyLot.Code, earlyLot.ExpiresOn,
                pool.SkuId, pool.WarehouseId, 1);
            var expectedFirst = expectedMixed with { OrderLineId = pool.UnreservedLine };
            var expectedSecond = new ReserveLotPick(pool.UnreservedLine, lateLot.LotId, lateLot.Code, lateLot.ExpiresOn,
                pool.SkuId, pool.WarehouseId, 4);
            Check("mixed Reserve picks only the earliest eligible lot", Primary, expectedMixed, picks[pool.MixedLine].Single());
            Check("first-time Reserve starts with remaining stock in the earliest lot", Primary, expectedFirst, picks[pool.UnreservedLine].First());
            Check("first-time Reserve fills the balance from the next eligible lot", Primary, expectedSecond, picks[pool.UnreservedLine].Skip(1).Single());
            Check("all new lot allocations have the expected FEFO provenance", Primary, 3, picks.Sum(group => group.Count()));
            Check("contrast tenant business state preserved across Reserve probes", Peer, peerBefore, await EffectDigest(Peer));
            Check("contrast tenant has no Reserve command receipts", Peer, 0,
                await peer.ExecuteScalarAsync<int>(new CommandDefinition("SELECT COUNT(*) FROM dbo.App05ASuccessorCommand;", cancellationToken: ct)));
            Check("Reserve does not move physical stock or change financial ledgers", Primary, oldFinancial, await ReserveFinancialDigest());
            Check("no successor Reserve in the peer database", Peer, 0,
                await peer.ExecuteScalarAsync<int>(new CommandDefinition("SELECT COUNT(*) FROM dbo.App05ASuccessorReserve;", cancellationToken: ct)));
            await Acknowledge("reserve-after");

            if (!await Wait("successor-delivery-stale", exited)) return false;
            var stale = await Read<SuccessorStaleDelivery>("successor-delivery-stale");
            await SetSuccessorDate(stale.TripId, 26, 25);
            await Acknowledge("successor-delivery-stale");
            if (!await Wait("successor-delivery-current", exited)) return false;
            Check("a fresh stale-day successor attempt creates no C.4 evidence", Primary, 0,
                await db.ExecuteScalarAsync<int>(new CommandDefinition("""
                    SELECT COUNT(*) FROM dbo.C4DeliveryEvidence WHERE TenantId=@Tenant AND ClientCommandId=@CommandId;
                    """, new { scope.Tenant, stale.CommandId }, cancellationToken: ct)));
            await SetSuccessorDate(stale.TripId, 25, 26);
            await Acknowledge("successor-delivery-current");

            Task SetSuccessorDate(Guid id, int before, int after) => db.ExecuteAsync(new CommandDefinition(verify + """
                UPDATE dbo.Trip SET BusinessDate=DATEFROMPARTS(2026,11,@After)
                WHERE TenantId=@Tenant AND Id=@Id AND BusinessDate=DATEFROMPARTS(2026,11,@Before) AND Status='PREPARED';
                IF @@ROWCOUNT<>1 THROW 51929,'Expected exactly the owned successor test trip.',1;
                """, new { scope.Tenant, scope.Nonce, scope.Name, Id = id, Before = before, After = after }, cancellationToken: ct));

            if (!await sources.VerifyApprovalDeliveryGuard(exited)) return false;
            if (!await Wait("successor-reserve-delivered", exited)) return false;
            var delivered = await Read<SuccessorReserveDelivery>("successor-reserve-delivered");
            var identity = new
            {
                scope.Tenant, delivered.OrderId, delivered.OrderLineId, delivered.ConfirmSourceId, delivered.OriginTripId,
                delivered.DestinationTripId, delivered.BindingId, delivered.RightId, delivered.ManifestLineId,
                delivered.DeliverySourceId, delivered.LoadSourceId
            };
            Check("successor delivery preserves exact Confirm, order, location, lot and source lineage", Primary, 1,
                await db.ExecuteScalarAsync<int>(new CommandDefinition("""
                    SELECT COUNT(*)
                    FROM dbo.SaleDelivery d
                    JOIN dbo.AOrder o ON o.TenantId=d.TenantId AND o.Id=d.AOrderId AND o.TripId=@OriginTripId
                    JOIN dbo.AConfirmedLineSnapshot s ON s.TenantId=d.TenantId AND s.AOrderId=d.AOrderId
                        AND s.AOrderLineId=d.AOrderLineId AND s.TripId=o.TripId AND s.SkuId=d.SkuId
                    JOIN dbo.TripManifestLine m ON m.TenantId=d.TenantId AND m.Id=d.TripManifestLineId
                        AND m.TripId=d.TripId AND m.StockRightId=d.StockRightId AND m.LoadedBaseQuantity=1
                    JOIN dbo.App05ASuccessorReserve r ON r.TenantId=d.TenantId AND r.RightId=d.StockRightId
                        AND r.DestinationTripId=d.TripId AND r.BindingId=d.App05BindingId
                        AND r.WarehouseId=m.WarehouseLocationId
                    JOIN dbo.F03LotLoad l ON l.TenantId=m.TenantId AND l.ManifestLineId=m.Id AND l.Quantity=1
                    JOIN dbo.F03LotIssue j ON j.TenantId=d.TenantId AND j.DeliveryId=d.Id AND j.LotId=l.LotId AND j.Quantity=1
                    JOIN dbo.F03Lot k ON k.TenantId=l.TenantId AND k.Id=l.LotId AND k.Code=N'DAY-A-EARLY'
                    JOIN dbo.ReceivableOpenItem ar ON ar.TenantId=d.TenantId AND ar.SaleDeliveryId=d.Id
                        AND ar.DueBusinessDate=DATEADD(day,s.TermsDaysSnapshot,d.BusinessDate) AND ar.OriginalAmount=d.PayableAmount
                    WHERE d.TenantId=@Tenant AND d.AOrderId=@OrderId AND d.AOrderLineId=@OrderLineId
                        AND s.ConfirmBusinessSourceId=@ConfirmSourceId AND d.App05OriginTripId=@OriginTripId
                        AND d.TripId=@DestinationTripId AND d.App05BindingId=@BindingId AND d.StockRightId=@RightId
                        AND d.TripManifestLineId=@ManifestLineId AND d.BusinessSourceId=@DeliverySourceId
                        AND m.LoadBusinessSourceId=@LoadSourceId AND d.BaseQuantity=1 AND d.C4PriorLineQuantity=1
                        AND d.BusinessDate=DATEFROMPARTS(2026,11,26) AND d.ShipToSnapshotId=o.ShipToSnapshotId;
                    """, identity, cancellationToken: ct)));
            Check("successor replay leaves exactly two delivered EA across both trips", Primary, 2m,
                await db.ExecuteScalarAsync<decimal>(new CommandDefinition("""
                    SELECT SUM(BaseQuantity) FROM dbo.SaleDelivery WHERE TenantId=@Tenant AND AOrderLineId=@OrderLineId;
                    """, identity, cancellationToken: ct)));
            Check("loading the reserved EA decreases physical and allocated MAIN exactly once", Primary,
                new ReservePool(32, 27), await ReadPool(pool.SkuId));
            Check("successor load and delivery leave the contrast tenant unchanged", Peer, peerBefore, await EffectDigest(Peer));
            await Acknowledge("successor-reserve-delivered");

            if (!await Wait("successor-ar-destination-only", exited)) return false;
            await ChangeOriginDeliveryWriter(App03DayBootstrap.Actor, "app05-oracle-other-sales");
            await Acknowledge("successor-ar-destination-only");
            if (!await Wait("successor-ar-origins-restored", exited)) return false;
            await ChangeOriginDeliveryWriter("app05-oracle-other-sales", App03DayBootstrap.Actor);
            await Acknowledge("successor-ar-origins-restored");

            Task ChangeOriginDeliveryWriter(string from, string to) => db.ExecuteAsync(new CommandDefinition(verify + """
                UPDATE dbo.C4DeliveryWriter SET ActorId=@To
                WHERE TenantId=@Tenant AND TripId=@OriginTripId AND ActorId=@From;
                IF @@ROWCOUNT<>1 THROW 51930,'Expected exactly the owned origin test writer.',1;
                """, new { scope.Tenant, scope.Nonce, scope.Name, delivered.OriginTripId, From = from, To = to }, cancellationToken: ct));

            if (!await Wait("successor-held-delivered", exited)) return false;
            var heldDelivery = await Read<SuccessorHeldDelivery>("successor-held-delivered");
            var heldScope = new { scope.Tenant, heldDelivery.MainLineId, heldDelivery.MixedLineId, heldDelivery.SuccessorTripId,
                heldDelivery.MainBindingId, heldDelivery.MixedBindingId, heldDelivery.MixedRightId };
            Check("mixed HELD conserves never-loaded and D.3 returned quantities and issues the original lot", Primary, 1,
                await db.ExecuteScalarAsync<int>(new CommandDefinition("""
                    SELECT COUNT(*) FROM dbo.App05AHeldExecution e
                    JOIN dbo.App05AHeldTransfer h ON h.TenantId=e.TenantId AND h.SuccessorRightId=e.RightId
                    JOIN dbo.StockRight origin ON origin.TenantId=e.TenantId AND origin.Id=e.OriginalRightId
                    JOIN dbo.StockRight child ON child.TenantId=e.TenantId AND child.Id=e.RightId
                    JOIN dbo.SaleDelivery d ON d.TenantId=e.TenantId AND d.StockRightId=e.RightId AND d.App05BindingId=e.BindingId
                        AND d.App05HeldExecutorId=e.RightId AND d.TripId=e.DestinationTripId AND d.AOrderLineId=e.OrderLineId
                    JOIN dbo.App05AHeldTransferLot lot ON lot.TenantId=e.TenantId AND lot.SuccessorRightId=e.RightId
                    JOIN dbo.F03LotIssue issue ON issue.TenantId=d.TenantId AND issue.DeliveryId=d.Id AND issue.LotId=lot.LotId
                    WHERE e.TenantId=@Tenant AND e.RightId=@MixedRightId AND e.OrderLineId=@MixedLineId
                        AND e.BindingId=@MixedBindingId AND e.DestinationTripId=@SuccessorTripId
                        AND h.NeverLoaded=1 AND h.Returned=2 AND origin.ReleasedBaseQuantity=3
                        AND child.GrantedBaseQuantity=3 AND child.ConsumedBaseQuantity=3
                        AND lot.NeverLoaded=1 AND lot.Returned=2 AND issue.Quantity=3 AND d.BaseQuantity=3;
                    """, heldScope, cancellationToken: ct)));
            Check("A40 has exactly 25 on the origin and 15 on the successor with full frozen value", Primary, 1,
                await db.ExecuteScalarAsync<int>(new CommandDefinition("""
                    SELECT COUNT(*) FROM dbo.F04PriceLine p
                    JOIN dbo.AConfirmedLineSnapshot s ON s.TenantId=p.TenantId AND s.AOrderLineId=p.OrderLineId
                    JOIN dbo.AOrder o ON o.TenantId=s.TenantId AND o.Id=s.AOrderId AND o.Status='FULFILLED'
                    CROSS APPLY (
                        SELECT SUM(IIF(d.App05BindingId IS NULL,d.BaseQuantity,0)) AS OriginalDelivered,
                               SUM(IIF(d.TripId=@SuccessorTripId AND d.App05BindingId=@MainBindingId,d.BaseQuantity,0)) AS SuccessorDelivered,
                               SUM(price.NetAmount) AS NetAmount,SUM(price.TaxAmount) AS TaxAmount,SUM(price.GrossAmount) AS GrossAmount
                        FROM dbo.SaleDelivery d JOIN dbo.F04DeliveryPricing price ON price.TenantId=d.TenantId AND price.DeliveryId=d.Id
                        WHERE d.TenantId=s.TenantId AND d.AOrderLineId=s.AOrderLineId
                            AND (d.TripId=s.TripId OR d.TripId=@SuccessorTripId)
                    ) x
                    WHERE p.TenantId=@Tenant AND p.OrderLineId=@MainLineId AND s.ConfirmedBaseQuantity=40
                        AND x.OriginalDelivered=25 AND x.SuccessorDelivered=15
                        AND x.NetAmount=p.NetAmount AND x.TaxAmount=p.TaxAmount AND x.GrossAmount=p.GrossAmount;
                    """, heldScope, cancellationToken: ct)));
            Check("HELD rerouting and two successor loads conserve MAIN allocation and physical quantity", Primary,
                new ReservePool(14, 9), await ReadPool(pool.SkuId));
            Check("the HELD journey preserves the contrast tenant", Peer, peerBefore, await EffectDigest(Peer));
            Check("all seven approval probes leave exactly one rejected receipt and no approval effect per command",
                Primary, 7, await db.ExecuteScalarAsync<int>(new CommandDefinition("""
                    SELECT COUNT(*) FROM dbo.App05ASuccessorCommand c
                    WHERE c.TenantId=@Tenant AND c.ActorId=@Checker AND c.CommandId IN @Commands AND c.Applied=0
                        AND JSON_VALUE(c.Receipt,'$.code') IN ('A_CLOSURE_SUCCESSOR_ACTIVE','A_CLOSURE_SCOPE_CHANGED',
                            'A_CLOSURE_ON_VEHICLE','A_CLOSURE_DELIVERY_PENDING','A_CLOSURE_REMAINDER_NOT_ELIGIBLE')
                        AND NOT EXISTS(SELECT 1 FROM dbo.App05ARemainderApproval a
                            WHERE a.TenantId=c.TenantId AND a.OrderLineId=@MixedLineId);
                    """, new { scope.Tenant, Checker = App05CloseBootstrap.ClosureChecker,
                        Commands = heldDelivery.ApprovalCommandIds, heldDelivery.MixedLineId }, cancellationToken: ct)));
            Check("mixed line keeps the original Confirm and fulfills exactly five units with no remaining HELD", Primary, 1,
                await db.ExecuteScalarAsync<int>(new CommandDefinition("""
                    SELECT COUNT(*) FROM dbo.AConfirmedLineSnapshot s
                    WHERE s.TenantId=@Tenant AND s.AOrderLineId=@MixedLineId AND s.ConfirmedBaseQuantity=5
                        AND (SELECT SUM(d.BaseQuantity) FROM dbo.SaleDelivery d
                            WHERE d.TenantId=s.TenantId AND d.AOrderLineId=s.AOrderLineId)=5
                        AND (SELECT SUM(r.GrantedBaseQuantity-r.ConsumedBaseQuantity-r.ReleasedBaseQuantity)
                            FROM dbo.AReservation a JOIN dbo.StockRight r ON r.TenantId=a.TenantId AND r.Id=a.StockRightId
                            WHERE a.TenantId=s.TenantId AND a.AOrderLineId=s.AOrderLineId)=0;
                    """, heldScope, cancellationToken: ct)));
            await Acknowledge("successor-held-delivered");

            if (!await Wait("remainder-proposal-before", exited)) return false;
            string beforeProposal = await EffectDigest(Primary);
            await Acknowledge("remainder-proposal-before");
            if (!await Wait("remainder-proposal-after", exited)) return false;
            var proposal = await Read<RemainderProposalProof>("remainder-proposal-after");
            Check("remainder proposal replay leaves exactly one request for the line", Primary, 1,
                await db.ExecuteScalarAsync<int>(new CommandDefinition("""
                    SELECT COUNT(*) FROM dbo.App05ARemainderClosureProposal WHERE TenantId=@Tenant AND OrderLineId=@LineId;
                    """, new { scope.Tenant, proposal.LineId }, cancellationToken: ct)));
            Check("the persisted proposal binds the exact maker, command, Confirm, follow-up and revision", Primary, 1,
                await db.ExecuteScalarAsync<int>(new CommandDefinition("""
                    SELECT COUNT(*) FROM dbo.App05ARemainderClosureProposal
                    WHERE TenantId=@Tenant AND ProposalId=@ProposalId AND OrderLineId=@LineId
                        AND ConfirmSourceId=@ConfirmSourceId AND Quantity=1 AND Maker=@Ops
                        AND CommandId=@CommandId AND FollowUpId=@FollowUpId AND Reason=@Reason
                        AND CONVERT(varchar(64),SourceRevision,2)=@Revision;
                    """, new { scope.Tenant, proposal.ProposalId, proposal.LineId, proposal.ConfirmSourceId,
                        proposal.CommandId, proposal.FollowUpId, proposal.Reason, proposal.Revision, App05CloseBootstrap.Ops }, cancellationToken: ct)));
            Check("a remainder proposal preserves stock, settlement, financial evidence and Confirm", Primary,
                beforeProposal, await EffectDigest(Primary));
            await Acknowledge("remainder-proposal-after");

            Task<ReservePool> ReadPool(Guid sku) => db.QuerySingleAsync<ReservePool>(new CommandDefinition("""
                SELECT OnHandBaseQuantity AS OnHand,AllocatedBaseQuantity AS Allocated
                FROM dbo.StockPool WHERE TenantId=@Tenant AND StockLocationId=@WarehouseId AND SkuId=@Sku;
                """, new { scope.Tenant, pool.WarehouseId, Sku = sku }, cancellationToken: ct));

            Task<string> ReserveFinancialDigest() => db.ExecuteScalarAsync<string>(new CommandDefinition("""
                SELECT CONVERT(varchar(64),HASHBYTES('SHA2_256',(
                    SELECT
                        JSON_QUERY((SELECT * FROM dbo.LedgerEffect WHERE TenantId=@Tenant AND LedgerKind NOT IN ('RIGHT_A','RIGHT_B')
                            ORDER BY Id FOR JSON PATH)) AS Ledger,
                        JSON_QUERY((SELECT * FROM dbo.PhysicalStockMovement WHERE TenantId=@Tenant ORDER BY Id FOR JSON PATH)) AS Movements,
                        JSON_QUERY((SELECT * FROM dbo.PhysicalStockBalance WHERE TenantId=@Tenant
                            ORDER BY StockLocationId,SkuId FOR JSON PATH)) AS Stock,
                        JSON_QUERY((SELECT * FROM dbo.AConfirmedLineSnapshot WHERE TenantId=@Tenant ORDER BY AOrderLineId FOR JSON PATH)) AS Confirms,
                        JSON_QUERY((SELECT * FROM dbo.ReceivableOpenItem WHERE TenantId=@Tenant ORDER BY Id FOR JSON PATH)) AS Ar,
                        JSON_QUERY((SELECT * FROM dbo.DeliveryStockIssue WHERE TenantId=@Tenant ORDER BY Id FOR JSON PATH)) AS Cogs
                    FOR JSON PATH, WITHOUT_ARRAY_WRAPPER)),2);
                """, scope, cancellationToken: ct))!;
            return true;
        }

        async Task<bool> VerifyApproval(Task exited)
        {
            if (!await Wait("approval-before", exited)) return false;
            var fixture = await Read<ApprovalBefore>("approval-before");
            await using var db = await OpenDatabase(Primary);
            var scope = new { Tenant = Primary, fixture.TripId, fixture.LineId, fixture.DeliverySourceId };
            var baseline = await ReadUnchanged();
            decimal allocation = await ReadAllocation();
            await Acknowledge("approval-before");
            if (!await SetCheckerScope("approval-revoke", false) || !await SetCheckerScope("approval-restore", true)) return false;
            if (!await Wait("approval-after", exited)) return false;
            var receipt = await Read<ApprovalAfter>("approval-after");
            var results = await db.QueryAsync<ApprovedRight>(new CommandDefinition("""
                SELECT a.SourceId,a.Quantity,a.Released,r.NeverLoaded,r.Returned,l.NeverLoaded AS LotNeverLoaded,l.Returned AS LotReturned
                FROM dbo.App05ARemainderApproval a
                JOIN dbo.App05ARemainderClosureProposal p ON p.TenantId=a.TenantId AND p.ProposalId=a.ProposalId
                JOIN dbo.App05ARemainderRelease r ON r.TenantId=a.TenantId AND r.SourceId=a.SourceId AND r.OrderLineId=a.OrderLineId
                JOIN dbo.App05ARemainderReleaseLot l ON l.TenantId=r.TenantId AND l.SourceId=r.SourceId AND l.StockRightId=r.StockRightId
                JOIN dbo.BusinessSource s ON s.TenantId=a.TenantId AND s.Id=a.SourceId AND s.TripId=p.TripId AND s.SourceKind='A_REMAINDER_CLOSED'
                JOIN dbo.BusinessSourceLine b ON b.TenantId=r.TenantId AND b.Id=r.SourceLineId AND b.BusinessSourceId=s.Id
                JOIN dbo.LedgerEffect e ON e.TenantId=b.TenantId AND e.BusinessSourceLineId=b.Id
                    AND e.LedgerKind='RIGHT_A_RELEASED' AND e.QuantityDelta=-a.Released AND e.AmountDelta=0
                WHERE a.TenantId=@Tenant AND a.OrderLineId=@LineId AND p.TripId=@TripId AND p.Maker=@Ops AND a.Checker=@Checker;
                """, new { scope.Tenant, scope.TripId, scope.LineId, App05CloseBootstrap.Ops, Checker = App05CloseBootstrap.ClosureChecker },
                cancellationToken: ct));
            var rows = results.ToDictionary(r => r.SourceId);
            Check("approval releases one never-loaded unit after closing the one unheld unit", Primary,
                new ApprovedRight(receipt.FirstSourceId, 2, 1, 1, 0, 1, 0), rows[receipt.FirstSourceId]);
            Check("fractional approval releases part of the returned lot", Primary,
                new ApprovedRight(receipt.FractionalSourceId, 0.999m, 0.999m, 0, 0.999m, 0, 0.999m), rows[receipt.FractionalSourceId]);
            Check("final approval releases the last 0.001 of the returned lot", Primary,
                new ApprovedRight(receipt.FinalSourceId, 0.001m, 0.001m, 0, 0.001m, 0, 0.001m), rows[receipt.FinalSourceId]);
            Check("exactly three approvals preserve the original Confirm and close only after all obligations are satisfied",
                Primary, 1, await db.ExecuteScalarAsync<int>(new CommandDefinition("""
                    SELECT COUNT(*) FROM dbo.AConfirmedLineSnapshot s
                    JOIN dbo.AOrder o ON o.TenantId=s.TenantId AND o.Id=s.AOrderId AND o.Status='REMAINDER_CLOSED'
                    JOIN dbo.AReservation a ON a.TenantId=s.TenantId AND a.AOrderLineId=s.AOrderLineId
                    JOIN dbo.StockRight r ON r.TenantId=a.TenantId AND r.Id=a.StockRightId
                        AND r.Status='RELEASED' AND r.GrantedBaseQuantity=3 AND r.ConsumedBaseQuantity=1 AND r.ReleasedBaseQuantity=2
                    WHERE s.TenantId=@Tenant AND s.TripId=@TripId AND s.AOrderLineId=@LineId AND s.ConfirmedBaseQuantity=4
                        AND (SELECT COUNT(*) FROM dbo.App05ARemainderApproval x WHERE x.TenantId=s.TenantId AND x.OrderLineId=s.AOrderLineId)=3
                        AND (SELECT SUM(x.Quantity) FROM dbo.App05ARemainderApproval x WHERE x.TenantId=s.TenantId AND x.OrderLineId=s.AOrderLineId)=3;
                    """, scope, cancellationToken: ct)));
            Check("release puts two held units back in the MAIN free quantity", Primary, allocation - 2, await ReadAllocation());
            Check("approval leaves physical stock, Confirm, delivery, AR, COGS and original close intact", Primary, baseline, await ReadUnchanged());
            await Acknowledge("approval-after");
            return true;

            async Task<bool> SetCheckerScope(string phase, bool active)
            {
                if (!await Wait(phase, exited)) return false;
                await db.ExecuteAsync(new CommandDefinition("""
                    IF DB_NAME()<>@Name OR NOT EXISTS(SELECT 1 FROM dbo.LocalDemoOwner WHERE RunId=@Nonce)
                        OR NOT EXISTS(SELECT 1 FROM dbo.App00ScratchMarker WHERE Nonce=@Nonce)
                        OR NOT EXISTS(SELECT 1 FROM dbo.LocalApp03Session WHERE RunId=@Nonce AND TenantId=@Tenant)
                        THROW 51935,'APP-05.5 checker evidence scope mismatch.',1;
                    UPDATE dbo.App05TripOperator SET IsActive=@Active
                    WHERE TenantId=@Tenant AND TripId=@TripId AND ActorId=@Checker AND IsActive<>@Active;
                    IF @@ROWCOUNT<>1 THROW 51936,'APP-05.5 checker test scope changed.',1;
                    """, new { scope.Tenant, scope.TripId, Name = db.Database, Nonce = run.Id, Active = active,
                        Checker = App05CloseBootstrap.ClosureChecker }, cancellationToken: ct));
                await Acknowledge(phase);
                return true;
            }

            Task<decimal> ReadAllocation() => db.QuerySingleAsync<decimal>(new CommandDefinition("""
                SELECT p.AllocatedBaseQuantity FROM dbo.StockPool p
                JOIN dbo.AReservation a ON a.TenantId=p.TenantId AND a.AOrderLineId=@LineId
                JOIN dbo.StockRight r ON r.TenantId=a.TenantId AND r.Id=a.StockRightId
                    AND r.StockLocationId=p.StockLocationId AND r.SkuId=p.SkuId
                WHERE p.TenantId=@Tenant;
                """, scope, cancellationToken: ct));

            Task<string> ReadUnchanged() => db.QuerySingleAsync<string>(new CommandDefinition("""
                SELECT CONVERT(varchar(64),HASHBYTES('SHA2_256',(
                    SELECT
                        JSON_QUERY((SELECT * FROM dbo.PhysicalStockBalance WHERE TenantId=@Tenant
                            ORDER BY StockLocationId,SkuId FOR JSON PATH)) AS Physical,
                        JSON_QUERY((SELECT * FROM dbo.PhysicalStockMovement WHERE TenantId=@Tenant ORDER BY Id FOR JSON PATH)) AS Movements,
                        JSON_QUERY((SELECT * FROM dbo.SaleDelivery WHERE TenantId=@Tenant AND BusinessSourceId=@DeliverySourceId FOR JSON PATH)) AS Delivery,
                        JSON_QUERY((SELECT r.* FROM dbo.ReceivableOpenItem r JOIN dbo.SaleDelivery d ON d.TenantId=r.TenantId AND d.Id=r.SaleDeliveryId
                            WHERE d.TenantId=@Tenant AND d.BusinessSourceId=@DeliverySourceId FOR JSON PATH)) AS AR,
                        JSON_QUERY((SELECT i.* FROM dbo.DeliveryStockIssue i JOIN dbo.SaleDelivery d ON d.TenantId=i.TenantId AND d.Id=i.SaleDeliveryId
                            WHERE d.TenantId=@Tenant AND d.BusinessSourceId=@DeliverySourceId FOR JSON PATH)) AS COGS,
                        JSON_QUERY((SELECT * FROM dbo.AConfirmedLineSnapshot WHERE TenantId=@Tenant AND AOrderLineId=@LineId FOR JSON PATH)) AS Confirm,
                        JSON_QUERY((SELECT * FROM dbo.App05TripClosure WHERE TenantId=@Tenant AND TripId=@TripId FOR JSON PATH)) AS Closure
                    FOR JSON PATH, WITHOUT_ARRAY_WRAPPER)),2);
                """, scope, cancellationToken: ct));
        }

        async Task<bool> VerifyApprovalRace(Task exited)
        {
            if (!await Wait("approval-race-before", exited)) return false;
            var fixture = await Read<ApprovalRaceFixture>("approval-race-before");
            await using var db = await OpenDatabase(Primary);
            var before = await ReadOutcome();
            Check("competing actions start with one HELD and no approval or successor", Primary,
                new ApprovalRaceState(0, 0, 0, 1, before.Allocated, before.Physical, "CONFIRMED"), before);
            await Acknowledge("approval-race-before");
            if (!await Wait("approval-race-after", exited)) return false;
            var receipt = await Read<ApprovalRaceReceipt>("approval-race-after");
            var expected = receipt.Approved
                ? new ApprovalRaceState(2, 0, 1, 0, before.Allocated - 1, before.Physical, "REMAINDER_CLOSED")
                : new ApprovalRaceState(0, 2, 0, 1, before.Allocated, before.Physical, "CONFIRMED");
            Check("only one concurrent action commits and stock rights are conserved", Primary, expected, await ReadOutcome());
            await Acknowledge("approval-race-after");
            return true;

            Task<ApprovalRaceState> ReadOutcome() => db.QuerySingleAsync<ApprovalRaceState>(new CommandDefinition("""
                SELECT
                    (SELECT COALESCE(SUM(a.Quantity),0) FROM dbo.App05ARemainderApproval a
                        WHERE a.TenantId=s.TenantId AND a.OrderLineId=s.AOrderLineId) AS Approved,
                    (SELECT COALESCE(SUM(b.Quantity),0) FROM dbo.App05ASuccessor b
                        WHERE b.TenantId=s.TenantId AND b.OrderLineId=s.AOrderLineId AND b.DestinationTripId=@Destination) AS Scheduled,
                    r.ReleasedBaseQuantity AS Released,r.GrantedBaseQuantity-r.ConsumedBaseQuantity-r.ReleasedBaseQuantity AS Held,
                    p.AllocatedBaseQuantity AS Allocated,p.OnHandBaseQuantity AS Physical,o.Status
                FROM dbo.AConfirmedLineSnapshot s JOIN dbo.AOrder o ON o.TenantId=s.TenantId AND o.Id=s.AOrderId
                JOIN dbo.AReservation a ON a.TenantId=s.TenantId AND a.AOrderLineId=s.AOrderLineId
                JOIN dbo.StockRight r ON r.TenantId=a.TenantId AND r.Id=a.StockRightId
                JOIN dbo.StockPool p ON p.TenantId=r.TenantId AND p.StockLocationId=r.StockLocationId AND p.SkuId=r.SkuId
                WHERE s.TenantId=@Tenant AND s.TripId=@Origin AND s.AOrderLineId=@LineId;
                """, new { Tenant = Primary, fixture.Origin, fixture.Destination, fixture.LineId }, cancellationToken: ct));
        }

        async Task SetPool(PoolFault fault, decimal from, decimal to)
        {
            await using var db = await OpenDatabase(Primary);
            await db.ExecuteAsync(new CommandDefinition("""
                IF DB_NAME()<>@Name OR NOT EXISTS(SELECT 1 FROM dbo.LocalDemoOwner WHERE RunId=@Nonce)
                    OR NOT EXISTS(SELECT 1 FROM dbo.App00ScratchMarker WHERE Nonce=@Nonce)
                    OR NOT EXISTS(SELECT 1 FROM dbo.LocalApp03Session WHERE RunId=@Nonce AND TenantId=@Tenant)
                    THROW 51923,'APP-05.2 pool evidence scope mismatch.',1;
                UPDATE p SET AllocatedBaseQuantity=@To FROM dbo.StockPool p
                JOIN dbo.Sku sku ON sku.TenantId=p.TenantId AND sku.Id=p.SkuId AND sku.Code=N'DAY-A-EA'
                JOIN dbo.StockLocation w ON w.TenantId=p.TenantId AND w.Id=p.StockLocationId AND w.Code=N'C2-MAIN'
                WHERE p.TenantId=@Tenant AND p.AllocatedBaseQuantity=@From
                    AND EXISTS(SELECT 1 FROM dbo.StockRight r WHERE r.TenantId=p.TenantId AND r.TripId=@Trip
                        AND r.SkuId=p.SkuId AND r.StockLocationId=p.StockLocationId AND r.RightKind='A');
                IF @@ROWCOUNT<>1 THROW 51924,'APP-05.2 pool evidence expected one held pool.',1;
                """, new { Tenant = Primary, Name = run.Name(Primary), Nonce = run.Id, fault.Trip, From = from, To = to },
                cancellationToken: ct));
        }

        async Task Fault(Guid trip, Guid command, bool arm)
        {
            await using var db = await OpenDatabase(Primary);
            // All identifiers here are fixed. The trip and command are parsed GUIDs scoped to this nonce-owned DB.
            string statement = arm ? $"""
                CREATE TRIGGER dbo.TR_App05EvidenceFailClose ON dbo.App05TripClosure AFTER INSERT AS
                BEGIN
                    SET NOCOUNT ON;
                    IF EXISTS (SELECT 1 FROM inserted WHERE TenantId=N'tenant-a' AND TripId='{trip:D}' AND CommandId='{command:D}')
                        THROW 51912,'APP-05.2 synthetic precommit fault.',1;
                END;
                """ : "DISABLE TRIGGER dbo.TR_App05EvidenceFailClose ON dbo.App05TripClosure;";
            await db.ExecuteAsync(new CommandDefinition("""
                IF DB_NAME()<>@Name
                   OR NOT EXISTS(SELECT 1 FROM dbo.LocalDemoOwner WHERE RunId=@Nonce)
                   OR NOT EXISTS(SELECT 1 FROM dbo.App00ScratchMarker WHERE Nonce=@Nonce)
                   OR NOT EXISTS(SELECT 1 FROM dbo.LocalApp03Session WHERE RunId=@Nonce AND TenantId=@Tenant)
                   OR NOT EXISTS(SELECT 1 FROM dbo.Trip WHERE TenantId=@Tenant AND Id=@Trip)
                    THROW 51913,'APP-05.2 evidence fault scope mismatch.',1;
                EXEC(@Statement);
                """, new { Tenant = Primary, Name = run.Name(Primary), Nonce = run.Id, Trip = trip, Statement = statement },
                cancellationToken: ct));
        }

        async Task<string> EffectDigest(string tenant, bool includeHandoffSources = true, bool includeIntakeEvidence = true)
        {
            await using var db = await OpenDatabase(tenant);
            string sourceSql = includeHandoffSources ? """
                JSON_QUERY((SELECT * FROM dbo.BusinessSource WHERE TenantId=@tenant ORDER BY Id FOR JSON PATH)) AS Sources,
                JSON_QUERY((SELECT * FROM dbo.BusinessSourceLine WHERE TenantId=@tenant ORDER BY Id FOR JSON PATH)) AS SourceLines,
                JSON_QUERY((SELECT * FROM dbo.D3TripUnload WHERE TenantId=@tenant ORDER BY Id FOR JSON PATH)) AS Handoffs,
                """ : "";
            string intakeSql = includeIntakeEvidence ? "JSON_QUERY((SELECT * FROM dbo.C4DeliveryEvidence WHERE TenantId=@tenant ORDER BY Id FOR JSON PATH)) AS Evidence," : "";
            return await db.ExecuteScalarAsync<string>(new CommandDefinition($"""
                SELECT CONVERT(varchar(64),HASHBYTES('SHA2_256',(
                    SELECT
                        {sourceSql}
                        JSON_QUERY((SELECT * FROM dbo.LedgerEffect WHERE TenantId=@tenant ORDER BY Id FOR JSON PATH)) AS Effects,
                        JSON_QUERY((SELECT * FROM dbo.PhysicalStockMovement WHERE TenantId=@tenant ORDER BY Id FOR JSON PATH)) AS Movements,
                        JSON_QUERY((SELECT * FROM dbo.PhysicalStockBalance WHERE TenantId=@tenant
                            ORDER BY StockLocationId,SkuId FOR JSON PATH)) AS Balances,
                        JSON_QUERY((SELECT * FROM dbo.StockPool WHERE TenantId=@tenant
                            ORDER BY StockLocationId,SkuId FOR JSON PATH)) AS Pool,
                        JSON_QUERY((SELECT * FROM dbo.StockRight WHERE TenantId=@tenant ORDER BY Id FOR JSON PATH)) AS Rights,
                        JSON_QUERY((SELECT * FROM dbo.AConfirmedLineSnapshot WHERE TenantId=@tenant ORDER BY AOrderLineId FOR JSON PATH)) AS Confirms,
                        {intakeSql}
                        JSON_QUERY((SELECT * FROM dbo.SaleDelivery WHERE TenantId=@tenant ORDER BY Id FOR JSON PATH)) AS Deliveries,
                        JSON_QUERY((SELECT * FROM dbo.ReceivableOpenItem WHERE TenantId=@tenant ORDER BY Id FOR JSON PATH)) AS Ar,
                        JSON_QUERY((SELECT * FROM dbo.CashReceipt WHERE TenantId=@tenant ORDER BY Id FOR JSON PATH)) AS Receipts,
                        JSON_QUERY((SELECT * FROM dbo.CashHandoverOffer WHERE TenantId=@tenant ORDER BY Id FOR JSON PATH)) AS Offers,
                        JSON_QUERY((SELECT * FROM dbo.CashHandoverAcceptance WHERE TenantId=@tenant ORDER BY Id FOR JSON PATH)) AS Acceptances,
                        JSON_QUERY((SELECT * FROM dbo.DeliveryStockIssue WHERE TenantId=@tenant ORDER BY Id FOR JSON PATH)) AS Cogs
                    FOR JSON PATH, WITHOUT_ARRAY_WRAPPER)),2);
                """, new { tenant }, cancellationToken: ct)) ?? throw new InvalidOperationException("Missing business digest.");
        }

        async Task<SqlConnection> OpenDatabase(string tenant)
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

        void Check<T>(string metric, string tenant, T expected, T actual)
        {
            bool equal = EqualityComparer<T>.Default.Equals(expected, actual);
            checks.Add(new(metric, tenant, expected!, actual!, equal ? null : "Values differ"));
            if (!equal) throw new InvalidOperationException($"{metric} for {tenant}: expected {expected}, actual {actual}.");
        }
    }

    private sealed record ApprovalRaceFixture(Guid Origin, Guid Destination, Guid LineId);
    private sealed record ApprovalRaceReceipt(bool Approved);
    private sealed record ApprovalRaceState(decimal Approved, decimal Scheduled, decimal Released, decimal Held,
        decimal Allocated, decimal Physical, string Status);
    private sealed record ApprovalBefore(Guid TripId, Guid LineId, Guid DeliverySourceId);
    private sealed record ApprovalAfter(Guid FirstSourceId, Guid FractionalSourceId, Guid FinalSourceId);
    private sealed record ApprovedRight(Guid SourceId, decimal Quantity, decimal Released, decimal NeverLoaded, decimal Returned,
        decimal LotNeverLoaded, decimal LotReturned);
    private sealed record Source(Guid SourceId);
    private sealed record Command(Guid CommandId, string ExpectedRevision);
    private sealed record Order(Guid OrderId, Guid OrderLineId, Guid ConfirmSourceId);
    private sealed record AccountingWork(Guid SourceId, Guid RelatedId);
    private sealed record PoolFault(Guid Trip, decimal Original, decimal Fault);
    private sealed record ArmFailure(Guid Main, Guid Clean, Command Failure, Order Order, Source Unload, Source CleanUnload,
        AccountingWork AccountingWork);
    private sealed record Receipt(Guid TripId, Guid ClosureId, Guid CommandId, DateTimeOffset ClosedAt, Guid HandoffId, JsonElement Snapshot);
    private sealed record OpenReceipt(Receipt Receipt, Source Delivery);
    private sealed record CleanCheckpoint(Receipt CleanReceipt);
    private sealed record ZeroCheckpoint(Receipt ZeroReceipt);
    private sealed record ReportCheckpoint(Guid ReportId);
    private sealed record Persisted(Guid ClosureId, string ActorId, Guid CommandId, string Status,
        DateTimeOffset ClosedAt, int OpenWorkCount, Guid HandoffId, string Snapshot);
    private sealed record TripImageRow(string Status, int Decisions);
    private sealed record ReserveBefore(Guid WarehouseId, Guid SkuId, Guid MixedLine, Guid UnreservedLine, Guid HeldLine,
        Guid MixedTrip, Guid FirstTrip, Guid SecondTrip);
    private sealed record ReserveLotBalance(Guid LotId, string Code, DateTime ExpiresOn, Guid SkuId, Guid WarehouseId,
        decimal Opening, decimal Held);
    private sealed record ReserveLotPick(Guid OrderLineId, Guid LotId, string Code, DateTime ExpiresOn, Guid SkuId,
        Guid WarehouseId, decimal Quantity);
    private sealed record ReserveRace(bool WinnerA, Guid SharedSkuId, Guid SharedLine);
    private sealed record ReserveGrant(Guid OrderLineId, int Rights, decimal Quantity, decimal LotQuantity, decimal LedgerQuantity);
    private sealed record ReservePool(decimal OnHand, decimal Allocated);
    private sealed record RemainderProposalProof(Guid ProposalId, Guid LineId, Guid ConfirmSourceId, Guid CommandId,
        Guid FollowUpId, string Reason, string Revision);
    private sealed record ApprovalTransferRace(Guid LineId, Guid RightId);
    private sealed record SuccessorHeldDelivery(Guid MainLineId, Guid MixedLineId, Guid SuccessorTripId,
        Guid MainBindingId, Guid MixedBindingId, Guid MixedRightId, Guid[] ApprovalCommandIds);
    private sealed record SuccessorReserveDelivery(Guid OrderId, Guid OrderLineId, Guid ConfirmSourceId, Guid OriginTripId,
        Guid DestinationTripId, Guid BindingId, Guid RightId, Guid ManifestLineId, Guid DeliverySourceId, Guid LoadSourceId);
    private sealed record SuccessorStaleDelivery(Guid TripId, Guid CommandId);
    private sealed record SuccessorFixture(Order Order, Order Held, Order Unreserved);
    private sealed record SuccessorFault(Guid CommandId);
    private sealed record SuccessorRevocation(Guid TripId, Guid CommandId);
    private sealed record SuccessorCommitted(Guid SuccessorRightId, decimal Quantity, string PayloadHash, string Receipt);
    private sealed record SuccessorRace(decimal Winner);
    private sealed record SuccessorOracle(Guid OrderLineId, decimal Scheduled, decimal Returned,
        decimal NeverLoaded, decimal LotQuantity, int Rights);
    private sealed record ZeroFixture(Guid Zero, Order Held, Order Unreserved, decimal HeldQuantity, Order? Shared);
    private sealed record ZeroProof(Guid OrderLineId, Guid ConfirmSourceId, decimal HeldBaseQuantity);
    private sealed record ZeroState(int Manifests, int Handoffs, int ReturnLines, decimal Held, int UnreservedRights);
    private sealed record OpenInventory(string Status, decimal MainStock, decimal MainCost, decimal Delivered, decimal Cogs,
        decimal Ar, decimal Returned, decimal Held);
    private sealed record Comparison(string Metric, string Tenant, object Expected, object Actual, string? Diff);
    private sealed record Artifact(string File, string Sha256);
}
