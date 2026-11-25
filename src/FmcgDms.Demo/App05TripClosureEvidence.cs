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
            }
            Check("contrast tenant business rows preserved across three synthetic days", Peer, originalPeer, await EffectDigest(Peer));
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
            }, tripClosure: true, successor: upgrade);
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
            await VerifyReceipt(zeroReceipt.ZeroReceipt, "CLOSED_WITH_OPEN_CASES", 2, zeroReceipt.ZeroReceipt.HandoffId);
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
            Check("zero handoff references exactly two source Confirm lines", Primary, 2, proofs.Count);
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
            return true;
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
    private sealed record SuccessorFixture(Order Order, Order Held, Order Unreserved);
    private sealed record SuccessorFault(Guid CommandId);
    private sealed record SuccessorRevocation(Guid TripId, Guid CommandId);
    private sealed record SuccessorCommitted(Guid SuccessorRightId, decimal Quantity, string PayloadHash, string Receipt);
    private sealed record SuccessorRace(decimal Winner);
    private sealed record SuccessorOracle(Guid OrderLineId, decimal Scheduled, decimal Returned,
        decimal NeverLoaded, decimal LotQuantity, int Rights);
    private sealed record ZeroFixture(Guid Zero, Order Held, Order Unreserved, decimal HeldQuantity);
    private sealed record ZeroProof(Guid OrderLineId, Guid ConfirmSourceId, decimal HeldBaseQuantity);
    private sealed record ZeroState(int Manifests, int Handoffs, int ReturnLines, decimal Held, int UnreservedRights);
    private sealed record OpenInventory(string Status, decimal MainStock, decimal MainCost, decimal Delivered, decimal Cogs,
        decimal Ar, decimal Returned, decimal Held);
    private sealed record Comparison(string Metric, string Tenant, object Expected, object Actual, string? Diff);
    private sealed record Artifact(string File, string Sha256);
}
