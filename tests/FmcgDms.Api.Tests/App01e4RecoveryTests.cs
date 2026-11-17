using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Dapper;
using FmcgDms.Api.Persistence;
using Microsoft.Data.SqlClient;
using Xunit;

namespace FmcgDms.Api.Tests;

/// <summary>
/// APP-01E.4: tests durable outcome discovery across every command family,
/// plus genuine TCP lost ACK + two separate OS Kestrel processes on two
/// nonce-owned LocalDB tenants. Extends E.2/E.3's independently checked SQL
/// snapshots, source lineage and 23-cell financial/physical/right probe.
/// </summary>
public sealed partial class App01e2CrossCommandRaceTests
{
    private const string E4Producer = "app01e4-recovery";
    private const string RecoveryRoute = "/api/synthetic/app01e/commands/outcome";

    public static IEnumerable<object[]> E4Cases()
    {
        foreach (string scenario in new[]
        {
            "reserve", "quota", "confirm", "credit", "manifest", "b20", "a25",
            "offer", "accept", "unload", "reject", "b31"
        })
        {
            yield return ["tenant-a", scenario];
            yield return ["tenant-b", scenario];
        }
    }

    public static IEnumerable<object[]> E4ProcessCases()
    {
        foreach (string scenario in new[] { "quota", "b20", "accept", "unload", "reject" })
        {
            yield return ["tenant-a", scenario];
            yield return ["tenant-b", scenario];
        }
    }

    private static IApp01aCommand E4Identity(IApp01aCommand c) => c switch
    {
        ReserveAStockCommand x => x with { ProducerId = E4Producer },
        GrantBQuotaCommand x => x with { ProducerId = E4Producer },
        ConfirmAOrderCommand x => x with { ProducerId = E4Producer },
        GrantBCreditCommand x => x with { ProducerId = E4Producer },
        LoadManifestCommand x => x with { ProducerId = E4Producer },
        PostBSaleDeliveryReceiptCommand x => x with { ProducerId = E4Producer },
        PostAPartialDeliveryCommand x => x with { ProducerId = E4Producer },
        OfferCashHandoverCommand x => x with { ProducerId = E4Producer },
        AcceptCashHandoverCommand x => x with { ProducerId = E4Producer },
        UnloadTripRemainderCommand x => x with { ProducerId = E4Producer },
        _ => throw new InvalidOperationException("APP01E4 unknown command.")
    };

    private async Task<Request> E4Request(TestApiFactory factory,
        HttpClient setup, string tenant, string scenario, Scene scene)
    {
        if (scenario == "b31")
        {
            await Prepare(setup, tenant, scene, load: true);
            return Req(B + "preflight-b-sale",
                new PreflightBSaleCommand(E4Producer, Id(), Id(),
                    scene.Trip, scene.Sku, 31m, 15m, 465m),
                "sales", SalesActor, "", 0, 0);
        }

        Request original = await E3Request(factory, setup, tenant,
            scenario == "reject" ? "quota" : scenario, scene);
        IApp01aCommand payload = E4Identity(original.Payload);
        if (scenario == "reject")
            return original with
            {
                Payload = ((GrantBQuotaCommand)payload) with { BaseQuantity = -1m },
                SourceKind = "", Lines = 0, Effects = 0
            };
        return original with { Payload = payload };
    }

    private static string LookupPath(IApp01aCommand c) =>
        RecoveryRoute + "?producerId=" + Uri.EscapeDataString(c.ProducerId)
        + "&commandId=" + Uri.EscapeDataString(c.CommandId);

    private static async Task<BusinessCommandRecovery> Lookup(
        HttpClient client, IApp01aCommand command)
    {
        using var response = await client.GetAsync(
            LookupPath(command), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return Assert.IsType<BusinessCommandRecovery>(
            await response.Content.ReadFromJsonAsync<BusinessCommandRecovery>(
                TestContext.Current.CancellationToken));
    }

    private static void Unknown(BusinessCommandRecovery result, string reason = "NOT_FOUND")
    {
        Assert.Equal("UNKNOWN", result.State);
        Assert.Null(result.Status);
        Assert.Null(result.OutcomeCode);
        Assert.Equal(reason, result.ReasonCode);
        Assert.Null(result.SourceId);
    }

    private static void AssertRecovered(Scene scene, Request req,
        SqlAudit sql, BusinessCommandRecovery result)
    {
        Assert.Equal("COMPLETED", result.State);
        Assert.Equal(sql.Status, result.Status);
        Assert.Equal(sql.OutcomeCode, result.OutcomeCode);
        Assert.Equal(sql.ReasonCode, result.ReasonCode);
        Assert.Equal(sql.SourceId, result.SourceId);
        Assert.Equal(req.Actor, sql.CommandActor);
        if (req.SourceKind.Length == 0)
        {
            Assert.Equal("REJECTED", result.Status);
            bool isQuotaPreflight = req.Payload is PreflightBSaleCommand;
            Assert.Equal(isQuotaPreflight ? "REJECTED_QUOTA" : "REJECTED", result.OutcomeCode);
            Assert.Equal(isQuotaPreflight ? "B_QUOTA_INSUFFICIENT" : "QUANTITY_INVALID", result.ReasonCode);
            Assert.Null(result.SourceId);
            Assert.Null(sql.SourceNumber);
            Assert.Equal(0, sql.Lines);
            Assert.Equal(0, sql.Effects);
        }
        else
        {
            Assert.Equal("APPLIED", result.Status);
            Assert.Null(result.ReasonCode);
            Assert.NotNull(result.SourceId);
            Assert.Equal(req.SourceKind, sql.SourceKind);
            Assert.Equal(req.Payload.SourceNumber, sql.SourceNumber);
            Assert.Equal(req.Actor, sql.SourceActor);
            Assert.Equal<Guid?>(scene.Trip, sql.SourceTrip);
            Assert.Equal(req.Lines, sql.Lines);
            Assert.Equal(req.Effects, sql.Effects);
        }
    }

    [Theory]
    [MemberData(nameof(E4Cases))]
    public async Task Lost_ack_after_commit_is_lookup_recoverable_on_fresh_server_factory(
        string tenant, string scenario)
    {
        Scene scene = Seed(tenant);
        Request req;
        SqlSnapshot committed;
        FullSqlState fingerprint;
        // E.1 handler is deliberately in-process here; the separate E.4
        // process matrix below verifies actual TCP RST and OS restart.
        using (var factory = fixture.CreateFactory())
        {
            using var setup = SqlScratchFixture.WithTenant(factory.CreateClient(), tenant);
            req = await E4Request(factory, setup, tenant, scenario, scene);
            using var caller = Client(factory, tenant, req.Actor, req.Role,
                new AfterServerAckLossHandler(req.Path));
            Unknown(await Lookup(caller, req.Payload));
            await Assert.ThrowsAsync<HttpRequestException>(() =>
                Send(caller, req.Path, req.Payload));
            committed = ReadSql(tenant, scene);
            fingerprint = await FullSql(tenant);
        }

        using var restartedFactory = fixture.CreateFactory();
        using var restartClient = Client(restartedFactory, tenant, req.Actor, req.Role);
        SqlAudit audit = Audit(tenant, req.Payload);
        var result = await Lookup(restartClient, req.Payload);
        AssertRecovered(scene, req, audit, result);
        Assert.Equal(fingerprint.Sha256, (await FullSql(tenant)).Sha256);

        // GET is read-only; a same-identity POST is a replay, not a new source.
        Reply replay = await Send(restartClient, req.Path, req.Payload);
        Assert.Equal(HttpStatusCode.OK, replay.HttpStatus);
        Assert.True(replay.Outcome.Replayed);
        Assert.Equal(result.Status, replay.Outcome.Status);
        Assert.Equal(result.OutcomeCode, replay.Outcome.OutcomeCode);
        Assert.Equal(result.ReasonCode, replay.Outcome.ReasonCode);
        Assert.Equal(result.SourceId, replay.Outcome.SourceId);
        Assert.Equal(fingerprint.Sha256, (await FullSql(tenant)).Sha256);
        Assert.Equal(committed.Numbers.Cells().ToArray(), ReadSql(tenant, scene).Numbers.Cells().ToArray());
    }

    /// <summary>
    /// Real boundary: the reverse proxy reads the upstream Kestrel HTTP 200,
    /// sends zero response bytes to the caller and resets its TCP socket.
    /// The first Kestrel *process* exits, a different process starts against
    /// the same owned SQL DB, then read/POST conflict tests run over TCP.
    /// </summary>
    [Theory]
    [MemberData(nameof(E4ProcessCases))]
    public async Task Lost_TCP_ack_and_real_process_restart_restore_durable_outcome(
        string tenant, string scenario)
    {
        using var setupFactory = fixture.CreateFactory();
        using var setup = SqlScratchFixture.WithTenant(setupFactory.CreateClient(), tenant);
        Scene scene = Seed(tenant);
        Request req = await E4Request(setupFactory, setup, tenant, scenario, scene);
        SqlSnapshot before = ReadSql(tenant, scene);
        FullSqlState fullBefore = await FullSql(tenant);
        string transport;
        int firstPid;
        await using (var first = await App01e4ApiProcess.StartAsync(
            fixture, TestContext.Current.CancellationToken))
        {
            firstPid = first.Pid;
            using var firstClient = first.Client(tenant, req.Actor, req.Role);
            Unknown(await Lookup(firstClient, req.Payload));

            await using var proxy = new App01e4TcpAckDrop();
            using var lostClient = SqlScratchFixture.WithTenant(
                new HttpClient(new SocketsHttpHandler { UseProxy = false })
                {
                    BaseAddress = proxy.BaseAddress,
                    Timeout = TimeSpan.FromSeconds(30)
                }, tenant, req.Actor, [req.Role]);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(
                TestContext.Current.CancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(30));
            Task<string> reset = proxy.DropOneAsync(first.Port, deadline.Token);
            await Assert.ThrowsAsync<HttpRequestException>(() =>
                Send(lostClient, req.Path, req.Payload));
            transport = await reset;
            Assert.StartsWith("HTTP/1.1 200", transport);
        }

        SqlAudit sql = Audit(tenant, req.Payload);
        SqlSnapshot committed = ReadSql(tenant, scene);
        FullSqlState fullCommitted = await FullSql(tenant);
        Assert.NotEqual(fullBefore.Sha256, fullCommitted.Sha256);

        BusinessCommandRecovery recovered, hiddenActor, hiddenTenant, caseChangedActor, caseChangedProducer;
        Reply replay, conflict;
        FullSqlState fullReplayed, fullConflicted;
        SqlSnapshot replayed, conflicted;
        int secondPid;
        await using (var second = await App01e4ApiProcess.StartAsync(
            fixture, TestContext.Current.CancellationToken))
        {
            secondPid = second.Pid;
            using var owner = second.Client(tenant, req.Actor, req.Role);
            recovered = await Lookup(owner, req.Payload);
            AssertRecovered(scene, req, sql, recovered);
            Assert.Equal(fullCommitted.Sha256, (await FullSql(tenant)).Sha256);

            using var otherActor = second.Client(tenant, "e4-unrelated-actor", req.Role);
            hiddenActor = await Lookup(otherActor, req.Payload);
            Unknown(hiddenActor);
            using var changedActor = second.Client(tenant, req.Actor.ToUpperInvariant(), req.Role);
            caseChangedActor = await Lookup(changedActor, req.Payload);
            Unknown(caseChangedActor);
            caseChangedProducer = await Lookup(owner,
                E4Identity(req.Payload) switch
                {
                    ReserveAStockCommand x => x with { ProducerId = E4Producer.ToUpperInvariant() },
                    GrantBQuotaCommand x => x with { ProducerId = E4Producer.ToUpperInvariant() },
                    ConfirmAOrderCommand x => x with { ProducerId = E4Producer.ToUpperInvariant() },
                    GrantBCreditCommand x => x with { ProducerId = E4Producer.ToUpperInvariant() },
                    LoadManifestCommand x => x with { ProducerId = E4Producer.ToUpperInvariant() },
                    PostBSaleDeliveryReceiptCommand x => x with { ProducerId = E4Producer.ToUpperInvariant() },
                    PostAPartialDeliveryCommand x => x with { ProducerId = E4Producer.ToUpperInvariant() },
                    OfferCashHandoverCommand x => x with { ProducerId = E4Producer.ToUpperInvariant() },
                    AcceptCashHandoverCommand x => x with { ProducerId = E4Producer.ToUpperInvariant() },
                    UnloadTripRemainderCommand x => x with { ProducerId = E4Producer.ToUpperInvariant() },
                    _ => throw new InvalidOperationException()
                });
            Unknown(caseChangedProducer);
            using var otherTenant = second.Client(
                tenant == "tenant-a" ? "tenant-b" : "tenant-a", req.Actor, req.Role);
            hiddenTenant = await Lookup(otherTenant, req.Payload);
            Unknown(hiddenTenant);
            Assert.Equal(fullCommitted.Sha256, (await FullSql(tenant)).Sha256);

            replay = await Send(owner, req.Path, req.Payload);
            Assert.Equal(HttpStatusCode.OK, replay.HttpStatus);
            Assert.True(replay.Outcome.Replayed);
            Assert.Equal(recovered.Status, replay.Outcome.Status);
            Assert.Equal(recovered.OutcomeCode, replay.Outcome.OutcomeCode);
            Assert.Equal(recovered.ReasonCode, replay.Outcome.ReasonCode);
            Assert.Equal(recovered.SourceId, replay.Outcome.SourceId);
            replayed = ReadSql(tenant, scene);
            fullReplayed = await FullSql(tenant);
            Assert.Equal(fullCommitted.Sha256, fullReplayed.Sha256);

            conflict = await Send(owner, req.Path, DifferentSource(req.Payload));
            Assert.Equal(HttpStatusCode.Conflict, conflict.HttpStatus);
            Assert.Equal("CONFLICT", conflict.Outcome.Status);
            Assert.Equal("IDEMPOTENCY_PAYLOAD_MISMATCH", conflict.Outcome.ReasonCode);
            conflicted = ReadSql(tenant, scene);
            fullConflicted = await FullSql(tenant);
            Assert.Equal(fullCommitted.Sha256, fullConflicted.Sha256);
        }
        Assert.NotEqual(firstPid, secondPid);
        Assert.Equal(committed.Numbers.Cells().ToArray(), replayed.Numbers.Cells().ToArray());
        Assert.Equal(committed.Numbers.Cells().ToArray(), conflicted.Numbers.Cells().ToArray());
        Assert.Equal(committed.LedgerSha256, replayed.LedgerSha256);
        Assert.Equal(committed.LedgerSha256, conflicted.LedgerSha256);
        WriteE4Evidence(tenant, scenario, req, sql, recovered, hiddenActor,
            hiddenTenant, caseChangedActor, caseChangedProducer,
            transport, firstPid, secondPid,
            fullBefore, fullCommitted, fullReplayed, fullConflicted,
            before, committed, replayed, conflicted, replay, conflict);
    }

    private static void WriteE4Evidence(
        string tenant, string scenario, Request req, SqlAudit sql,
        BusinessCommandRecovery recovered, BusinessCommandRecovery hiddenActor,
        BusinessCommandRecovery hiddenTenant, BusinessCommandRecovery caseChangedActor,
        BusinessCommandRecovery caseChangedProducer, string transport, int firstPid, int secondPid,
        FullSqlState before, FullSqlState committed, FullSqlState replayed,
        FullSqlState conflicted, SqlSnapshot initial, SqlSnapshot posted,
        SqlSnapshot replaySql, SqlSnapshot conflictSql, Reply replay, Reply conflict)
    {
        Cell[] cells =
        [
            ..NumberDiffs("REPLAY", posted, replaySql),
            ..NumberDiffs("CONFLICT", posted, conflictSql)
        ];
        bool passed = cells.Length == 46 && cells.All(x => x.Match)
            && transport.StartsWith("HTTP/1.1 200", StringComparison.Ordinal)
            && firstPid != secondPid
            && before.Sha256 != committed.Sha256
            && committed.Sha256 == replayed.Sha256
            && committed.Sha256 == conflicted.Sha256
            && recovered.State == "COMPLETED"
            && recovered.Status == sql.Status
            && recovered.SourceId == sql.SourceId
            && hiddenActor.State == "UNKNOWN" && hiddenTenant.State == "UNKNOWN"
            && caseChangedActor.State == "UNKNOWN"
            && caseChangedProducer.State == "UNKNOWN"
            && replay.Outcome.Replayed && conflict.HttpStatus == HttpStatusCode.Conflict;
        var report = new
        {
            Schema = "APP01E4_RECOVERY_V1",
            TestRunId = Environment.GetEnvironmentVariable("APP01E4_TEST_RUN_ID") ?? "LOCAL_UNTRACKED",
            SourceHeadSha = Environment.GetEnvironmentVariable("APP01E4_SOURCE_HEAD_SHA"),
            WorkflowSha = Environment.GetEnvironmentVariable("GITHUB_SHA"),
            TenantId = tenant, Database = committed.Database, Scenario = scenario,
            ProducerId = req.Payload.ProducerId, CommandId = req.Payload.CommandId,
            SourceNumber = req.Payload.SourceNumber, ActorId = req.Actor,
            ExpectedSourceKind = req.SourceKind, ExpectedLines = req.Lines,
            ExpectedEffects = req.Effects,
            Transport = "TCP_RST_AFTER_UPSTREAM_RESPONSE_NO_DOWNSTREAM_ACK",
            UpstreamStatus = transport,
            Restart = "TWO_DISTINCT_OS_PROCESSES",
            FirstProcessId = firstPid, SecondProcessId = secondPid,
            OutcomeLookup = recovered, OtherActorLookup = hiddenActor,
            OtherTenantLookup = hiddenTenant,
            CaseChangedActorLookup = caseChangedActor,
            CaseChangedProducerLookup = caseChangedProducer,
            Sql = sql, Initial = initial, Posted = posted,
            ReplaySql = replaySql, ConflictSql = conflictSql,
            Before = before, Committed = committed, Replayed = replayed, Conflicted = conflicted,
            Replay = replay.Outcome, Conflict = conflict.Outcome,
            ExpectedCells = cells.Length, MatchedCells = cells.Count(x => x.Match),
            Cells = cells, Status = passed ? "PASS" : "FAIL"
        };
        string folder = Path.Combine(AppContext.BaseDirectory, "TestResults", "APP-01E4");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, $"{tenant}-{scenario}-recovery.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false));
        Assert.True(passed, "APP-01E.4 durable lost-ACK recovery SQL invariants failed.");
    }

    [Fact]
    public async Task Outcome_lookup_fails_closed_for_identity_auth_role_and_nonterminal_state()
    {
        const string tenant = "tenant-a";
        using var factory = fixture.CreateFactory();
        using var owner = Client(factory, tenant, WarehouseActor, "warehouse");
        var command = new GrantBQuotaCommand(E4Producer, Id(), Id(),
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 30m);
        Unknown(await Lookup(owner, command));

        using var anonymous = factory.CreateClient();
        using var unauthorized = await anonymous.GetAsync(LookupPath(command), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        using var auditor = Client(factory, tenant, WarehouseActor, "auditor");
        using var forbidden = await auditor.GetAsync(LookupPath(command), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);

        foreach (string path in new[]
        {
            RecoveryRoute, RecoveryRoute + "?producerId=",
            RecoveryRoute + "?producerId=x&commandId=y&commandId=z",
            RecoveryRoute + "?producerId=x&producerId=y&commandId=z",
            RecoveryRoute + "?producerId=" + new string('x', 129) + "&commandId=y"
        })
        {
            using var invalid = await owner.GetAsync(path, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        }

        // The read-only lookup must not promote an incomplete persisted row
        // to an APPLIED/REJECTED answer even on an owned LocalDB scratch DB.
        string key = Id();
        Guid row = Guid.NewGuid();
        await using (var db = new SqlConnection(Connection(tenant)))
        {
            await db.OpenAsync(TestContext.Current.CancellationToken);
            await db.ExecuteAsync("""
                INSERT dbo.BusinessCommand(Id,TenantId,ProducerId,CommandId,
                    PayloadHashVersion,PayloadHash,ActorId,Status)
                VALUES(@Id,@Tenant,@Producer,@Command,1,
                    CONVERT(binary(32),HASHBYTES('SHA2_256',N'e4-nonterminal')),
                    @Actor,N'RECEIVED')
                """, new { Id = row, Tenant = tenant, Producer = E4Producer,
                    Command = key, Actor = WarehouseActor }, commandTimeout: 30);
        }
        try
        {
            Unknown(await Lookup(owner,
                command with { CommandId = key }), "NOT_FINAL");
        }
        finally
        {
            await using var db = new SqlConnection(Connection(tenant));
            await db.ExecuteAsync("""
                DELETE FROM dbo.BusinessCommand
                WHERE Id=@Id AND TenantId=@Tenant AND Status=N'RECEIVED'
                """, new { Id = row, Tenant = tenant });
        }

        using var disabled = fixture.CreateFactory(syntheticEnabled: false);
        using var disabledClient = disabled.CreateClient();
        using var absent = await disabledClient.GetAsync(LookupPath(command), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, absent.StatusCode);
        using var production = fixture.CreateFactory(syntheticEnabled: false,
            environment: "Production");
        using var prodClient = production.CreateClient();
        using var notMapped = await prodClient.GetAsync(LookupPath(command), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, notMapped.StatusCode);
    }
}
