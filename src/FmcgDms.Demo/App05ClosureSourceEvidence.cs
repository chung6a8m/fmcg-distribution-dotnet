using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;

namespace FmcgDms.Demo;

internal sealed class App05ClosureSourceEvidence(DemoRun run, string folder, CancellationToken ct,
    Func<string, Task, Task<bool>> wait, Func<string, Task> acknowledge, Func<bool, Task<string>> digest)
{
    private const string Tenant = "tenant-a";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly List<Comparison> checks = [];

    internal async Task<bool> VerifyPendingDelivery(Task exited)
    {
        if (!await wait("arm-delivery-post-fault", exited)) return false;
        var input = await Read<DeliveryProbe>("arm-delivery-post-fault");
        string before = await digest(false);
        await using var db = await OwnedDatabase();
        var beforeTrip = await ReadTrip(input.TripId);
        await SetFault(db, input, true);
        await acknowledge("arm-delivery-post-fault");

        if (!await wait("received-delivery-blocked", exited)) return false;
        var evidence = (await Read<DeliveryEvidence>("received-delivery-blocked")).EvidenceId;
        Check("C.4 intake persisted before interrupted posting", new DeliveryState("RECEIVED", 0, 0, 0, 0),
            await DeliveryStateFor(db, input, evidence));
        await UncommittedEffects(input.TripId, beforeTrip, before, includeIntakeEvidence: false, "real C.4 RECEIVED blocks D.3 and close");
        await SetIntakeState(db, input, evidence, "RECEIVED", "PENDING");
        await acknowledge("received-delivery-blocked");

        if (!await wait("pending-delivery-blocked", exited)) return false;
        Check("guarded PENDING variant remains on the same real intake", evidence,
            (await Read<DeliveryEvidence>("pending-delivery-blocked")).EvidenceId);
        Check("simulated PENDING intake has no posting", new DeliveryState("PENDING", 0, 0, 0, 0),
            await DeliveryStateFor(db, input, evidence));
        await UncommittedEffects(input.TripId, beforeTrip, before, includeIntakeEvidence: false, "C.4 PENDING fault variant blocks D.3 and close");
        await SetIntakeState(db, input, evidence, "PENDING", "RECEIVED");
        await SetFault(db, input, false);
        await acknowledge("pending-delivery-blocked");

        if (!await wait("delivery-resolved", exited)) return false;
        var resolved = await Read<DeliveryResolution>("delivery-resolved");
        Check("same exact C.4 evidence resolved by normal command retry", evidence, resolved.EvidenceId);
        Check("only one posting after C.4 recovery and replay", new DeliveryState("APPLIED", 1, 25, 250000, 75000),
            await DeliveryStateFor(db, input, evidence));
        Check("recovered receipt links the exact persisted delivery source", 1,
            await db.ExecuteScalarAsync<int>(new CommandDefinition("""
                SELECT COUNT(*) FROM dbo.SaleDelivery WHERE TenantId=@Tenant AND TripId=@TripId AND C4EvidenceId=@EvidenceId
                    AND BusinessSourceId=@SourceId;
                """, new { Tenant, input.TripId, resolved.EvidenceId, resolved.SourceId }, cancellationToken: ct)));
        await acknowledge("delivery-resolved");
        return true;
    }

    internal async Task<bool> VerifySourceRaceAndCash(Task exited, Guid tripId)
    {
        await using var db = await OwnedDatabase();
        if (!await wait("before-custody-probes", exited)) return false;
        var custody = await Read<SaleCustody>("before-custody-probes");
        Check("day-three custody belongs to the prepared trip", tripId, custody.TripId);
        Check("exact new B cash receipt before any offer", 1, await db.ExecuteScalarAsync<int>(new CommandDefinition("""
            SELECT COUNT(*) FROM dbo.SaleDelivery d JOIN dbo.CashReceipt c ON c.TenantId=d.TenantId AND c.SaleDeliveryId=d.Id
            WHERE d.TenantId=@Tenant AND d.TripId=@TripId AND d.BusinessSourceId=@SaleSource AND c.ReceivedAmount=@Amount
                AND NOT EXISTS(SELECT 1 FROM dbo.CashHandoverOffer o WHERE o.TenantId=d.TenantId AND o.TripId=d.TripId);
            """, new { Tenant, custody.TripId, custody.SaleSource, custody.Amount }, cancellationToken: ct)));
        var custodyTrip = await ReadTrip(tripId);
        string beforeCustody = await digest(true);
        await acknowledge("before-custody-probes");

        if (!await wait("custody-blocked", exited)) return false;
        await UncommittedEffects(tripId, custodyTrip, beforeCustody, includeIntakeEvidence: true, "custody blocks D.3 and close");
        await acknowledge("custody-blocked");

        if (!await wait("before-offer-probes", exited)) return false;
        var offer = await Read<Offer>("before-offer-probes");
        Check("exact cash offer in flight", 1, await db.ExecuteScalarAsync<int>(new CommandDefinition("""
            SELECT COUNT(*) FROM dbo.CashHandoverOffer o
            WHERE o.TenantId=@Tenant AND o.TripId=@TripId AND o.Id=@OfferId AND o.OfferedAmount=@Amount
                AND NOT EXISTS(SELECT 1 FROM dbo.CashHandoverAcceptance a WHERE a.TenantId=o.TenantId AND a.OfferId=o.Id);
            """, new { Tenant, custody.TripId, custody.Amount, offer.OfferId }, cancellationToken: ct)));
        var offeredTrip = await ReadTrip(tripId);
        string beforeOffer = await digest(true);
        await acknowledge("before-offer-probes");

        if (!await wait("offer-blocked", exited)) return false;
        await UncommittedEffects(tripId, offeredTrip, beforeOffer, includeIntakeEvidence: true, "pending cash offer blocks D.3 and close");
        await acknowledge("offer-blocked");

        if (!await wait("arm-accounting-source-race", exited)) return false;
        var race = await Read<AccountingRace>("arm-accounting-source-race");
        Check("race trip matches owned day-three fixture", tripId, race.TripId);
        Check("exact D.4 receipt exists while posting is absent", new SourceProgress(1, 0),
            await AccountingProgress(db, race.Work));
        Check("cashier acceptance resolved the tested source", 1, await db.ExecuteScalarAsync<int>(new CommandDefinition("""
            SELECT COUNT(*) FROM dbo.CashHandoverAcceptance a
            WHERE a.TenantId=@Tenant AND a.TripId=@TripId AND a.OfferId=@OfferId AND a.AcceptedAmount=@Amount;
            """, new { Tenant, custody.TripId, custody.Amount, offer.OfferId }, cancellationToken: ct)));
        string beforeRace = await digest(true);

        // The held app lock is the same transaction-owned resource required by Close. D.4 has an independent
        // partner transaction, so its committed source change can be forced into the interval before Close reads.
        await using (var tx = (SqlTransaction)await db.BeginTransactionAsync(ct))
        {
            int lockResult = await db.ExecuteScalarAsync<int>(new CommandDefinition("""
                DECLARE @Result int;
                EXEC @Result=sys.sp_getapplock @Resource=@Resource,@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=3000;
                SELECT @Result;
                """, new { Resource = $"APP01C:TRIP:{Tenant}:{tripId:N}" }, tx, cancellationToken: ct));
            if (lockResult < 0) throw new InvalidOperationException($"Unable to acquire the owned trip race lock: {lockResult}.");
            await acknowledge("arm-accounting-source-race");
            if (!await wait("competing-close-waits", exited)) return false;

            var waiting = Stopwatch.StartNew();
            bool inSql;
            do
            {
                inSql = await db.ExecuteScalarAsync<bool>(new CommandDefinition("""
                    SELECT CONVERT(bit,CASE WHEN EXISTS
                        (SELECT 1 FROM sys.dm_tran_locks ready
                        JOIN sys.dm_tran_locks waiter ON waiter.resource_type=ready.resource_type
                            AND waiter.resource_database_id=ready.resource_database_id
                            AND waiter.resource_description=ready.resource_description
                        WHERE ready.resource_type='APPLICATION' AND ready.resource_database_id=DB_ID()
                            AND ready.request_session_id=@@SPID AND ready.request_status='GRANT'
                            AND waiter.request_session_id<>@@SPID AND waiter.request_status='WAIT')
                        THEN 1 ELSE 0 END);
                    """, transaction: tx, cancellationToken: ct));
                if (!inSql) await Task.Delay(40, ct);
            } while (!inSql && !exited.IsCompleted && waiting.Elapsed < TimeSpan.FromSeconds(6));
            Check("competing Close is actually waiting on the trip lock", true, inSql);
            await acknowledge("competing-close-waits");

            if (!await wait("concurrent-accounting-posted", exited)) return false;
            var posted = await Read<Posting>("concurrent-accounting-posted");
            await using var independent = await OwnedDatabase();
            Check("D.4 posting committed while Close was still blocked", 1,
                await independent.ExecuteScalarAsync<int>(new CommandDefinition("""
                    SELECT COUNT(*) FROM d4accounting.Posting p
                    JOIN d4accounting.Receipt r ON r.Id=p.ReceiptId AND r.SourceLineId=p.SourceLineId
                    WHERE p.Id=@PostingId AND r.DeliveryId=@RelatedId AND p.SourceLineId=@SourceId AND p.Kind='BASE';
                    """, new { posted.PostingId, race.Work.RelatedId, race.Work.SourceId }, cancellationToken: ct)));
            await tx.CommitAsync(ct);
            await acknowledge("concurrent-accounting-posted");
        }

        if (!await wait("accounting-source-close-refused", exited)) return false;
        Check("stale close response persisted without creating a decision", 1, await db.ExecuteScalarAsync<int>(new CommandDefinition("""
            SELECT COUNT(*) FROM dbo.App05TripCloseRejection r WHERE r.TenantId=@Tenant AND r.TripId=@TripId
                AND r.CommandId=@CommandId AND r.Code='READINESS_CHANGED'
                AND NOT EXISTS(SELECT 1 FROM dbo.App05TripClosure c WHERE c.TenantId=r.TenantId AND c.TripId=r.TripId);
            """, new { Tenant, race.TripId, race.Input.CommandId }, cancellationToken: ct)));
        Check("stale Close and accounting posting preserve physical/financial app rows", beforeRace, await digest(true));
        await acknowledge("accounting-source-close-refused");

        if (!await wait("after-accounting-source-fresh-close", exited)) return false;
        var fresh = await Read<FreshClosure>("after-accounting-source-fresh-close");
        Check("only fresh Close stores the new source state", 1,
            await db.ExecuteScalarAsync<int>(new CommandDefinition("""
                SELECT COUNT(*) FROM dbo.App05TripClosure c
                JOIN dbo.Trip t ON t.TenantId=c.TenantId AND t.Id=c.TripId AND t.Status=c.Status
                CROSS APPLY OPENJSON(c.Snapshot,'$.work')
                    WITH (SourceId uniqueidentifier '$.sourceId', Label nvarchar(240) '$.label') item
                WHERE c.ClosureId=@ClosureId AND c.TenantId=@Tenant AND c.TripId=@TripId
                    AND c.Status='CLOSED_WITH_OPEN_CASES' AND c.OpenWorkCount=3 AND c.ExpectedRevision<>@ExpectedRevision
                    AND item.SourceId=@SourceId AND item.Label=N'Đối soát D.4 · Chờ cutoff';
                """, new { fresh.ClosureId, Tenant, race.TripId, race.Input.ExpectedRevision, race.Work.SourceId },
                cancellationToken: ct)));
        Check("fresh Close after race preserves physical/financial app rows", beforeRace, await digest(true));
        await acknowledge("after-accounting-source-fresh-close");
        return true;
    }

    internal Task WriteResults() => File.WriteAllTextAsync(Path.Combine(folder, "source-guards-sql.json"),
        JsonSerializer.Serialize(checks, Json));

    private async Task UncommittedEffects(Guid tripId, Unsettled baseline, string before, bool includeIntakeEvidence, string metric)
    {
        Check(metric + " without stock, AR, COGS or fund changes", before, await digest(includeIntakeEvidence));
        await using var db = await OwnedDatabase();
        Check(metric + " without status change, D.3 handoff or closure", new Unsettled(baseline.Status, 0, 0), await ReadTrip(tripId));
    }

    private async Task<Unsettled> ReadTrip(Guid tripId)
    {
        await using var db = await OwnedDatabase();
        return await db.QuerySingleAsync<Unsettled>(new CommandDefinition("""
            SELECT t.Status,
                (SELECT COUNT(*) FROM dbo.D3TripUnload u WHERE u.TenantId=t.TenantId AND u.TripId=t.Id) AS Handoffs,
                (SELECT COUNT(*) FROM dbo.App05TripClosure c WHERE c.TenantId=t.TenantId AND c.TripId=t.Id) AS Decisions
            FROM dbo.Trip t WHERE t.TenantId=@Tenant AND t.Id=@TripId;
            """, new { Tenant, tripId }, cancellationToken: ct));
    }

    private async Task SetFault(SqlConnection db, DeliveryProbe input, bool arm)
    {
        string statement = arm ? $"""
            CREATE TRIGGER dbo.TR_App05EvidenceFailDelivery ON dbo.SaleDelivery AFTER INSERT AS
            BEGIN
                SET NOCOUNT ON;
                IF EXISTS (SELECT 1 FROM inserted d JOIN dbo.C4DeliveryEvidence e ON e.TenantId=d.TenantId AND e.Id=d.C4EvidenceId
                    WHERE d.TenantId=N'tenant-a' AND d.TripId='{input.TripId:D}'
                      AND e.ManifestLineId='{input.ManifestLineId:D}' AND e.ClientCommandId='{input.CommandId:D}')
                    THROW 51914,'APP-05.2 synthetic posting fault after C.4 intake.',1;
            END;
            """ : "DISABLE TRIGGER dbo.TR_App05EvidenceFailDelivery ON dbo.SaleDelivery;";
        await db.ExecuteAsync(new CommandDefinition(OwnedMutationScope + """
            IF NOT EXISTS(SELECT 1 FROM dbo.TripManifestLine WHERE TenantId=@Tenant AND TripId=@TripId AND Id=@ManifestLineId)
                THROW 51915,'APP-05.2 evidence manifest scope mismatch.',1;
            EXEC(@Statement);
            """, new { Tenant, Name = run.Name(Tenant), Nonce = run.Id, input.TripId, input.ManifestLineId, Statement = statement },
            cancellationToken: ct));
    }

    private async Task SetIntakeState(SqlConnection db, DeliveryProbe input, Guid evidence, string from, string to)
    {
        int changed = await db.ExecuteAsync(new CommandDefinition(OwnedMutationScope + """
            UPDATE c SET Status=@To FROM dbo.BusinessCommand c
            JOIN dbo.C4DeliveryEvidence e ON e.TenantId=c.TenantId AND e.Id=c.Id
            WHERE c.TenantId=@Tenant AND c.Status=@From AND c.CompletedAtUtc IS NULL AND e.Id=@Evidence
                AND e.TripId=@TripId AND e.ManifestLineId=@ManifestLineId AND e.ClientCommandId=@CommandId
                AND NOT EXISTS (SELECT 1 FROM dbo.SaleDelivery d WHERE d.TenantId=e.TenantId AND d.C4EvidenceId=e.Id);
            """, new { Tenant, Name = run.Name(Tenant), Nonce = run.Id, input.TripId, input.ManifestLineId, input.CommandId, evidence, from, to },
            cancellationToken: ct));
        Check("harness changes only the exact unfinished C.4 row " + from + " -> " + to, 1, changed);
    }

    private const string OwnedMutationScope = """
        IF DB_NAME()<>@Name
            OR NOT EXISTS(SELECT 1 FROM dbo.LocalDemoOwner WHERE RunId=@Nonce)
            OR NOT EXISTS(SELECT 1 FROM dbo.App00ScratchMarker WHERE Nonce=@Nonce)
            OR NOT EXISTS(SELECT 1 FROM dbo.LocalApp03Session WHERE RunId=@Nonce AND TenantId=@Tenant)
            OR NOT EXISTS(SELECT 1 FROM dbo.Trip WHERE TenantId=@Tenant AND Id=@TripId)
            THROW 51915,'APP-05.2 evidence fault scope mismatch.',1;

        """;

    private Task<DeliveryState> DeliveryStateFor(SqlConnection db, DeliveryProbe input, Guid evidence) =>
        db.QuerySingleAsync<DeliveryState>(new CommandDefinition("""
            SELECT c.Status,COUNT(d.Id) AS Deliveries,COALESCE(SUM(d.BaseQuantity),0) AS Quantity,
                COALESCE(SUM(ar.OriginalAmount),0) AS Ar,COALESCE(SUM(i.CostAmount),0) AS Cogs
            FROM dbo.C4DeliveryEvidence e JOIN dbo.BusinessCommand c ON c.TenantId=e.TenantId AND c.Id=e.Id
            LEFT JOIN dbo.SaleDelivery d ON d.TenantId=e.TenantId AND d.C4EvidenceId=e.Id
            LEFT JOIN dbo.ReceivableOpenItem ar ON ar.TenantId=d.TenantId AND ar.SaleDeliveryId=d.Id
            LEFT JOIN dbo.DeliveryStockIssue i ON i.TenantId=d.TenantId AND i.SaleDeliveryId=d.Id
            WHERE e.TenantId=@Tenant AND e.Id=@Evidence AND e.TripId=@TripId AND e.ManifestLineId=@ManifestLineId
                AND e.ClientCommandId=@CommandId GROUP BY c.Status;
            """, new { Tenant, evidence, input.TripId, input.ManifestLineId, input.CommandId }, cancellationToken: ct));

    private Task<SourceProgress> AccountingProgress(SqlConnection db, AccountingWork work) =>
        db.QuerySingleAsync<SourceProgress>(new CommandDefinition("""
            SELECT COUNT(DISTINCT r.Id) AS Receipts,COUNT(p.Id) AS Postings
            FROM d4accounting.Receipt r LEFT JOIN d4accounting.Posting p ON p.ReceiptId=r.Id AND p.SourceLineId=r.SourceLineId
             AND p.Kind='BASE'
            WHERE r.DeliveryId=@RelatedId AND r.SourceLineId=@SourceId;
            """, work, cancellationToken: ct));

    private async Task<SqlConnection> OwnedDatabase()
    {
        await run.Verify(Tenant, app03Session: true);
        var db = new SqlConnection(run.Connection(Tenant));
        try { await db.OpenAsync(ct); }
        catch { await db.DisposeAsync(); throw; }
        return db;
    }

    private async Task<T> Read<T>(string name)
    {
        await using var stream = File.OpenRead(Path.Combine(folder, name + ".json"));
        return await JsonSerializer.DeserializeAsync<T>(stream, Json, ct) ?? throw new InvalidOperationException($"Missing {name} payload.");
    }

    private void Check<T>(string metric, T expected, T actual)
    {
        bool equal = EqualityComparer<T>.Default.Equals(expected, actual);
        checks.Add(new(metric, expected!, actual!, equal ? null : "Values differ"));
        if (!equal) throw new InvalidOperationException($"{metric}: expected {expected}, actual {actual}.");
    }

    private sealed record DeliveryProbe(Guid TripId, Guid ManifestLineId, Guid CommandId);
    private sealed record DeliveryEvidence(Guid EvidenceId);
    private sealed record DeliveryResolution(Guid EvidenceId, Guid SourceId);
    private sealed record DeliveryState(string Status, int Deliveries, decimal Quantity, decimal Ar, decimal Cogs);
    private sealed record SaleCustody(Guid TripId, Guid SaleSource, decimal Amount);
    private sealed record Offer(Guid OfferId);
    private sealed record Unsettled(string Status, int Handoffs, int Decisions);
    private sealed record RaceInput(Guid CommandId, string ExpectedRevision);
    private sealed record AccountingWork(Guid SourceId, Guid RelatedId);
    private sealed record AccountingRace(Guid TripId, RaceInput Input, AccountingWork Work);
    private sealed record SourceProgress(int Receipts, int Postings);
    private sealed record Posting(Guid PostingId);
    private sealed record FreshClosure(Guid ClosureId);
    private sealed record Comparison(string Metric, object Expected, object Actual, string? Diff);
}
