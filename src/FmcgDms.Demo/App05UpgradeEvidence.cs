using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.Versioning;
using System.Text.Json;
using Dapper;
using FmcgDms.Api.Persistence;
using Microsoft.Data.SqlClient;

namespace FmcgDms.Demo;

internal static class App05UpgradeEvidence
{
    private const string TenantA = "tenant-a";
    private const string TenantB = "tenant-b";
    private const string C1 = "/api/synthetic/app02c1";
    private const string C2 = "/api/synthetic/app02c2";
    private const string C3 = "/api/synthetic/app02c3";
    private const string C4 = "/api/synthetic/app02c4";
    private static readonly string[] BusinessTables =
    [
        "AOrder", "AOrderLine", "AConfirmedLineSnapshot", "C2LinePolicy", "TripManifestLine",
        "SaleDelivery", "ReceivableOpenItem", "PhysicalStockMovement", "F04DeliveryPricing",
        "BusinessCommand", "BusinessSource", "LedgerEffect", "F03LotIssue"
    ];

    [SupportedOSPlatform("windows")]
    internal static async Task Run(string root, string source)
    {
        var run = new DemoRun(Guid.NewGuid().ToString("N"));
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var ct = deadline.Token;
        Task? hostTask = null;
        var firstDay = App03DayBootstrap.FirstDay;
        var secondDay = firstDay.AddDays(1);
        string folder = Directory.CreateDirectory(Path.Combine(root, "TestResults", "app05-1-upgrade", run.Id)).FullName;
        Console.WriteLine($"APP-05.1 upgrade evidence: {folder}");

        try
        {
            var first = await Open("Start");
            Require(first.CloseCredentials is null, "Predecessor unexpectedly exposed APP-05.1 credentials.");
            var (orderId, deliveryCommand) = await PostOriginalDelivery(first, ct);
            await Stop();
            var before = await DevF01Evidence.Capture(run, BusinessTables);
            Require(before.Single(row => row.Tenant == TenantA && row.Table == "SaleDelivery").Rows == 1,
                "Predecessor did not contain the original delivery.");
            var oldOpenings = await ReadOriginalOpenings();
            Require(await VersionCount(TenantA) == 0 && await VersionCount(TenantB) == 0,
                "Predecessor already contains the new migration.");

            // Hold B's existing migration lock while requesting the real upgrade.
            // Tenant A commits normally, then B times out before any migration there.
            await run.Verify(TenantB, app03Session: true);
            await using (var blocked = new SqlConnection(run.Connection(TenantB)))
            {
                await blocked.OpenAsync(ct);
                await blocked.ExecuteAsync(new CommandDefinition("""
                    DECLARE @result int;
                    EXEC @result=sys.sp_getapplock @Resource=N'APP-03.1a migration',
                        @LockMode='Exclusive', @LockOwner='Session', @LockTimeout=0;
                    IF @result<0 THROW 51781, 'Test could not hold the second tenant migration lock.', 1;
                    """, cancellationToken: ct));
                try
                {
                    await App03DevSession.Run("Resume", root, source, run.Id, closeReadiness: true);
                    throw new InvalidOperationException("Upgrade unexpectedly passed while the second tenant migration was blocked.");
                }
                catch (SqlException ex) when (ex.Number == 51000 &&
                    ex.Message.Contains("Could not lock APP-03.1a migration.", StringComparison.Ordinal))
                {
                    Require(await VersionCount(TenantA) == 1 && await VersionCount(TenantB) == 0,
                        "Failure did not leave exactly the first tenant migrated.");
                }
                finally
                {
                    await blocked.ExecuteAsync(new CommandDefinition("""
                        EXEC sys.sp_releaseapplock @Resource=N'APP-03.1a migration', @LockOwner='Session';
                        """, cancellationToken: ct));
                }
            }

            var preparedA = await Fixture(TenantA);
            var resumed = await Open("Resume");
            Require(resumed.CloseCredentials is not null && resumed.WebUrl == first.WebUrl,
                "Plain Resume did not recover the persisted upgrade target at its original browser origin.");
            var recoveredA = await Fixture(TenantA);
            var recoveredB = await Fixture(TenantB);
            Require(preparedA == recoveredA, "Resume reseeded the already upgraded tenant.");
            VerifyFixture(recoveredA);
            VerifyFixture(recoveredB);
            Require(recoveredA.CustomerId != recoveredB.CustomerId && recoveredA.SkuId != recoveredB.SkuId,
                "Tenants share APP-05.1 fixture source IDs.");
            Require(await VersionCount(TenantA) == 1 && await VersionCount(TenantB) == 1,
                "Resume did not complete the migration exactly once per tenant.");

            var replay = await Read<JsonElement>(resumed,
                C4 + $"/trips/{deliveryCommand.TripId}/commands/{deliveryCommand.CommandId}", ct);
            var duplicate = await Read<JsonElement>(resumed, C4 + "/deliver", ct, deliveryCommand);
            Require(replay.GetProperty("sourceId").GetGuid() == duplicate.GetProperty("sourceId").GetGuid(),
                "The predecessor delivery could not be recovered using its original command and source.");
            await Stop();
            await using (var revoke = new SqlConnection(run.Connection(TenantA)))
            {
                int changed = await revoke.ExecuteAsync(new CommandDefinition("""
                    UPDATE dbo.App05TripOperator SET IsActive=0
                    WHERE TenantId=@TenantA AND TripId=@TripId AND ActorId=@Ops;
                    """, new { TenantA, deliveryCommand.TripId, App05CloseBootstrap.Ops }, cancellationToken: ct));
                Require(changed == 1, "The original readiness operator was not assigned.");
            }

            await App03DevSession.Run("NextDay", root, source, run.Id, businessDate: secondDay);
            await App03DevSession.Run("NextDay", root, source, run.Id, businessDate: secondDay);
            var next = await Open("Resume");
            Require(next.CloseCredentials is not null && next.WebUrl == first.WebUrl,
                "NextDay did not preserve the readiness profile and browser origin.");
            await Stop();

            var after = await DevF01Evidence.Capture(run, BusinessTables);
            var newOpenings = await ReadOriginalOpenings();
            Require(before.SequenceEqual(after) && oldOpenings.SequenceEqual(newOpenings),
                "Upgrade or NextDay changed an original order, delivery, snapshot, ledger or opening.");
            var assignmentsA = await Assignments(TenantA);
            var assignmentsB = await Assignments(TenantB);
            Require(assignmentsA.SequenceEqual(new[] { 3, 6, 5 }) && assignmentsB.SequenceEqual(new[] { 3, 6, 6 }),
                "NextDay did not provision only new trips or restored a revoked operator.");
            await App03DevSession.Run("Remove", root, source, run.Id);
            Require(!App03DevSession.HasManifest(run.Id) && !await run.Exists(TenantA) && !await run.Exists(TenantB),
                "Remove did not clear both nonce-owned session targets.");

            await File.WriteAllTextAsync(Path.Combine(folder, "upgrade-result.json"), JsonSerializer.Serialize(new
            {
                status = "PASS", source, tree = Environment.GetEnvironmentVariable("APP05_EVIDENCE_TREE"),
                runId = run.Id, instance = DemoRun.Instance, orderId, deliveryCommand.CommandId,
                interruptedVersions = new[] { 1, 0 }, recoveredVersions = new[] { 1, 1 },
                expectedOpeningQuantity = 60, expectedOpeningValue = 180000, expectedCreditCap = 500000,
                before, after, oldOpenings, newOpenings, fixture = new[] { recoveredA, recoveredB },
                assignmentsA, assignmentsB, removed = true
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }), ct);
        }
        finally
        {
            if (hostTask is not null) await Stop();
            if (App03DevSession.HasManifest(run.Id)) await App03DevSession.Run("Remove", root, source, run.Id);
        }

        async Task<App03DevHost> Open(string action)
        {
            var ready = new TaskCompletionSource<App03DevHost>(TaskCreationOptions.RunContinuationsAsynchronously);
            hostTask = App03DevSession.Run(action, root, source, run.Id, host =>
            {
                ready.SetResult(host);
                return Task.CompletedTask;
            }, settlement: true, finance: true);
            if (await Task.WhenAny(hostTask, ready.Task).WaitAsync(ct) == hostTask) await hostTask;
            return await ready.Task.WaitAsync(ct);
        }

        async Task Stop()
        {
            var active = hostTask;
            hostTask = null;
            if (active is null) return;
            if (!active.IsCompleted)
            {
                using var signal = EventWaitHandle.OpenExisting($@"Local\FmcgDms.APP03.{run.Id}");
                signal.Set();
            }
            await active.WaitAsync(TimeSpan.FromSeconds(30));
        }

        async Task<int> VersionCount(string tenant)
        {
            await using var db = new SqlConnection(run.Connection(tenant));
            return await db.ExecuteScalarAsync<int>(new CommandDefinition("""
                SELECT COUNT(*) FROM dbo.VersionInfo WHERE Version=202611250001;
                """, cancellationToken: ct));
        }

        async Task<FixtureState> Fixture(string tenant)
        {
            await using var db = new SqlConnection(run.Connection(tenant));
            return await db.QuerySingleAsync<FixtureState>(new CommandDefinition("""
                SELECT s.Id AS SkuId,c.Id AS CustomerId,l.Id AS LotId,p.Id AS PolicyId,l.ExpiresOn,
                    o.BaseQuantity,o.CostAmount,lo.Quantity AS LotOpening,p.UnitPrice,
                    p.DiscountPerUnit,p.TaxRate,p.TermsDays,p.CreditCap,p.Maker,p.Checker,b.Version
                FROM dbo.LocalApp05Bootstrap b
                JOIN dbo.Sku s ON s.TenantId=@tenant AND s.Code=@SkuCode
                JOIN dbo.Customer c ON c.TenantId=s.TenantId AND c.Code=@CustomerCode
                JOIN dbo.C2CommercialPolicy p ON p.TenantId=s.TenantId AND p.CustomerId=c.Id AND p.SkuId=s.Id
                JOIN dbo.F03Lot l ON l.TenantId=s.TenantId AND l.SkuId=s.Id
                JOIN dbo.StockLocation w ON w.TenantId=s.TenantId AND w.Code='C2-MAIN'
                JOIN dbo.PhysicalStockOpening o ON o.TenantId=s.TenantId AND o.StockLocationId=w.Id AND o.SkuId=s.Id
                JOIN dbo.F03LotOpening lo ON lo.TenantId=s.TenantId AND lo.LotId=l.Id AND lo.LocationId=w.Id;
                """, new { tenant, App05CloseBootstrap.SkuCode, App05CloseBootstrap.CustomerCode }, cancellationToken: ct));
        }

        async Task<string[]> ReadOriginalOpenings()
        {
            var images = new List<string>();
            foreach (string tenant in DemoRun.Tenants)
            {
                await using var db = new SqlConnection(run.Connection(tenant));
                images.AddRange(await db.QueryAsync<string>(new CommandDefinition("""
                    SELECT (SELECT o.* FOR JSON PATH,WITHOUT_ARRAY_WRAPPER,INCLUDE_NULL_VALUES) FROM dbo.PhysicalStockOpening o
                    JOIN dbo.Sku s ON s.TenantId=o.TenantId AND s.Id=o.SkuId
                    WHERE o.TenantId=@tenant AND s.Code<>@SkuCode ORDER BY o.SkuId,o.StockLocationId;
                    """, new { tenant, App05CloseBootstrap.SkuCode }, cancellationToken: ct)));
            }
            return images.ToArray();
        }

        async Task<int[]> Assignments(string tenant)
        {
            await using var db = new SqlConnection(run.Connection(tenant));
            using var grid = await db.QueryMultipleAsync(new CommandDefinition("""
                SELECT COUNT(*) FROM dbo.LocalApp05TripBootstrap;
                SELECT COUNT(*) FROM dbo.App05TripOperator WHERE TenantId=@tenant;
                SELECT COUNT(*) FROM dbo.App05TripOperator WHERE TenantId=@tenant AND IsActive=1;
                """, new { tenant }, cancellationToken: ct));
            return [await grid.ReadSingleAsync<int>(), await grid.ReadSingleAsync<int>(), await grid.ReadSingleAsync<int>()];
        }

        static void VerifyFixture(FixtureState fixture) => Require(fixture is
        {
            Version: App05CloseBootstrap.Version, BaseQuantity: 60, CostAmount: 180000, LotOpening: 60,
            UnitPrice: 10000, DiscountPerUnit: 0, TaxRate: 0, TermsDays: 7, CreditCap: 500000,
            Maker: App05CloseBootstrap.Maker, Checker: App05CloseBootstrap.Checker
        } && fixture.ExpiresOn.Date > App03DayBootstrap.LastDay.ToDateTime(TimeOnly.MinValue),
            "APP-05.1 fixture did not match the predeclared synthetic opening, lot or policy.");
    }

    private static async Task<(Guid OrderId, C4DeliveryInput Delivery)> PostOriginalDelivery(App03DevHost host,
        CancellationToken ct)
    {
        var catalog = await Read<C2Catalog>(host, C2 + "/catalog", ct);
        var customer = catalog.Customers.Single(row => row.Label.StartsWith("C2-FIXTURE", StringComparison.Ordinal));
        var trip = catalog.Trips.Single(row => row.Label == "DEV-20261123-A");
        var sku = catalog.Skus.Single(row => row.Label.StartsWith("R-05 ·", StringComparison.Ordinal));
        var warehouse = catalog.Warehouses.Single(row => row.Label == "C2-MAIN");
        var master = await Read<C1Detail>(host, C1 + $"/customers/{customer.Id}", ct);
        var registered = master.RegisteredShipTos.Single();
        var selection = await Read<JsonElement>(host, C1 + "/ship-to-selections", ct,
            new C1SelectionInput(Guid.NewGuid(), customer.Id, null, registered.Id, registered.Revision, null));
        var draft = await Read<JsonElement>(host, C2 + "/drafts", ct,
            new C2DraftInput(Guid.NewGuid(), customer.Id, trip.Id, selection.GetProperty("id").GetGuid(), [new(sku.Id, 2)]));
        Guid order = draft.GetProperty("sourceId").GetGuid();
        await Read<JsonElement>(host, C2 + "/confirm", ct, new C2ConfirmInput(Guid.NewGuid(), order));
        var confirmed = (await Read<C2OrderView[]>(host, C2 + "/orders", ct)).Single(row => row.Id == order);
        await Read<JsonElement>(host, C2 + "/reserve", ct, new C2ReserveInput(Guid.NewGuid(), order,
            confirmed.Lines.Single().LineId!.Value, warehouse.Id, 2, confirmed.ConfirmSourceId!.Value));
        var right = (await Read<IdView[]>(host, C3 + $"/trips/{trip.Id}/rights", ct, token: host.ReceiverToken)).Single();
        await Read<JsonElement>(host, C3 + "/load", ct, new C3LoadInput(Guid.NewGuid(), trip.Id, [new(right.Id, 2, 2)]),
            host.ReceiverToken);
        var line = (await Read<DeliveryLine[]>(host, C4 + $"/trips/{trip.Id}/lines", ct)).Single();
        var delivery = new C4DeliveryInput(Guid.NewGuid(), trip.Id, line.Id, line.ShipToVersion, 2);
        await Read<JsonElement>(host, C4 + "/deliver", ct, delivery);
        return (order, delivery);
    }

    private static async Task<T> Read<T>(App03DevHost host, string path, CancellationToken ct,
        object? input = null, string? token = null)
    {
        using var request = new HttpRequestMessage(input is null ? HttpMethod.Get : HttpMethod.Post, host.WebUrl + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token ?? host.SourceToken);
        if (input is not null) request.Content = JsonContent.Create(input);
        using var response = await host.Client.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"{path} returned {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(ct)}");
        return (await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct))!;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed record IdView(Guid Id);
    private sealed record DeliveryLine(Guid Id, int ShipToVersion);
    private sealed record FixtureState(Guid SkuId, Guid CustomerId, Guid LotId, Guid PolicyId, DateTime ExpiresOn,
        decimal BaseQuantity, decimal CostAmount, decimal LotOpening, decimal UnitPrice, decimal DiscountPerUnit,
        decimal TaxRate, int TermsDays, decimal CreditCap, string Maker, string Checker, string Version);
}
