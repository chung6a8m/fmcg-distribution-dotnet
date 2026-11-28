using System.Data;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using FmcgDms.Api.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FmcgDms.Api.Tests;

public sealed partial class App01e2CrossCommandRaceTests
{
    // A real SQL transaction, not a timing guess or fabricated SQL1205,
    // holds precisely the shared receipt domain. Both production paths
    // must enqueue on it, before they can touch receipt ranges. The
    // pre-remediation runtime cannot satisfy this directed wait assertion.
    [Theory]
    [InlineData("tenant-a")]
    [InlineData("tenant-b")]
    public async Task Rel756c_receipt_gate_fences_intake_and_processing_without_cross_tenant_blocking(string tenant)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using var isolated = new SqlScratchFixture();
        await isolated.InitializeAsync();
        var test = new App01e2CrossCommandRaceTests(isolated);
        string peer = tenant == "tenant-a" ? "tenant-b" : "tenant-a";
        using var factory = isolated.CreateFactory();
        using var setup = SqlScratchFixture.WithTenant(factory.CreateClient(), tenant);
        using var sales = Client(factory, tenant, SalesActor, "sales");
        using var cashier = Client(factory, tenant, CashierActor, "cashier");
        using var peerSales = Client(factory, peer, SalesActor, "sales");
        var scene = await test.SetupA3(tenant, setup, sales);
        var p = NewPEvent(scene.Scene.Trip, scene.Offer);
        var c = NewCEvent(scene.Scene.Trip, p, scene.Count);
        Guid receipt = Assert.IsType<Guid>((await IntakeA3(cashier, c)).ReceiptId);
        string peerBefore = (await test.FullSql(peer)).Sha256;
        using var scope = factory.Services.CreateScope();
        var processor = scope.ServiceProvider.GetRequiredService<App02a3ProcessingService>();
        var outbound = scope.ServiceProvider.GetRequiredService<App02a3OutboxService>();

        using var db = test.Open(tenant);
        using var observer = test.Open(tenant);
        // Open() constructs a closed SqlConnection; keep both SQL sessions
        // open across the held transaction and DMV observations.
        await db.OpenAsync(ct);
        await observer.OpenAsync(ct);
        await using var holder = (SqlTransaction)await db.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        // Independent literal key (not the implementation helper). This
        // connection holds no receipt, role, Trip or other business locks.
        string key = "APP02:RECEIPT:" + tenant;
        int owner = await db.ExecuteScalarAsync<int>(new CommandDefinition("SELECT @@SPID", transaction: holder, cancellationToken: ct));
        int acquired = await db.ExecuteScalarAsync<int>(new CommandDefinition("""
            DECLARE @result int;
            EXEC @result=sys.sp_getapplock @Resource=@Key,@LockMode='Exclusive',
                @LockOwner='Transaction',@LockTimeout=10000;
            SELECT @result;
            """, new { Key = key }, holder, cancellationToken: ct));
        Assert.True(acquired >= 0);
        Task<App02IntakeResult>? intake = null;
        Task<string>? processing = null;
        int[] pWait = [], bothWait = [];
        var work = new List<Task>();
        try
        {
            intake = IntakeA3(sales, p);
            work.Add(intake);
            pWait = await Rel756cWaitersAsync(observer, owner, key, 1, ct);
            processing = processor.ProcessAsync(tenant, receipt, DateTimeOffset.UtcNow, ct);
            work.Add(processing);
            bothWait = await Rel756cWaitersAsync(observer, owner, key, 2, ct);
            Assert.Contains(pWait[0], bothWait);
            Assert.False(intake.IsCompleted);
            Assert.False(processing.IsCompleted);
            // Actual peer endpoint acquires its own receipt gate while this
            // tenant is blocked. Unknown receipt must remain indistinguishable.
            var peerResult = await peerSales.GetFromJsonAsync<App02OutcomeResult>(
                A3 + "outcome?producerId=" + Uri.EscapeDataString(SalesActor)
                + "&eventId=" + Uri.EscapeDataString(p.EventId), ct);
            Assert.Equal("UNKNOWN", peerResult?.Status);
            Assert.False(intake.IsCompleted);
            Assert.False(processing.IsCompleted);
        }
        finally
        {
            // Transaction-owned lock is released by rollback; drain started
            // tasks even if the original assertion fails, before scratch cleanup.
            await holder.RollbackAsync(CancellationToken.None);
            try { await Task.WhenAll(work); }
            catch { /* awaited again below on the successful assertion path */ }
        }

        var ack = await intake!;
        Assert.False(ack.Replayed);
        Assert.Equal("APPLIED", ack.Status);
        string result = await processing!;
        Assert.Contains(result, new[] { "PENDING_DEPENDENCY", "APPLIED" });
        // Model the normal event wake after committed P, not a retry of a
        // failed assertion/command. The original zero-retry oracle stays exact.
        await processor.SweepTenantAsync(tenant, DateTimeOffset.UtcNow, true, ct);
        Guid message = observer.QuerySingle<Guid>("""
            SELECT MessageId FROM dbo.App02AcceptanceApplication
            WHERE TenantId=@Tenant AND CReceiptId=@Receipt
            """, new { Tenant = tenant, Receipt = receipt });
        Assert.Equal("ACKED", await outbound.DispatchOneAsync(tenant, DateTimeOffset.UtcNow,
            stopAfterConsumerCommit: false, ct, onlyMessageId: message));
        decimal[] actual = test.A4ReadSql(tenant, scene.Scene.Trip).Values();
        using var expectedFixture = JsonDocument.Parse(File.ReadAllBytes(A4FixturePath()));
        decimal[] expected = expectedFixture.RootElement.GetProperty("states").GetProperty("acked")
            .EnumerateArray().Select(x => x.GetDecimal()).ToArray();
        Assert.Equal(expected, actual);
        string peerAfter = (await test.FullSql(peer)).Sha256;
        Assert.Equal(peerBefore, peerAfter);
        await FailureDiagnostics.WriteAsync(AppContext.BaseDirectory, $"rel756c-{tenant}-gate",
            new
            {
                schema = "REL756C_RECEIPT_GATE_V1",
                tenant, fixtureNonce = isolated.Nonce, tripId = scene.Scene.Trip, cReceiptId = receipt,
                sourceHeadSha = FailureDiagnostics.Sha(Environment.GetEnvironmentVariable("APP02A4_SOURCE_HEAD_SHA") ?? "LOCAL") ?? "INVALID",
                workflowSha = FailureDiagnostics.Sha(Environment.GetEnvironmentVariable("GITHUB_SHA") ?? "LOCAL") ?? "INVALID",
                testRunId = "rel756c-" + (Environment.GetEnvironmentVariable("GITHUB_RUN_ID") ?? "local")
                    + "-" + (Environment.GetEnvironmentVariable("GITHUB_RUN_ATTEMPT") ?? "local"),
                holderSpid = owner, intakeWaiter = pWait[0], processingWaiter = bothWait.Single(x => x != pWait[0]),
                gate = key, releasedBy = "ROLLBACK", peerLookup = "UNKNOWN",
                peerBefore, peerAfter, initialProcessing = result, ack = ack.Ack,
                oracleChecked = expected.Length, oracleMatched = actual.Zip(expected).Count(x => x.First == x.Second),
                metrics = A4Columns, expected, actual
            }, ct);
    }

    private static async Task<int[]> Rel756cWaitersAsync(SqlConnection db, int owner,
        string key, int expected, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(3))
        {
            int[] waiting = (await db.QueryAsync<int>(new CommandDefinition("""
                SELECT DISTINCT CONVERT(int,w.request_session_id)
                FROM sys.dm_tran_locks w
                JOIN sys.dm_tran_locks g ON g.resource_type=w.resource_type
                  AND g.resource_database_id=w.resource_database_id
                  AND g.resource_associated_entity_id=w.resource_associated_entity_id
                  AND g.resource_description=w.resource_description
                WHERE g.request_session_id=@Owner AND w.request_session_id<>@Owner
                  AND w.resource_type=N'APPLICATION' AND w.resource_database_id=DB_ID()
                  AND CHARINDEX(@Key,w.resource_description)>0
                  AND w.request_owner_type=N'TRANSACTION' AND w.request_mode=N'X'
                  AND w.request_status=N'WAIT'
                  AND g.request_owner_type=N'TRANSACTION' AND g.request_mode=N'X'
                  AND g.request_status=N'GRANT'
                """, new { Owner = owner, Key = key }, commandTimeout: 2, cancellationToken: ct))).ToArray();
            if (waiting.Length == expected) return waiting;
            await Task.Delay(50, ct);
        }
        throw new InvalidOperationException("REL756C expected distinct real SQL receipt-domain waiters");
    }
}
