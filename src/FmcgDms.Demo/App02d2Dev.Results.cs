using System.Xml.Linq;
using Dapper;
using FmcgDms.Api.Persistence;

namespace FmcgDms.Demo;

internal sealed partial class App02d2Dev
{
    internal async Task Complete(int browserExit)
    {
        string junit = Path.Combine(root, "web", "test-results-app02d2", run.Id, "junit.xml");
        try
        {
            foreach (string tenant in DemoRun.Tenants)
            {
                string slot = Slot(tenant);
                await Record("D2-01", slot, () => UpgradeCase(tenant));
                foreach (string id in new[] { "D2-02", "D2-03", "D2-04", "D2-05", "D2-06", "D2-17" })
                    await Record(id, slot, () => BrowserCase(tenant, id, browserExit));
                await Record("D2-07", slot, () => EvidenceCase(tenant));
                await Record("D2-08", slot, () => IdentityCase(tenant));
                await Record("D2-09", slot, () => StockCase(tenant));
                await Record("D2-10", slot, () => GrantCase(tenant));
                await Record("D2-11", slot, () => RaceCase(tenant, false));
                await Record("D2-12", slot, () => RaceCase(tenant, true));
                await Record("D2-13", slot, () => RollbackCase(tenant));
                await Record("D2-14", slot, () => RecoveryCase(tenant));
                await Record("D2-15", slot, () => ChannelCase(tenant));
                string other = tenant == "tenant-a" ? "tenant-b" : "tenant-a";
                await Record("D2-16", $"{slot}-to-{Slot(other)}", () => PeerCase(tenant, other));
                await Record("D2-18", slot, () => OldBalancesCase(tenant));
            }
        }
        finally
        {
            string report = Path.Combine(evidence, "sql-review.json");
            await Save(report, new
            {
                source, apiSha256 = apiSha, runId = run.Id, physical = "NOT_RUN", host = Environment.MachineName,
                instance = DemoRun.Instance, contractSha256 = ContractSha, matrixSha256 = MatrixSha,
                fixtureDefinitionSha256 = FixtureSha, expectedSha256 = ExpectedSha,
                fixtureSha256 = FileHash(Path.Combine(evidence, "fixture.json")),
                browserReportSha256 = File.Exists(junit) ? FileHash(junit) : null,
                checkpoints = Directory.GetFiles(Checkpoints, "*.json").Order().Select(p => new { file = Path.GetFileName(p), sha256 = FileHash(p) }),
                cases = results.OrderBy(x => x.CaseId).ThenBy(x => x.Scope), http = observations
            });
            Console.WriteLine($"APP-02D.2 SQL comparison: {report}");
        }
        if (results.Count != 36 || results.Any(x => x.Status != "PASS") || browserExit != 0)
            throw new InvalidOperationException("D.2 has incomplete or failed case evidence.");
        var tests = XDocument.Load(junit).Descendants("testcase").ToArray();
        if (tests.Length < 12 || tests.Any(x => x.Element("failure") is not null || x.Element("skipped") is not null || x.Element("error") is not null))
            throw new InvalidOperationException("D.2 browser evidence has a missing or failed scenario.");
    }

    private static string FileHash(string path) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));

    private async Task BrowserCase(string tenant, string id, int browserExit)
    {
        var fixture = browserScenes[(tenant, id)];
        string path = Path.Combine(Checkpoints, $"{Slot(tenant)}-{id}.json");
        Verify(File.Exists(path), true, "Browser result exists for case and tenant");
        var outcome = System.Text.Json.JsonSerializer.Deserialize<BrowserResult>(await File.ReadAllTextAsync(path, ct), Json)!;
        Verify(outcome.HttpStatus, 200, "Browser actual HTTP outcome");
        Verify(outcome.Status, "APPLIED", "Browser actual durable outcome");
        Verify((Guid?)outcome.SourceId, await D2Sql.CommandSource(run, fixture.Scene, outcome.ProducerId, outcome.CommandId),
            "Browser command maps to exactly the same SQL source");
        if (id == "D2-17")
        {
            Verify(outcome.OfflinePostCount, 0, "No offline business POST");
            Verify(outcome.QueuedAfterOffline, false, "No offline business queue");
            foreach (int status in new[] { outcome.RevokedLookupStatus, outcome.RevokedReplayStatus, outcome.RevokedSubmitStatus })
                Verify(status is 403 or 404, true, "Revoked authority denied");
        }
        var opening = fixture.Opening;
        Verify(opening.Main, 30m, "Frozen opening MAIN quantity");
        Verify(opening.MainValue, 300m, "Frozen opening MAIN value");
        Verify(opening.Truck, id == "D2-05" ? 60m : 70m, "Frozen opening TRIP quantity");
        Verify(opening.TruckValue, id == "D2-05" ? 600m : 700m, "Frozen opening TRIP value");
        Verify(opening.A, 40m, "Frozen opening A right");
        Verify(opening.B, id == "D2-05" ? 20m : 30m, "Frozen opening B right");
        Verify(opening.CreditRemaining, 100m, "Frozen opening grant");
        Verify(opening.CustomerCredit, id == "D2-05" ? 20m : 0m, "Frozen opening CustomerCredit");
        Verify(opening.Custody, id == "D2-05" ? 120m : 0m, "Frozen opening custody");
        Verify(opening.ArNet, 0m, "Frozen opening AR");
        await VerifyPosting(fixture.Scene, fixture.Request, outcome.SourceId, opening, expected[id]);
        Verify(browserExit, 0, "Playwright process exit code");
    }

    private async Task VerifyPosting(D2Scene s, D2Request request, Guid sourceId, D2Snapshot before, Expected expectedValue)
    {
        var actual = await D2Sql.Read(run, s);
        decimal qty = expectedValue.Qty, payable = qty * 10, ar = expectedValue.Ar;
        decimal cash = expectedValue.Cash, cashApplied = expectedValue.CashApplied, credit = expectedValue.Credit;
        decimal creditApplied = Math.Max(0, -credit), creditIssued = Math.Max(0, credit);
        var expectedSnapshot = before with
        {
            Truck = before.Truck - qty, TruckValue = before.TruckValue - payable,
            B = before.B - qty,
            B10Remaining = before.B10Remaining - (request.TripManifestLineId == s.B10 ? qty : 0),
            B20Remaining = before.B20Remaining - (request.TripManifestLineId == s.B20 ? qty : 0),
            CreditConsumed = before.CreditConsumed + ar,
            ArGross = before.ArGross + payable, ArApplied = before.ArApplied + cashApplied + creditApplied,
            ArNet = before.ArNet + ar, Cogs = before.Cogs + payable, Cash = before.Cash + cash,
            CashApplied = before.CashApplied + cashApplied, Custody = before.Custody + cash,
            CustomerCredit = before.CustomerCredit + credit,
            Sales = before.Sales + 1, Issues = before.Issues + 1, OpenItems = before.OpenItems + 1,
            Receipts = before.Receipts + (cash > 0 ? 1 : 0),
            Allocations = before.Allocations + (cashApplied > 0 ? 1 : 0),
            GrantUses = before.GrantUses + (ar > 0 ? 1 : 0)
        };
        Verify(actual, expectedSnapshot, "Whole trip actual SQL versus independent arithmetic");
        var posting = await D2Sql.Source(run, s, sourceId);
        var row = posting.Row;
        Verify(row.Source, sourceId, "Source identity");
        Verify(row.Customer, request.CustomerId, "Customer remains debtor");
        Verify(row.ShipTo, request.ShipToSnapshotId, "Immutable ShipTo snapshot");
        Verify(row.Selection, (Guid?)request.SelectionId, "Source-bound ShipTo selection");
        Verify(row.Outlet, request.OutletId, "Selection outlet");
        Verify(row.ManifestLine, request.TripManifestLineId, "Exact D.1 source manifest line");
        var origin = await D2Sql.LoadOrigin(run, s, request.TripManifestLineId);
        Verify(row.LoadSourceLine, origin.SourceLine, "Exact immutable D.1 load source line");
        Verify(origin.D1Writer, true, "Load provenance belongs to guarded D.1 writer");
        decimal loadQuantity = request.TripManifestLineId == s.B10 ? 10 : 20;
        Verify(origin.Quantity, loadQuantity, "Loaded quantity on the exact manifest line");
        Verify(origin.Cost, loadQuantity * 10, "Original carrying value on the exact manifest line");
        Verify(origin.MainOutQuantity, -loadQuantity, "Original MAIN OUT quantity");
        Verify(origin.MainOutValue, -loadQuantity * 10, "Original MAIN OUT value");
        Verify(origin.TruckInQuantity, loadQuantity, "Original TRIP IN quantity");
        Verify(origin.TruckInValue, loadQuantity * 10, "Original TRIP IN value");
        Verify(row.Right, s.BRight, "Exact loaded B right");
        Verify(row.Sku, request.SkuId, "Exact SKU");
        Verify(row.Quantity, qty, "Posted quantity");
        Verify(row.Payable, payable, "Server payable");
        Verify(row.Cost, payable, "Posted cost");
        Verify(row.IssueQuantity, qty, "Issue quantity");
        Verify(row.IssueCost, payable, "Issue cost");
        Verify(row.ArGross, payable, "Source AR gross");
        Verify(row.ArApplied, cashApplied + creditApplied, "Source AR applied");
        Verify(row.ArNet, ar, "Source AR net");
        Verify(row.Cash, cash, "Cash actually verified");
        Verify(row.CashEvidence, cash > 0 ? request.VerificationEvidenceRef : null, "Exact committed cash witness");
        Verify(row.CashApplied, cashApplied, "Cash applied");
        Verify(row.Allocation, cashApplied, "Source-bound cash allocation");
        Verify(row.CreditApplied, creditApplied, "Reserved customer credit applied");
        Verify(row.CreditIssued, creditIssued, "Receipt surplus issued");
        Verify(row.GrantConsumed, ar, "B grant consumption matches residual AR");
        Verify(row.GrantUses, ar > 0 ? 1 : 0, "Grant consumption row cardinality");
        Verify(row.Grant, ar > 0 ? s.Grant : null, "Grant proof source id");
        Verify(row.ApprovedPolicy, ar > 0 ? s.Approval : null, "Source approval id");
        Verify(row.PendingBank, request.PendingBankEvidenceRef, "Pending bank evidence lineage");
        Verify(row.BankReported, request.PendingBankEvidenceRef is null ? 0m : qty == 8 ? 80m : 60m, "Bank reported amount remains evidence only");
        Verify(row.Reservation, request.CustomerCreditReservationId, "CustomerCredit reservation lineage");
        Verify(row.CreditOrigin.HasValue, creditApplied > 0, "Applied credit references the verified original receipt");
        Verify(row.InvoiceEligible, false, "Synthetic sale cannot be invoiced");

        static string Format(string key, string kind, decimal quantity, decimal money) =>
            FormattableString.Invariant($"{key}|{kind}|{quantity:0.0000}|{money:0.0000}");
        var effects = new List<string>
        {
            Format("PHYSICAL-OUT", "PHYSICAL_STOCK", -qty, -payable),
            Format("COGS", "COGS", 0, payable),
            Format("AR-GROSS", "AR", 0, payable),
            Format("RIGHT-B-USED", "RIGHT_B_CONSUMED", -qty, 0)
        };
        if (cash > 0)
        {
            effects.Add(Format("AR-CASH-ALLOC", "AR", 0, -cashApplied));
            effects.Add(Format("CASH-CUSTODY", "CUSTODY", 0, cash));
        }
        if (creditApplied > 0)
        {
            effects.Add(Format("AR-CREDIT-ALLOC", "AR", 0, -creditApplied));
            effects.Add(Format("CUSTOMER-CREDIT-USED", "CUSTOMER_CREDIT", 0, -creditApplied));
        }
        if (creditIssued > 0) effects.Add(Format("CUSTOMER-CREDIT-ISSUED", "CUSTOMER_CREDIT", 0, creditIssued));
        if (ar > 0) effects.Add(Format("CREDIT-B-USED", "RIGHT_CREDIT_B_CONSUMED", 0, -ar));
        Verify(string.Join(",", posting.Ledger.Select(x => Format(x.Key, x.Kind, x.Quantity, x.Amount)).Order()),
            string.Join(",", effects.Order()), "Exact signed source ledger effects");
        var physical = posting.Ledger.Single(x => x.Key == "PHYSICAL-OUT");
        Verify(physical.Location, (Guid?)s.Truck, "Only vehicle stock is issued");
    }

    private async Task UpgradeCase(string tenant)
    {
        var u = upgrades[tenant];
        d1.VerifyD1History(tenant);
        Verify(u.BeforeVersion, 202611220007L, "Exact pre-D2 migration boundary");
        Verify(u.AfterVersion, 202611220008L, "D2 migration applied");
        Verify(u.After, u.Before, "Legacy, C.3 and D.1 load fingerprints preserved");
        string before = await D2Sql.Fingerprint(run, tenant);
        ScratchMigrator.Migrate(run.Connection(tenant), tenant, run.Id, LogLevel.Warning, true);
        Verify(await D2Sql.Fingerprint(run, tenant), before, "Migration reapplication preserves protected rows");
        await using var db = await App02c6Dev.ReadOnly(run, tenant);
        Verify(await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.VersionInfo WHERE Version=202611220008"), 1,
            "Single migration history row");
        foreach (var mutate in new Action<Microsoft.Data.SqlClient.SqlConnectionStringBuilder>[]
        {
            x => x.DataSource = "refused-non-localdb", x => x.InitialCatalog = "master",
            x => x.InitialCatalog = $"Fmcg_App00_{run.Id}_{(Slot(tenant) == "A" ? "B" : "A")}",
            x => x.InitialCatalog = $"Fmcg_App00_{Key()}_{Slot(tenant)}"
        })
        {
            var foreign = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(run.Connection(tenant));
            mutate(foreign);
            bool refused = false;
            try { ScratchMigrator.Migrate(foreign.ConnectionString, tenant, run.Id, LogLevel.Warning, true); }
            catch (InvalidOperationException) { refused = true; }
            Verify(refused, true, "Unsafe instance, name, nonce or tenant refused");
        }
        var modern = u.Scene;
        await restart("legacy");
        try
        {
            var legacy = new FmcgDms.Api.Persistence.PostBSaleDeliveryReceiptCommand(Actor(tenant), Key(), Key(),
                modern.Trip, modern.Customer, modern.ShipTo, modern.B20, modern.Sku, 1, 10, 10, "legacy-fence");
            var reply = await Send(tenant, HttpMethod.Post, "/api/synthetic/app01c/commands/post-b-sale-delivery-receipt", legacy);
            Verify(reply.Reason, "B_LOADED_RIGHT_NOT_FOUND", "B20 rejects D.1 manifest source");
        }
        finally { await restart("on"); }
        var legacyLine = await db.QueryFirstAsync<Guid>("SELECT Id FROM dbo.TripManifestLine WHERE IsC3=0 AND RightKind='B'");
        var attempted = Request(modern, await d1.SeedD2Cash(modern, 60), 60) with { TripManifestLineId = legacyLine };
        await NoEffect(tenant, attempted, "D.2 cannot use legacy manifest");
    }
}
