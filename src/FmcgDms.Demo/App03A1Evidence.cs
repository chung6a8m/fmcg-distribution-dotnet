using System.Diagnostics;
using System.Text.Json;
using Dapper;
using FmcgDms.Api.Persistence;
using Microsoft.Data.SqlClient;

namespace FmcgDms.Demo;

internal sealed class App03A1Evidence(DemoRun run, string root, string actor)
{
    private static readonly string[] PreservedTables =
    [
        "AOrder", "AOrderLine", "AConfirmedLineSnapshot", "StockRight", "TripManifestLine", "SaleDelivery",
        "ReceivableOpenItem", "PhysicalStockMovement", "PhysicalStockBalance", "LedgerEffect",
        "F03LotHold", "F03LotLoad", "F03LotIssue", "F04PriceLine", "F04DeliveryPricing"
    ];
    private const string TenantA = "tenant-a";
    internal string DirectoryPath { get; } = Directory.CreateDirectory(Path.Combine(root, "TestResults", "app03-1a", run.Id)).FullName;

    internal async Task Verify(Process browser, string source, string apiSha256, Func<Func<Task>, Task> restart, CancellationToken ct)
    {
        await using var a = new SqlConnection(run.Connection(TenantA));
        await using var b = new SqlConnection(run.Connection("tenant-b"));
        await WaitFor("upgrade");
        var before = await DevF01Evidence.Capture(run, PreservedTables);
        var predecessor = await Versions(a);
        Guid existingOrder = await a.QuerySingleAsync<Guid>("SELECT Id FROM dbo.AOrder WHERE TenantId=N'tenant-a'");
        await restart(async () =>
        {
            await run.Verify(TenantA);
            ScratchMigrator.MigrateApp03A1(run.Connection(TenantA), TenantA, run.Id, LogLevel.Warning);
            ScratchMigrator.MigrateApp03A1(run.Connection(TenantA), TenantA, run.Id, LogLevel.Warning);
            await App02c1Dev.AssignVisitCustomer(a, TenantA);
        });
        var upgraded = await DevF01Evidence.Capture(run, PreservedTables);
        var versionsA = await Versions(a);
        var versionsB = await Versions(b);
        var expectedVersions = predecessor.Append(202611230002L).Order().ToArray();
        await Ready("upgrade");

        await WaitFor("delivered");
        var delivered = await DevF01Evidence.Capture(run, PreservedTables);
        var delivery = await a.QuerySingleAsync<Delivery>("""
            SELECT d.Id,d.AOrderId AS OrderId,i.LotId,l.Code AS Lot,d.BaseQuantity AS Quantity,p.Currency,
                   p.GrossAmount AS Gross,ar.OutstandingAmount AS Outstanding,d.CostAmount AS Cost
            FROM dbo.SaleDelivery d
            JOIN dbo.F03LotIssue i ON i.TenantId=d.TenantId AND i.DeliveryId=d.Id AND i.Quantity=d.BaseQuantity
            JOIN dbo.F03Lot l ON l.TenantId=i.TenantId AND l.Id=i.LotId
            JOIN dbo.F04DeliveryPricing p ON p.TenantId=d.TenantId AND p.DeliveryId=d.Id
            JOIN dbo.ReceivableOpenItem ar ON ar.TenantId=d.TenantId AND ar.SaleDeliveryId=d.Id
            """);
        var expectedDelivery = delivery with { OrderId = existingOrder, Lot = "RET-ORIGIN", Quantity = 2, Currency = "VND",
            Gross = 20000, Outstanding = 20000, Cost = 6667 };
        await Ready("delivered");

        await WaitFor("visited");
        var visited = await DevF01Evidence.Capture(run, PreservedTables);
        var visit = await a.QuerySingleAsync<Visit>("""
            SELECT v.Id,v.ActorId,v.Outcome,c.Code AS Customer,k.Kind AS CommandKind,v.CompletedAtUtc
            FROM dbo.F01Visit v
            JOIN dbo.Customer c ON c.TenantId=v.TenantId AND c.Id=v.CustomerId
            JOIN dbo.C1Command k ON k.TenantId=v.TenantId AND k.ActorId=v.ActorId
              AND k.CommandId=v.CommandId AND k.ResultId=v.Id AND k.HttpStatus=200
            WHERE v.TenantId=N'tenant-a'
            """);
        var expectedVisit = visit with { ActorId = actor, Outcome = "NO_ORDER", Customer = "C2-FIXTURE",
            CommandKind = "VISIT_COMPLETE", CompletedAtUtc = DevF05Evidence.FixtureUtc };
        await Ready("visited");

        await WaitFor("revoke");
        if (await a.ExecuteAsync("""
            UPDATE dbo.App02ActorRoleGrant SET IsActive=0,ChangedBy=N'app03-1a-verifier',ChangedAtUtc=SYSUTCDATETIME()
            WHERE TenantId=N'tenant-a' AND ActorId=@actor AND RoleCode=N'sales' AND IsActive=1
            """, new { actor }) != 1)
            throw new InvalidOperationException("Expected one current Sales grant to revoke.");
        await Ready("revoke");
        await browser.WaitForExitAsync(ct);

        var actual = await a.QuerySingleAsync<ReturnRequest>("""
            SELECT r.Id,r.DeliveryId,r.LotId,r.Quantity,r.RequestedBy,c.Status AS CommandStatus
            FROM dbo.F05ReturnCase r
            JOIN dbo.BusinessSource s ON s.TenantId=r.TenantId AND s.Id=r.Id
            JOIN dbo.BusinessCommand c ON c.TenantId=s.TenantId AND c.Id=s.BusinessCommandId
            WHERE r.TenantId=N'tenant-a'
            """);
        var expectedReturn = new ReturnRequest(actual.Id, delivery.Id, delivery.LotId, 1, actor, "APPLIED");
        var final = await DevF01Evidence.Capture(run, PreservedTables);
        var counts = await b.QuerySingleAsync<PeerCounts>("""
            SELECT (SELECT COUNT(*) FROM dbo.AOrder) AS Orders,(SELECT COUNT(*) FROM dbo.F01Visit) AS Visits,
                   (SELECT COUNT(*) FROM dbo.SaleDelivery) AS Deliveries,
                   (SELECT COUNT(*) FROM dbo.ReceivableOpenItem) AS Receivables,
                   (SELECT COUNT(*) FROM dbo.F05ReturnCase) AS ReturnCases
            """);
        var expectedPeer = new PeerCounts(0, 0, 0, 0, 0);
        bool upgradePreserved = before.SequenceEqual(upgraded);
        bool visitPreserved = delivered.SequenceEqual(visited);
        bool returnPreserved = visited.SequenceEqual(final);
        bool peerPreserved = before.Where(row => row.Tenant == "tenant-b")
            .SequenceEqual(final.Where(row => row.Tenant == "tenant-b"));
        bool passed = browser.ExitCode == 0 && versionsA.SequenceEqual(expectedVersions) && versionsB.SequenceEqual(expectedVersions)
            && upgradePreserved && visitPreserved && returnPreserved && peerPreserved
            && delivery == expectedDelivery && visit == expectedVisit && actual == expectedReturn && counts == expectedPeer;
        await File.WriteAllTextAsync(Path.Combine(DirectoryPath, "sql-review.json"), JsonSerializer.Serialize(new
        {
            status = passed ? "PASS" : "FAIL", runId = run.Id, profile = App03A1Profile.Name, source, apiSha256,
            clockUtc = DevF05Evidence.FixtureUtc, predecessor, expectedVersions, versionsA, versionsB,
            delivery = new { expected = expectedDelivery, actual = delivery, matches = expectedDelivery == delivery },
            visit = new { expected = expectedVisit, actual = visit, matches = expectedVisit == visit },
            returnCase = new { expected = expectedReturn, actual, matches = expectedReturn == actual },
            peer = new { expected = expectedPeer, actual = counts, matches = expectedPeer == counts },
            rowImages = new { before, upgraded, delivered, visited, final, upgradePreserved, visitPreserved,
                returnPreserved, peerPreserved },
            browserExitCode = browser.ExitCode,
            startupRejections = new[] { "Missing schema", "Missing F04 dependency", "Unsupported F02 combination" }
        }, new JsonSerializerOptions { WriteIndented = true }), ct);
        if (!passed) throw new InvalidOperationException("APP-03.1a verification failed. See sql-review.json and browser results.");
        Console.WriteLine($"APP-03.1a PASS: {Path.Combine(DirectoryPath, "sql-review.json")}");

        async Task WaitFor(string phase)
        {
            string path = Path.Combine(DirectoryPath, phase + ".request");
            while (!File.Exists(path))
            {
                if (browser.HasExited) throw new InvalidOperationException($"Browser exited before {phase}: {browser.ExitCode}.");
                await Task.Delay(75, ct);
            }
            if (await File.ReadAllTextAsync(path, ct) != run.Id)
                throw new InvalidDataException($"Checkpoint {phase} belongs to another run.");
        }

        async Task Ready(string phase)
        {
            string path = Path.Combine(DirectoryPath, phase + ".ready");
            await File.WriteAllTextAsync(path + ".partial", run.Id, ct);
            File.Move(path + ".partial", path);
        }
    }

    internal static async Task ExpectStartupRejection(ProcessStartInfo start, string reason, CancellationToken ct)
    {
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start API for negative check.");
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(12));
        try
        {
            await process.WaitForExitAsync(deadline.Token);
            string error = await stderr;
            await stdout;
            if (process.ExitCode == 0 || !error.Contains(reason, StringComparison.Ordinal))
                throw new InvalidOperationException($"API did not reject profile with the expected reason: {reason}.");
            Console.WriteLine($"APP-03 startup rejection verified: {reason}");
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
    }

    private static async Task<long[]> Versions(SqlConnection db) =>
        (await db.QueryAsync<long>("SELECT Version FROM dbo.VersionInfo ORDER BY Version")).ToArray();

    private sealed record Delivery(Guid Id, Guid OrderId, Guid LotId, string Lot, decimal Quantity, string Currency,
        long Gross, decimal Outstanding, decimal Cost);
    private sealed record Visit(Guid Id, string ActorId, string Outcome, string Customer, string CommandKind, DateTime CompletedAtUtc);
    private sealed record ReturnRequest(Guid Id, Guid DeliveryId, Guid LotId, decimal Quantity, string RequestedBy, string CommandStatus);
    private sealed record PeerCounts(int Orders, int Visits, int Deliveries, int Receivables, int ReturnCases);
}
