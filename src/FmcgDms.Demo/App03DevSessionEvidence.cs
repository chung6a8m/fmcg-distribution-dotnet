using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Dapper;
using FmcgDms.Api.Persistence;
using Microsoft.Data.SqlClient;

namespace FmcgDms.Demo;

internal static class App03DevSessionEvidence
{
    private const string C1 = "/api/synthetic/app02c1";
    private const string C2 = "/api/synthetic/app02c2";
    private const string C3 = "/api/synthetic/app02c3";
    private const string C4 = "/api/synthetic/app02c4";
    private const string Tenant = "tenant-a";
    private static readonly string[] Tables =
    [
        "AOrder", "AOrderLine", "AConfirmedLineSnapshot", "TripManifestLine", "SaleDelivery",
        "ReceivableOpenItem", "PhysicalStockOpening", "PhysicalStockBalance", "PhysicalStockMovement",
        "C2CommercialPolicy", "F04PricePlan", "F04DeliveryPricing", "BusinessCommand", "BusinessSource",
        "LedgerEffect", "C1Command", "App02ActorRoleGrant"
    ];

    internal static async Task Run(string root, string source)
    {
        var run = new DemoRun(Guid.NewGuid().ToString("N"));
        var foreign = new DemoRun(Guid.NewGuid().ToString("N"));
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var ct = deadline.Token;
        Task? hostTask = null;
        var expectedAr = 20000m;
        var expectedCost = 6667m;
        bool foreignCreated = false;
        bool removed = false;
        try
        {
            var active = await Open("Start");
            string webOrigin = active.WebUrl;
            await Reject(() => RunOperation("Remove"), "in use");
            var catalog = await Read<C2Catalog>(active, C2 + "/catalog");
            var customer = catalog.Customers.Single(row => row.Label.StartsWith("C2-FIXTURE", StringComparison.Ordinal));
            var trip = catalog.Trips.Single();
            var sku = catalog.Skus.Single(row => row.Label.StartsWith("R-05 ·", StringComparison.Ordinal));
            var warehouse = catalog.Warehouses.Single(row => row.Label == "C2-MAIN");
            var selection = await Post(C1 + "/ship-to-selections", new C1SelectionInput(Guid.NewGuid(),
                customer.Id, null, null, null, new C1Address("VN", "HCM", "Khu DEV", "APP-03.1b", "Nhận DEV")));
            var draft = await Post(C2 + "/drafts", new C2DraftInput(Guid.NewGuid(), customer.Id, trip.Id,
                selection.GetProperty("id").GetGuid(), [new(sku.Id, 2)]));
            Guid orderId = draft.GetProperty("sourceId").GetGuid();
            await Post(C2 + "/confirm", new C2ConfirmInput(Guid.NewGuid(), orderId));
            var confirmed = (await Read<C2OrderView[]>(active, C2 + "/orders")).Single();
            await Post(C2 + "/reserve", new C2ReserveInput(Guid.NewGuid(), orderId, confirmed.Lines.Single().LineId!.Value,
                warehouse.Id, 2, confirmed.ConfirmSourceId!.Value));
            var right = (await Read<IdView[]>(active, C3 + $"/trips/{trip.Id}/rights")).Single();
            await Post(C3 + "/load", new C3LoadInput(Guid.NewGuid(), trip.Id, [new(right.Id, 2, 2)]));
            var line = (await Read<DeliveryLine[]>(active, C4 + $"/trips/{trip.Id}/lines")).Single();
            var delivery = new C4DeliveryInput(Guid.NewGuid(), trip.Id, line.Id, line.ShipToVersion, 2);

            // The transport observes completion, then discards the response before the caller learns the source ID.
            using (var droppedAck = await Send(active, C4 + "/deliver", delivery, ct))
                Ensure(droppedAck.StatusCode == HttpStatusCode.OK, "Delivery did not commit at the lost ACK checkpoint.");

            await using var db = new SqlConnection(run.Connection(Tenant));
            var before = await ReadDelivery(db);
            Ensure(before.OrderId == orderId && before.Outstanding == expectedAr && before.Cost == expectedCost
                && before.Quantity == 2 && before.Lot == "RET-ORIGIN", "Unexpected original sale/AR/lot.");
            var originalToken = active.SourceToken;
            var snapshot = await DevF01Evidence.Capture(run, Tables);
            using (var api = Process.GetProcessById(active.ApiPid))
            using (var vite = Process.GetProcessById(active.VitePid))
            {
                var isolated = Directory.CreateTempSubdirectory("fmcg-app03-stop-");
                string script = Path.Combine(isolated.FullName, "Run-App03Dev.ps1");
                try
                {
                    File.Copy(Path.Combine(root, "scripts", "Run-App03Dev.ps1"), script);
                    await StopUsingScript(script, run.Id, ct);
                    await hostTask!.WaitAsync(ct);
                    Ensure(api.HasExited && vite.HasExited, "Stop without build outputs left a host running.");
                }
                finally
                {
                    File.Delete(script);
                    isolated.Delete(recursive: false);
                }
            }
            var afterStop = await DevF01Evidence.Capture(run, Tables);
            Ensure(snapshot.SequenceEqual(afterStop), "Stop without build outputs changed database rows.");

            // Simulate a recoverable startup failure without changing either database.
            using (var occupied = new TcpListener(IPAddress.Loopback, App02c1Dev.ApiPort))
            {
                occupied.Start();
                await Reject(() => RunOperation("Resume"), $"port {App02c1Dev.ApiPort}");
            }
            var afterFailure = await DevF01Evidence.Capture(run, Tables);
            Ensure(snapshot.SequenceEqual(afterFailure), "Failed Resume changed database rows.");

            active = await Open("Resume");
            Ensure(active.WebUrl == webOrigin, "Resume changed the origin used for browser pending commands.");
            await Expect(active, C1 + "/session", HttpStatusCode.Unauthorized, token: originalToken);
            var identity = await Read<C1Identity>(active, C1 + "/session");
            Ensure(identity.SessionId == run.Id && identity.TenantId == Tenant && identity.ActorId == "c1-dev-sales",
                "Resume changed the run, actor or binding.");
            var outcomeUrl = C4 + $"/trips/{trip.Id}/commands/{delivery.CommandId}";
            var recovered = await Read<JsonElement>(active, outcomeUrl);
            var replayed = await Post(C4 + "/deliver", delivery);
            var ar = (await Read<C4ArView[]>(active, C4 + $"/trips/{trip.Id}/ar")).Single();
            var order = (await Read<C2OrderView[]>(active, C2 + "/orders")).Single();
            Ensure(recovered.GetProperty("sourceId").GetGuid() == before.SourceId
                && replayed.GetProperty("sourceId").GetGuid() == before.SourceId
                && replayed.GetProperty("replayed").GetBoolean() && ar.SourceId == before.SourceId
                && order.Id == orderId && order.Lines.Single().Delivered == 2
                && ar.Outstanding == expectedAr && ar.Cost == expectedCost, "Source, delivery state or balance changed on recovery.");
            var afterReplay = await DevF01Evidence.Capture(run, Tables);
            Ensure(snapshot.SequenceEqual(afterReplay), "Resume or replay changed seeded or posted rows.");
            await Stop();

            Ensure(await db.ExecuteAsync("""
                UPDATE dbo.App02ActorRoleGrant SET IsActive=0,ChangedBy=N'app03-1b-verifier'
                WHERE TenantId=N'tenant-a' AND ActorId=N'c1-dev-sales' AND RoleCode=N'sales' AND IsActive=1
                """) == 1, "Expected one active Sales grant.");
            var revokedSnapshot = await DevF01Evidence.Capture(run, Tables);
            active = await Open("Resume");
            await Expect(active, C1 + "/customers", HttpStatusCode.Forbidden);
            await Expect(active, outcomeUrl, HttpStatusCode.Forbidden);
            await Expect(active, C4 + "/deliver", HttpStatusCode.Forbidden, delivery);
            await Expect(active, C1 + "/customers/save", HttpStatusCode.Forbidden,
                new C1CustomerInput(Guid.NewGuid(), null, null, "APP03-DENIED", "Denied"));
            var afterRevocation = await DevF01Evidence.Capture(run, Tables);
            Ensure(revokedSnapshot.SequenceEqual(afterRevocation), "Resume or denied operations changed rows or grants.");
            await Stop();

            await VerifyAbruptExit(root, source, run.Id, ct);
            await Stop();
            var afterAbruptExit = await DevF01Evidence.Capture(run, Tables);
            Ensure(revokedSnapshot.SequenceEqual(afterAbruptExit), "Abrupt launcher exit changed rows or grants.");
            await Reject(() => run.Remove(), "requires the APP-03 Remove");

            await foreign.Create(Tenant);
            foreignCreated = true;
            await Reject(() => App03DevSession.Run("Remove", root, source, foreign.Id), "manifest");
            await foreign.Verify(Tenant);
            await using var peer = new SqlConnection(run.Connection("tenant-b"));
            await peer.ExecuteAsync("UPDATE dbo.LocalApp03Session SET RunId=@Id", new { foreign.Id });
            try
            {
                await Reject(() => RunOperation("Remove"), "ownership");
                foreach (string tenant in DemoRun.Tenants) await run.Verify(tenant);
            }
            finally { await peer.ExecuteAsync("UPDATE dbo.LocalApp03Session SET RunId=@Id", new { run.Id }); }
            await VerifyMissingReadyTenant(run, () => RunOperation("Remove"));
            await RunOperation("Remove");
            removed = true;
            Ensure(!await run.Exists("tenant-a") && !await run.Exists("tenant-b") && !App03DevSession.HasManifest(run.Id),
                "Remove left an owned database or manifest.");
            await foreign.Verify(Tenant);
            await foreign.Remove();
            foreignCreated = false;

            string dir = Directory.CreateDirectory(Path.Combine(root, "TestResults", "app03-1b", run.Id)).FullName;
            await File.WriteAllTextAsync(Path.Combine(dir, "session-review.json"), JsonSerializer.Serialize(new
            {
                status = "PASS", source, runId = run.Id, foreignRunId = foreign.Id, webOrigin, original = before,
                expectedAr, expectedCost, recovered, replayed, ar, orderStatus = order.Status,
                preservedRows = snapshot, revokedRows = revokedSnapshot,
                verified = new[] { "Stop and Resume at the same browser origin", "startup failure preserves rows", "old credential rejected",
                    "lost ACK lookup and replay", "revoked read/write/replay", "parent-only termination reaps both hosts",
                    "Stop from a standalone script without a checkout or build outputs",
                    "active, missing, or mismatched removal rejected", "isolated removal" }
            }, new JsonSerializerOptions { WriteIndented = true }), ct);
            Console.WriteLine($"APP-03.1b PASS: {dir}");

            async Task<JsonElement> Post(string path, object input) =>
                await Read<JsonElement>(active, path, input);
        }
        finally
        {
            try
            {
                if (hostTask is { IsCompleted: false }) await Stop();
                if (!removed && App03DevSession.HasManifest(run.Id)) await RunOperation("Remove");
                else if (!removed && await run.Exists("tenant-b"))
                    await run.Remove(app03Session: true, allowUnbound: true);
            }
            finally
            {
                if (foreignCreated) await foreign.Remove();
            }
        }

        Task RunOperation(string operation) => App03DevSession.Run(operation, root, source, run.Id);

        async Task<App03DevHost> Open(string operation)
        {
            var ready = new TaskCompletionSource<App03DevHost>(TaskCreationOptions.RunContinuationsAsynchronously);
            hostTask = App03DevSession.Run(operation, root, source, run.Id, host =>
            {
                ready.SetResult(host);
                return Task.CompletedTask;
            });
            if (await Task.WhenAny(ready.Task, hostTask).WaitAsync(ct) == hostTask) await hostTask;
            return await ready.Task.WaitAsync(ct);
        }

        async Task Stop()
        {
            await StopUsingScript(Path.Combine(root, "scripts", "Run-App03Dev.ps1"), run.Id, ct);
            if (hostTask is not null) await hostTask;
        }

        async Task<T> Read<T>(App03DevHost host, string path, object? input = null)
        {
            using var response = await Send(host, path, input, ct);
            Ensure(response.IsSuccessStatusCode, $"{path} returned {(int)response.StatusCode}.");
            return (await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct))!;
        }

        async Task Expect(App03DevHost host, string path, HttpStatusCode status, object? input = null, string? token = null)
        {
            using var response = await Send(host, path, input, ct, token);
            Ensure(response.StatusCode == status, $"{path} expected {(int)status}, actual {(int)response.StatusCode}.");
        }
    }

    private static async Task StopUsingScript(string script, string runId, CancellationToken ct)
    {
        var start = new ProcessStartInfo("pwsh")
        {
            WorkingDirectory = Path.GetDirectoryName(script)!, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (string arg in new[] { "-NoProfile", "-File", script, "-Action", "Stop", "-RunId", runId })
            start.ArgumentList.Add(arg);
        using var stop = Process.Start(start) ?? throw new InvalidOperationException("Could not start the Stop command.");
        var output = stop.StandardOutput.ReadToEndAsync(ct);
        var error = stop.StandardError.ReadToEndAsync(ct);
        try
        {
            await stop.WaitForExitAsync(ct);
            if (stop.ExitCode != 0)
                throw new InvalidOperationException($"Stop exited {stop.ExitCode}: {await error}");
            Console.Write(await output);
        }
        finally
        {
            if (!stop.HasExited)
            {
                stop.Kill(entireProcessTree: true);
                await stop.WaitForExitAsync();
            }
        }
    }

    private static async Task VerifyMissingReadyTenant(DemoRun run, Func<Task> remove)
    {
        const string tenant = "tenant-b";
        string original = run.Name(tenant);
        string temporary = new DemoRun(Guid.NewGuid().ToString("N")).Name(tenant);
        string manifest = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FmcgDms", "App03Dev", run.Id, "session.json");
        byte[] before = await File.ReadAllBytesAsync(manifest);
        var beforeRows = (await DevF01Evidence.Capture(run, Tables)).Where(row => row.Tenant == Tenant).ToArray();
        await run.Verify(tenant, app03Session: true);
        await using var master = new SqlConnection(run.Connection());
        int databaseId = await master.QuerySingleAsync<int>("SELECT DB_ID(@original)", new { original });
        Ensure(await master.ExecuteScalarAsync<int?>("SELECT DB_ID(@temporary)", new { temporary }) is null,
            "Temporary scratch name already exists.");
        await master.ExecuteAsync($"ALTER DATABASE [{original}] MODIFY NAME = [{temporary}];");
        try
        {
            await Reject(remove, "missing tenant-b");
            await run.Verify(Tenant, app03Session: true);
            byte[] after = await File.ReadAllBytesAsync(manifest);
            Ensure(before.SequenceEqual(after), "Missing tenant changed the Ready manifest.");
        }
        finally
        {
            Ensure(await master.ExecuteScalarAsync<int?>("SELECT DB_ID(@temporary)", new { temporary }) == databaseId
                && !await run.Exists(tenant), "Cannot restore the verified tenant-b scratch name.");
            await master.ExecuteAsync($"ALTER DATABASE [{temporary}] MODIFY NAME = [{original}];");
        }
        await run.Verify(tenant, app03Session: true);
        var afterRows = (await DevF01Evidence.Capture(run, Tables)).Where(row => row.Tenant == Tenant).ToArray();
        Ensure(beforeRows.SequenceEqual(afterRows), "Missing tenant changed tenant-a business rows.");
    }

    private static async Task VerifyAbruptExit(string root, string source, string runId, CancellationToken ct)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true
        };
        foreach (string arg in new[] { typeof(App03DevSessionEvidence).Assembly.Location, "App03SessionProbe", root, source, runId })
            start.ArgumentList.Add(arg);
        using var launcher = Process.Start(start) ?? throw new InvalidOperationException("Could not launch the APP-03 host probe.");
        Process? api = null;
        Process? vite = null;
        try
        {
            const string marker = "APP-03 probe ready ";
            string? line;
            do { line = await launcher.StandardOutput.ReadLineAsync(ct); }
            while (line is not null && !line.StartsWith(marker, StringComparison.Ordinal));
            if (line is null) throw new InvalidOperationException("Probe exited before both hosts became ready.");
            var ids = line[marker.Length..].Split(' ');
            api = Process.GetProcessById(int.Parse(ids[0]));
            vite = Process.GetProcessById(int.Parse(ids[1]));
            launcher.Kill(entireProcessTree: false);
            await launcher.WaitForExitAsync(ct);
            await Task.WhenAll(api.WaitForExitAsync(ct), vite.WaitForExitAsync(ct)).WaitAsync(TimeSpan.FromSeconds(10), ct);
        }
        finally
        {
            await Reap(launcher);
            await Reap(api);
            await Reap(vite);
            api?.Dispose();
            vite?.Dispose();
        }

        static async Task Reap(Process? child)
        {
            if (child is null || child.HasExited) return;
            child.Kill(entireProcessTree: true);
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    private static Task<DeliveryState> ReadDelivery(SqlConnection db) => db.QuerySingleAsync<DeliveryState>("""
        SELECT d.Id AS DeliveryId,d.BusinessSourceId AS SourceId,d.AOrderId AS OrderId,d.BaseQuantity AS Quantity,a.OutstandingAmount AS Outstanding,
               d.CostAmount AS Cost,l.Code AS Lot
        FROM dbo.SaleDelivery d
        JOIN dbo.ReceivableOpenItem a ON a.TenantId=d.TenantId AND a.SaleDeliveryId=d.Id
        JOIN dbo.F03LotIssue i ON i.TenantId=d.TenantId AND i.DeliveryId=d.Id
        JOIN dbo.F03Lot l ON l.TenantId=i.TenantId AND l.Id=i.LotId
        """);

    private static async Task<HttpResponseMessage> Send(App03DevHost host, string path, object? input, CancellationToken ct, string? token = null)
    {
        using var request = new HttpRequestMessage(input is null ? HttpMethod.Get : HttpMethod.Post, host.WebUrl + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token ?? host.SourceToken);
        if (input is not null) request.Content = JsonContent.Create(input);
        return await host.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    private static async Task Reject(Func<Task> action, string message)
    {
        try { await action(); }
        catch (InvalidOperationException ex) when (ex.Message.Contains(message, StringComparison.OrdinalIgnoreCase)) { return; }
        throw new InvalidOperationException($"Expected rejection mentioning {message}.");
    }

    private static void Ensure(bool ok, string message)
    {
        if (!ok) throw new InvalidOperationException(message);
    }

    private sealed record DeliveryState(Guid DeliveryId, Guid SourceId, Guid OrderId, decimal Quantity, decimal Outstanding, decimal Cost, string Lot);
    private sealed record IdView(Guid Id);
    private sealed record DeliveryLine(Guid Id, int ShipToVersion);
}
