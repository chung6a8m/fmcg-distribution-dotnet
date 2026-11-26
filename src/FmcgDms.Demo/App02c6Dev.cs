using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Dapper;
using Microsoft.Data.SqlClient;

namespace FmcgDms.Demo;

internal sealed partial class App02c6Dev
{
    internal const string Checker = "c6-dev-checker";
    private const string MatrixSha = "04743B66AF77F02D3DA86EED229498D6AC947C201B5C189A3BF79BB96DFA6290";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly DemoRun run;
    private readonly string root;
    private readonly string source;
    private readonly string apiSha256;
    private readonly string directory;

    internal App02c6Dev(string root, DemoRun run, string source, string apiSha256)
    {
        this.root = root;
        this.run = run;
        this.source = source;
        this.apiSha256 = apiSha256;
        string matrix = File.ReadAllText(Path.Combine(root, "docs", "roadmap", "app-02c6-localdb-acceptance-matrix.md"));
        if (Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(matrix.Replace("\r\n", "\n")))) != MatrixSha)
            throw new InvalidOperationException("C.6 requires the Owner-approved v0.1 matrix bytes.");
        directory = Path.Combine(root, "TestResults", "app02c6", run.Id);
        Directory.CreateDirectory(CheckpointPath);
    }

    // The C.2/C.4 SQL transaction lock can wait for 10 seconds.
    // Give the acceptance probe time to observe the resulting response.
    internal static HttpClient CreateEvidenceClient() =>
        new(new EvidenceTimingHandler()) { Timeout = TimeSpan.FromSeconds(15) };

    private sealed class EvidenceTimingHandler() : DelegatingHandler(new SocketsHttpHandler { UseProxy = false })
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var timer = Stopwatch.StartNew();
            string path = request.RequestUri!.AbsolutePath;
            try
            {
                var response = await base.SendAsync(request, ct);
                if (timer.Elapsed >= TimeSpan.FromSeconds(3))
                    Console.Error.WriteLine($"C.6 HTTP {request.Method} {path}: {(int)response.StatusCode} after {timer.ElapsedMilliseconds} ms");
                return response;
            }
            catch (OperationCanceledException)
            {
                Console.Error.WriteLine($"C.6 HTTP {request.Method} {path}: canceled after {timer.ElapsedMilliseconds} ms");
                throw;
            }
        }
    }

    internal DateTimeOffset InitialTime { get; } = new(2026, 10, 31, 16, 0, 0, TimeSpan.Zero);
    internal DateTimeOffset CanonicalTime { get; } = new(2026, 10, 31, 17, 5, 0, TimeSpan.Zero);
    internal string ClockPath => Path.Combine(directory, "clock.utc");
    internal string CheckpointPath => Path.Combine(directory, "checkpoints");

    internal async Task SetClock(DateTimeOffset utc)
    {
        string staging = ClockPath + ".next";
        await File.WriteAllTextAsync(staging, utc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        File.Move(staging, ClockPath, overwrite: true);
    }

    internal async Task Prepare(SqlConnection db, string tenant)
    {
        await run.Verify(tenant);
        await db.ExecuteAsync("""
            CREATE USER App02C6SqlOracle WITHOUT LOGIN;
            GRANT SELECT ON SCHEMA::dbo TO App02C6SqlOracle;
            DENY INSERT, UPDATE, DELETE ON SCHEMA::dbo TO App02C6SqlOracle;
            INSERT dbo.App02ActorRoleGrant(TenantId,ActorId,RoleCode,IsActive,ChangedBy)
            VALUES(@tenant,@Checker,N'sales',1,N'c6-dev-launcher'),
                  (@tenant,@Checker,N'case_checker',1,N'c6-dev-launcher');
            """, new { tenant, Checker });
    }

    internal static async Task<SqlConnection> ReadOnly(DemoRun run, string tenant)
    {
        await run.Verify(tenant);
        var db = new SqlConnection(run.Connection(tenant));
        try
        {
            await db.OpenAsync();
            await db.ExecuteAsync("EXECUTE AS USER = 'App02C6SqlOracle';");
            int safe = await db.ExecuteScalarAsync<int>("""
                SELECT IIF(USER_NAME()='App02C6SqlOracle' AND
                    HAS_PERMS_BY_NAME('dbo','SCHEMA','SELECT')=1 AND
                    HAS_PERMS_BY_NAME('dbo','SCHEMA','INSERT')=0 AND
                    HAS_PERMS_BY_NAME('dbo','SCHEMA','UPDATE')=0 AND
                    HAS_PERMS_BY_NAME('dbo','SCHEMA','DELETE')=0,1,0)
                """);
            if (safe != 1) throw new InvalidOperationException("C.6 SQL oracle role is not read-only.");
            return db;
        }
        catch
        {
            await db.DisposeAsync();
            throw;
        }
    }

    private sealed record Checkpoint(string Slot, string CaseId, string Phase, Guid? OrderId, Guid? CustomerId);

    internal async Task ServeCheckpoints(Process browser, CancellationToken ct)
    {
        do
        {
            foreach (string path in Directory.EnumerateFiles(CheckpointPath, "*.request.json"))
            {
                string stem = Path.GetFileName(path)[..^".request.json".Length];
                string destination = Path.Combine(CheckpointPath, stem + ".response.json");
                if (File.Exists(destination)) continue;
                Checkpoint request = JsonSerializer.Deserialize<Checkpoint>(await File.ReadAllTextAsync(path, ct), Json)
                    ?? throw new InvalidDataException("C.6 checkpoint is empty.");
                if (request.Slot is not ("A" or "B") || !CaseName().IsMatch(request.CaseId) ||
                    !PhaseName().IsMatch(request.Phase) || !stem.Equals($"{request.CaseId}-{request.Slot}-{request.Phase}", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("C.6 checkpoint identity is invalid.");
                string tenant = request.Slot == "A" ? "tenant-a" : "tenant-b";
                if (request.Phase is "register-policy" or "register-expired-policy")
                    await RegisterPolicy(tenant, request.CustomerId, request.Phase == "register-expired-policy");
                var snapshot = await Observe(run, tenant);
                if (request.OrderId is Guid order)
                    snapshot = snapshot with { OrderTrace = await TraceOrder(tenant, order) };
                await WriteJson(destination, snapshot);
            }
            if (browser.HasExited) return;
            await Task.Delay(75, ct);
        } while (true);
    }

    private async Task RegisterPolicy(string tenant, Guid? customerId, bool expired)
    {
        if (customerId is null || customerId == Guid.Empty)
            throw new InvalidDataException("C.6 fixture requires a created customer.");
        await run.Verify(tenant);
        await using var db = new SqlConnection(run.Connection(tenant));
        DateTime from = InitialTime.UtcDateTime.AddDays(-3);
        DateTime until = InitialTime.UtcDateTime.AddDays(expired ? -1 : 1);
        int count = await db.ExecuteAsync("""
            INSERT dbo.C2CommercialPolicy(TenantId,Id,CustomerId,SkuId,Version,Decision,EffectiveFromUtc,EffectiveUntilUtc,
                Maker,Checker,ApprovedAtUtc,EvidenceRef,UnitPrice,DiscountPerUnit,TaxRate,TermsDays,CreditCap,SellerTimeZone)
            SELECT @tenant,NEWID(),c.Id,s.Id,1,'APPROVED',@from,@until,
                N'c6-maker',N'c6-checker',@from,N'c6-v0.1',15,0,0,7,1000,'Asia/Ho_Chi_Minh'
            FROM dbo.Customer c JOIN dbo.Sku s ON s.TenantId=c.TenantId AND s.Code=N'C2-SKU'
            WHERE c.TenantId=@tenant AND c.Id=@customerId
            """, new { tenant, customerId, from, until });
        if (count != 1) throw new InvalidOperationException("C.6 fixture customer or SKU is missing.");
    }

    internal sealed record Snapshot(decimal Main, decimal Truck, decimal MainValue, decimal TruckValue,
        decimal Allocated, int Deliveries, decimal Ar, decimal Cogs, int Manifests, decimal Held, int Orders, string Fingerprint)
    {
        public object? OrderTrace { get; init; }
    }

    internal static async Task<Snapshot> Observe(DemoRun run, string tenant)
    {
        await using var db = await ReadOnly(run, tenant);
        return await db.QuerySingleAsync<Snapshot>("""
            SELECT
                COALESCE(SUM(IIF(l.Code='C2-MAIN',b.OnHandBaseQuantity,0)),0) AS Main,
                COALESCE(SUM(IIF(l.Code='C2-TRUCK',b.OnHandBaseQuantity,0)),0) AS Truck,
                COALESCE(SUM(IIF(l.Code='C2-MAIN',b.OnHandCostAmount,0)),0) AS MainValue,
                COALESCE(SUM(IIF(l.Code='C2-TRUCK',b.OnHandCostAmount,0)),0) AS TruckValue,
                (SELECT COALESCE(SUM(AllocatedBaseQuantity),0) FROM dbo.StockPool) AS Allocated,
                (SELECT COUNT(*) FROM dbo.SaleDelivery) AS Deliveries,
                (SELECT COALESCE(SUM(OriginalAmount),0) FROM dbo.ReceivableOpenItem) AS Ar,
                (SELECT COALESCE(SUM(CostAmount),0) FROM dbo.SaleDelivery) AS Cogs,
                (SELECT COUNT(*) FROM dbo.TripManifest) AS Manifests,
                (SELECT COALESCE(SUM(LoadedBaseQuantity),0) FROM dbo.TripManifestLine WHERE IsC3=1)
                    - (SELECT COALESCE(SUM(BaseQuantity),0) FROM dbo.SaleDelivery WHERE C4EvidenceId IS NOT NULL) AS Held,
                (SELECT COUNT(*) FROM dbo.AOrder) AS Orders,
                CONVERT(varchar(64),HASHBYTES('SHA2_256',CONCAT(
                    (SELECT * FROM dbo.Customer ORDER BY Id FOR JSON PATH, INCLUDE_NULL_VALUES),
                    (SELECT * FROM dbo.C1ShipToSelection ORDER BY Id FOR JSON PATH, INCLUDE_NULL_VALUES),
                    (SELECT * FROM dbo.AOrder ORDER BY Id FOR JSON PATH, INCLUDE_NULL_VALUES),
                    (SELECT * FROM dbo.StockRight ORDER BY Id FOR JSON PATH, INCLUDE_NULL_VALUES),
                    (SELECT * FROM dbo.TripManifestLine ORDER BY Id FOR JSON PATH, INCLUDE_NULL_VALUES),
                    (SELECT * FROM dbo.SaleDelivery ORDER BY Id FOR JSON PATH, INCLUDE_NULL_VALUES),
                    (SELECT * FROM dbo.LedgerEffect ORDER BY Id FOR JSON PATH, INCLUDE_NULL_VALUES),
                    (SELECT * FROM dbo.ReceivableOpenItem ORDER BY Id FOR JSON PATH, INCLUDE_NULL_VALUES))),2) AS Fingerprint
            FROM dbo.PhysicalStockBalance b JOIN dbo.StockLocation l ON l.TenantId=b.TenantId AND l.Id=b.StockLocationId
            """);
    }

    private async Task<object> TraceOrder(string tenant, Guid order)
    {
        await using var db = await ReadOnly(run, tenant);
        var confirmation = await db.QuerySingleOrDefaultAsync("""
            SELECT c.OrderId,c.CustomerId,c.SelectionId,c.ConfirmSourceId,c.ShipToSnapshotId,
                CONVERT(varchar(64),s.PayloadHash,2) AS ShipToHash,
                (SELECT COUNT(*) FROM dbo.C1Outlet WHERE CustomerId=c.CustomerId) AS OutletCount
            FROM dbo.C2Confirmation c JOIN dbo.C1ShipToSelection s ON s.Id=c.SelectionId AND s.TenantId=c.TenantId
            WHERE c.OrderId=@order
            """, new { order });
        var deliveries = await db.QueryAsync("""
            SELECT d.Id,d.BusinessSourceId,d.BaseQuantity,d.BusinessDate,d.CostAmount,d.PayableAmount,
                a.Id AS ArId,a.CustomerId AS DebtorId,a.OriginalAmount,a.DueBusinessDate,e.OccurredAtUtc,
                d.ShipToSnapshotId,i.Id AS StockIssueId
            FROM dbo.SaleDelivery d JOIN dbo.ReceivableOpenItem a ON a.SaleDeliveryId=d.Id AND a.TenantId=d.TenantId
            JOIN dbo.C4DeliveryEvidence e ON e.Id=d.C4EvidenceId AND e.TenantId=d.TenantId
            JOIN dbo.DeliveryStockIssue i ON i.SaleDeliveryId=d.Id AND i.TenantId=d.TenantId
            WHERE d.AOrderId=@order ORDER BY e.OccurredAtUtc,d.Id
            """, new { order });
        var policy = await db.QuerySingleOrDefaultAsync("""
            SELECT p.PolicyVersion AS Version,l.TermsDaysSnapshot AS TermsDays,CONVERT(varchar(64),p.SourceHash,2) AS PolicyHash,
                (SELECT COALESCE(SUM(r.GrantedBaseQuantity-r.ConsumedBaseQuantity-r.ReleasedBaseQuantity),0)
                    FROM dbo.AReservation a JOIN dbo.StockRight r ON r.Id=a.StockRightId AND r.TenantId=a.TenantId
                    WHERE a.AOrderLineId=l.AOrderLineId AND a.TenantId=l.TenantId) AS Reserved
            FROM dbo.AConfirmedLineSnapshot l JOIN dbo.C2LinePolicy p ON p.OrderLineId=l.AOrderLineId AND p.TenantId=l.TenantId
            WHERE l.AOrderId=@order
            """, new { order });
        return new { order, confirmation, policy, deliveries };
    }

    private static async Task WriteJson(string path, object value)
    {
        string staging = path + ".next";
        await File.WriteAllTextAsync(staging, JsonSerializer.Serialize(value, Json));
        File.Move(staging, path, overwrite: true);
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    [GeneratedRegex(@"^DEV-(0[1-9]|1[01])$")]
    private static partial Regex CaseName();

    [GeneratedRegex(@"^[a-z0-9-]{1,64}$")]
    private static partial Regex PhaseName();
}
