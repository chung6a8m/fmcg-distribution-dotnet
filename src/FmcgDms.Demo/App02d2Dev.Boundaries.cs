using System.Net;
using System.Net.Http.Json;
using Dapper;

namespace FmcgDms.Demo;

internal sealed partial class App02d2Dev
{
    private async Task RollbackCase(string tenant)
    {
        foreach (string table in new[] { "DeliveryStockIssue", "ReceiptAllocation" })
        {
            var s = await d1.SeedD2(tenant, "D2-13-" + table);
            var input = Request(s, await d1.SeedD2Cash(s, 60), 60);
            string before = await D2Sql.Fingerprint(run, tenant);
            var opening = await D2Sql.Read(run, s);
            await using var db = await Owner(tenant);
            bool early = table == "DeliveryStockIssue";
            string filter = early ? "SaleDeliveryId" : "ReceivableOpenItemId";
            string link = early ? "d.Id" : "a.Id";
            string prerequisites = early
                ? "EXISTS (SELECT 1 FROM dbo.PhysicalStockBalance WHERE TenantId=d.TenantId AND StockLocationId=t.VehicleLocationId AND SkuId=d.SkuId AND OnHandBaseQuantity=60 AND OnHandCostAmount=600)"
                : "EXISTS (SELECT 1 FROM dbo.CashReceipt r WHERE r.TenantId=d.TenantId AND r.SaleDeliveryId=d.Id AND r.ReceivedAmount=60) AND EXISTS (SELECT 1 FROM dbo.DeliveryStockIssue si WHERE si.TenantId=d.TenantId AND si.SaleDeliveryId=d.Id)";
            string join = early ? "" : "JOIN dbo.ReceivableOpenItem a ON a.TenantId=d.TenantId AND a.SaleDeliveryId=d.Id";
            // Constants come from this nonce-owned fixture, never from a request.
            await db.ExecuteAsync($"""
                CREATE TRIGGER dbo.D2FixtureTransactionFault ON dbo.{table} AFTER INSERT AS
                BEGIN
                    SET NOCOUNT ON;
                    IF NOT EXISTS (
                        SELECT 1 FROM inserted i JOIN dbo.SaleDelivery d ON d.TenantId=i.TenantId
                        JOIN dbo.Trip t ON t.TenantId=d.TenantId AND t.Id=d.TripId
                        {join}
                        WHERE i.{filter}={link} AND d.TripId='{s.Trip:D}') RETURN;
                    IF NOT EXISTS (
                        SELECT 1 FROM inserted i JOIN dbo.SaleDelivery d ON d.TenantId=i.TenantId
                        JOIN dbo.Trip t ON t.TenantId=d.TenantId AND t.Id=d.TripId
                        {join}
                        WHERE i.{filter}={link} AND d.TripId='{s.Trip:D}' AND ({prerequisites}))
                        THROW 51903,'D.2 fault ordering prerequisite missing',1;
                    THROW {(early ? 51901 : 51902)},'D.2 synthetic transaction fault',1;
                END
                """);
            try
            {
                var failure = await Send(tenant, HttpMethod.Post, Sales, input);
                Verify(failure.Http, 500, $"Injected fault when writing {table}");
                Verify(await D2Sql.Fingerprint(run, tenant), before, $"{table} failure rolls back all protected SQL");
                Verify(await D2Sql.CommandRows(run, s, input.ProducerId, input.CommandId), 0,
                    $"{table} failure rolls back the command row");
            }
            finally
            {
                await run.Verify(tenant);
                await db.ExecuteAsync("DROP TRIGGER dbo.D2FixtureTransactionFault");
            }
            Guid posted = Applied(await Send(tenant, HttpMethod.Post, Sales, input), "Same command succeeds after fault removed");
            await VerifyPosting(s, input, posted, opening, expected["D2-13"]);
        }
    }

    private async Task RecoveryCase(string tenant)
    {
        var s = await d1.SeedD2(tenant, "D2-14");
        var input = Request(s, await d1.SeedD2Cash(s, 60), 60);
        var opening = await D2Sql.Read(run, s);
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0));
        await using var proxy = builder.Build();
        var upstream = new TaskCompletionSource<Reply>(TaskCreationOptions.RunContinuationsAsynchronously);
        proxy.MapPost("/drop-ack", async context =>
        {
            try { upstream.TrySetResult(await Send(tenant, HttpMethod.Post, Sales, input)); }
            catch (Exception ex) { upstream.TrySetException(ex); }
            finally { context.Abort(); }
        });
        await proxy.StartAsync(ct);
        bool lost = false;
        try { using var ignored = await client.PostAsJsonAsync(proxy.Urls.Single() + "/drop-ack", new { }, ct); }
        catch (HttpRequestException) { lost = true; }
        Verify(lost, true, "Caller did not receive ACK");
        Guid sourceId = Applied(await upstream.Task.WaitAsync(ct), "Actual API committed before ACK was dropped");
        await restart("on");
        var lookup = await Send(tenant, HttpMethod.Get, Lookup(input));
        Verify(lookup.Source, (Guid?)sourceId, "Original source found after API restart");
        Verify(lookup.Status, "APPLIED", "Recovered original terminal outcome");
        var retry = await Send(tenant, HttpMethod.Post, Sales, input);
        Verify(retry.Replayed, true, "Identical authorized retry is replayed");
        Verify(retry.Source, (Guid?)sourceId, "Replay returns original source");
        var conflict = await NoEffect(tenant, input with { BaseQuantity = 9 }, "Changed payload with same identity");
        Verify(conflict.Http, 409, "Changed payload reports conflict");
        await NoEffect(tenant, input with { CommandId = Key() }, "Same source with new identity");
        await VerifyPosting(s, input, sourceId, opening, expected["D2-14"]);
    }

    private async Task ChannelCase(string tenant)
    {
        var s = await d1.SeedD2(tenant, "D2-15");
        var input = Request(s, await d1.SeedD2Cash(s, 60), 60);
        await restart("off");
        try
        {
            var blocked = await NoEffect(tenant, input, "Missing synthetic channel profile");
            Verify(blocked.Reason, "B_NO_CHANNEL", "Missing channel is rejected explicitly");
        }
        finally { await restart("on"); }
        var altered = System.Text.Json.JsonSerializer.SerializeToNode(input, Json)!.AsObject();
        altered["invoiceEligible"] = true;
        var opening = await D2Sql.Read(run, s);
        var reply = await Send(tenant, HttpMethod.Post, Sales, altered);
        Guid sourceId;
        if (reply.Applied) sourceId = reply.Source!.Value;
        else
        {
            Verify(reply.Denied, true, "Client cannot turn on invoice eligibility");
            Verify(await D2Sql.Read(run, s), opening, "Invalid eligibility leaves no posting");
            sourceId = Applied(await Send(tenant, HttpMethod.Post, Sales, input), "Valid synthetic sale");
        }
        await VerifyPosting(s, input, sourceId, opening, expected["D2-02"]);
        Verify((await D2Sql.Read(run, s)).Outbox, opening.Outbox, "No invoice or accounting outbound effect");
    }

    private async Task PeerCase(string tenant, string peer)
    {
        await using var diagnostic = await D2PeerWitness.TryStart(root, this.evidence, source, run, tenant, witnessPids, ct);
        peerWitness = diagnostic;
        d1.D2PeerDiagnostic = diagnostic;
        try
        {
            diagnostic?.SelectPhase("origin-seed");
            var own = await d1.SeedD2(tenant, $"D2-16-OUT-{Slot(tenant)}");
            diagnostic?.SelectPhase("peer-seed");
            var foreign = await d1.SeedD2(peer, $"D2-16-IN-{Slot(tenant)}");
            var input = Request(own, await d1.SeedD2Cash(own, 60), 60);
            var peerRequest = Request(foreign, await d1.SeedD2Cash(foreign, 60), 60);
            string ownBefore = await D2Sql.Fingerprint(run, tenant);
            string peerBefore = await D2Sql.Fingerprint(run, peer);
            diagnostic?.SelectPhase("peer-checks");
            foreach (var variant in new[]
            {
                peerRequest with { ProducerId = Actor(tenant) },
                Fresh(input) with { TripId = foreign.Trip },
                Fresh(input) with { CustomerId = foreign.Customer, OutletId = foreign.Outlet },
                Fresh(input) with { ShipToSnapshotId = foreign.ShipTo, SelectionId = foreign.Selection },
                Fresh(input) with { TripManifestLineId = foreign.B20 },
                Fresh(input) with { VerificationEvidenceRef = peerRequest.VerificationEvidenceRef }
            })
                await NoEffect(tenant, variant, "Peer identifiers denied without D.2 source");
            var prior = browserScenes[(peer, "D2-02")];
            var evidenceFile = Path.Combine(Checkpoints, $"{Slot(peer)}-D2-02.json");
            var evidence = System.Text.Json.JsonSerializer.Deserialize<BrowserResult>(await File.ReadAllTextAsync(evidenceFile, ct), Json)!;
            var lookup = await Send(tenant, HttpMethod.Get, Lookup(prior.Request with
            {
                CommandId = evidence.CommandId, ProducerId = evidence.ProducerId
            }));
            Verify(lookup.Http is 403 or 404, true, "Peer terminal outcome inaccessible");
            Verify(lookup.Source, (Guid?)null, "Peer outcome has no source disclosure");
            Verify(await D2Sql.Fingerprint(run, tenant), ownBefore, "Origin tenant protected rows unchanged");
            Verify(await D2Sql.Fingerprint(run, peer), peerBefore, "Peer tenant protected rows unchanged");
        }
        finally { peerWitness = null; d1.D2PeerDiagnostic = null; }
    }
}
