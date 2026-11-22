using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using FmcgDms.Api.Persistence;
using Microsoft.Data.SqlClient;

namespace FmcgDms.Demo;

internal sealed partial class App02d4Dev
{
    private sealed record FixtureSource(DateTimeOffset OccurredAt, decimal Quantity, decimal Gross, decimal Ar, decimal Cost);

    private async Task<SqlConnection> Owner(string tenant)
    {
        await run.Verify(tenant);
        var db = new SqlConnection(run.Connection(tenant));
        await db.OpenAsync(ct);
        return db;
    }

    private async Task<SqlConnection> Reader(string tenant)
    {
        var db = await Owner(tenant);
        try
        {
            await db.ExecuteAsync("EXECUTE AS USER='d4oracle' WITH NO REVERT;");
            return db;
        }
        catch
        {
            await db.DisposeAsync();
            throw;
        }
    }

    private async Task<string> SourceHash(string tenant)
    {
        string primary = await D2Sql.Fingerprint(run, tenant);
        await using var db = await Owner(tenant);
        string additional = await db.ExecuteScalarAsync<string>("""
            SELECT CONCAT((SELECT * FROM dbo.D3SalePricing ORDER BY TenantId,SaleDeliveryId FOR JSON PATH, INCLUDE_NULL_VALUES),
                '|', (SELECT * FROM dbo.D3TripUnload ORDER BY Id FOR JSON PATH, INCLUDE_NULL_VALUES),
                '|', (SELECT * FROM dbo.D3CommandScope ORDER BY CommandRowId FOR JSON PATH, INCLUDE_NULL_VALUES),
                '|', (SELECT * FROM dbo.C4DeliveryEvidence ORDER BY Id FOR JSON PATH, INCLUDE_NULL_VALUES),
                '|', (SELECT * FROM dbo.App02OutboxMessage ORDER BY MessageId FOR JSON PATH, INCLUDE_NULL_VALUES))
            """) ?? "";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(primary + additional)));
    }

    private async Task Upgrade(string tenant)
    {
        var scene = scenes[tenant];
        string target = run.Connection(tenant);
        await using var owner = await Owner(tenant);
        Equal("D.3 migration boundary", await owner.ExecuteScalarAsync<long>("SELECT MAX(Version) FROM dbo.VersionInfo"), 202611220010L);
        void ExpectMigrationDenied(string label, string connection, string proposedTenant, string nonce)
        {
            bool denied = false;
            try { ScratchMigrator.MigrateD4(connection, proposedTenant, nonce, LogLevel.Warning); }
            catch (InvalidOperationException) { denied = true; }
            Equal(label, denied, true);
        }
        ExpectMigrationDenied("wrong instance refused",
            new SqlConnectionStringBuilder(target) { DataSource = @"(localdb)\D4NotOwned" }.ConnectionString, tenant, run.Id);
        ExpectMigrationDenied("wrong DB name refused",
            new SqlConnectionStringBuilder(target) { InitialCatalog = "master" }.ConnectionString, tenant, run.Id);
        ExpectMigrationDenied("wrong nonce refused", target, tenant, Key());
        ExpectMigrationDenied("wrong tenant refused", target, Peer(tenant), run.Id);
        string invalidMarker = Key();
        await owner.ExecuteAsync("UPDATE dbo.App00ScratchMarker SET Nonce=@invalidMarker WHERE Nonce=@Id",
            new { invalidMarker, run.Id });
        try { ExpectMigrationDenied("wrong ownership marker refused", target, tenant, run.Id); }
        finally
        {
            await owner.ExecuteAsync("UPDATE dbo.App00ScratchMarker SET Nonce=@Id WHERE Nonce=@invalidMarker",
                new { invalidMarker, run.Id });
        }
        Equal("rejection left D.3 version untouched", await owner.ExecuteScalarAsync<long>("SELECT MAX(Version) FROM dbo.VersionInfo"), 202611220010L);

        ScratchMigrator.MigrateD4(target, tenant, run.Id, LogLevel.Warning);
        Equal("D.4 migration boundary", await owner.ExecuteScalarAsync<long>("SELECT MAX(Version) FROM dbo.VersionInfo"), 202611230001L);
        Equal("D.3 source fingerprint across upgrade", await SourceHash(tenant), scene.SourceFingerprint);
        Equal("tenant catalog", await owner.ExecuteScalarAsync<string>("SELECT TenantId FROM dbo.AppTenantBinding WHERE Slot=1"), tenant);
        await owner.ExecuteAsync("""
            INSERT d4app.TripProfile(TripId) VALUES (@Trip);
            INSERT d4app.InvoiceProfile(Seller,Channel,Active)
              VALUES('D4-A','MOCK-V1',1),('D4-B','MOCK-V1',1),('D4-NO-CHANNEL',NULL,0);
            INSERT d4app.ActorRole(ActorId,RoleCode,IsActive) VALUES ('d4-dual','maker',1),('d4-dual','checker',1);
            """, scene);
        foreach (string role in Roles)
            await owner.ExecuteAsync("INSERT d4app.ActorRole(ActorId,RoleCode,IsActive) VALUES (@actor,@role,1)",
                new { actor = Actor(role), role });

        var fresh = new DemoRun(Key());
        try
        {
            await fresh.Create(tenant, bindBeforeFeatureMigrations: true);
            await fresh.Verify(tenant);
            ScratchMigrator.MigrateD4(fresh.Connection(tenant), tenant, fresh.Id, LogLevel.Warning);
            await using var db = new SqlConnection(fresh.Connection(tenant));
            Equal("fresh scratch latest migration", await db.ExecuteScalarAsync<long>("SELECT MAX(Version) FROM dbo.VersionInfo"), 202611230001L);
            Equal("fresh scratch source isolation", await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM d4app.CommittedSource"), 0);
        }
        finally { await fresh.RemoveDevSlot(tenant); }

        await using var oracleDb = await Reader(tenant);
        Equal("SQL oracle user", await oracleDb.ExecuteScalarAsync<string>("SELECT USER_NAME()"), "d4oracle");
        await PermissionDenied(oracleDb, "UPDATE d4app.ActorRole SET IsActive=1 WHERE 1=0", "oracle cannot write");
        foreach ((string principal, string other) in new[] { ("d4invoice", "d4accounting.Journal"), ("d4accounting", "d4invoice.InvoiceFact") })
        {
            await using var probe = await Owner(tenant);
            await probe.ExecuteAsync(principal == "d4invoice"
                ? "EXECUTE AS USER='d4invoice' WITH NO REVERT;"
                : "EXECUTE AS USER='d4accounting' WITH NO REVERT;");
            Equal("SQL effective partner", await probe.ExecuteScalarAsync<string>("SELECT USER_NAME()"), principal);
            await PermissionDenied(probe, "INSERT d4app.ActorRole(ActorId,RoleCode,IsActive) SELECT 'probe','invoice',1 WHERE 1=0",
                principal + " app insert denied");
            string write = other == "d4accounting.Journal"
                ? "INSERT d4accounting.Journal(Operation,SourceLineId,TransportId,Outcome,RecordedAt) SELECT 'receive',NEWID(),'probe','probe',SYSDATETIMEOFFSET() WHERE 1=0"
                : "INSERT d4invoice.InvoiceFact(IntentId,StableKey,FactId,Channel,Hash,Payload,CommittedAt) SELECT NEWID(),'probe',NEWID(),'probe','probe','{}',SYSDATETIMEOFFSET() WHERE 1=0";
            await PermissionDenied(probe, write, principal + " partner insert denied");
            string peerDb = new SqlConnectionStringBuilder(run.Connection(Peer(tenant))).InitialCatalog;
            await PermissionDenied(probe, $"SELECT COUNT(*) FROM [{peerDb}].dbo.AppTenantBinding", principal + " peer read denied");
        }

        await using (var app = await Owner(tenant))
        {
            await app.ExecuteAsync("EXECUTE AS USER='d4app' WITH NO REVERT;");
            Equal("restricted application SQL user", await app.ExecuteScalarAsync<string>("SELECT USER_NAME()"), "d4app");
            await PermissionDenied(app, "UPDATE d4app.InvoiceIntent SET Hash='probe' WHERE 1=0", "app cannot change frozen invoice hash");
            await PermissionDenied(app, "UPDATE d4app.AccountingCorrection SET Gross=0 WHERE 1=0", "app cannot change frozen correction delta");
        }

        var values = (await oracleDb.QueryAsync<FixtureSource>("SELECT OccurredAt,Quantity,Gross,Ar,Cost FROM d4app.CommittedSource ORDER BY OccurredAt")).ToArray();
        Equal("fixture delivery count", values.Length, 3);
        int i = 0;
        foreach (var expected in fixture.GetProperty("deliveries").EnumerateArray())
        {
            var fact = values[i++];
            Equal("source time", fact.OccurredAt, expected.GetProperty("occurredUtc").GetDateTimeOffset());
            Amount(expected.GetProperty("key").GetString()!, [fact.Quantity, fact.Gross, fact.Ar, fact.Cost],
                expected.GetProperty("key").GetString()!);
        }
        Equal("invoice eligibility count", await oracleDb.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.SaleDelivery WHERE TripId=@Trip AND InvoiceEligible=1", scene), 0);
        Equal("A deliveries on one order", await oracleDb.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.SaleDelivery WHERE TripId=@Trip AND AOrderId=@Order AND RightKind='A'", scene), 2);
    }

    private async Task PermissionDenied(SqlConnection db, string sql, string label)
    {
        bool denied = false;
        try { await db.ExecuteAsync(sql); }
        catch (SqlException e) when (e.Number is 229 or 230 or 916) { denied = true; }
        Equal(label, denied, true);
    }

    private async Task AccountingCases(string tenant)
    {
        await using var db = await Reader(tenant);
        using var local = new HttpClient(new SocketsHttpHandler { UseProxy = false })
        {
            BaseAddress = new Uri(origin), Timeout = TimeSpan.FromSeconds(5)
        };
        var tokens = Roles.ToDictionary(role => role, role => sign(tenant, role, Actor(role)));
        tokens.Add("dual-maker", sign(tenant, "maker", "d4-dual"));
        tokens.Add("dual-checker", sign(tenant, "checker", "d4-dual"));
        var observed = await App02d4DevAccounting.Run(local, db, scenes[tenant].Deliveries, tokens,
            d1.SetClock, () => ResetAccounting(tenant), () => restart(true), ct);
        results.AddRange(observed.Select(x => new CaseResult(x.Id, tenant, x.Status, x.Checks.ToArray(), x.Error)));
        foreach (var item in observed) Console.WriteLine($"{item.Id} {tenant} {item.Status} {item.Error}");
    }

    private Task ResetRehearsal(string tenant) => Reset(tenant, InvoiceResetSql + AccountingResetSql);
    private Task ResetInvoice(string tenant) => Reset(tenant, InvoiceResetSql);
    private Task ResetAccounting(string tenant) => Reset(tenant, AccountingResetSql);
    private const string InvoiceResetSql = """
        DELETE FROM d4app.InvoiceAttempt;
        DELETE FROM d4app.InvoiceCase;
        DELETE FROM d4app.InvoiceIntent;
        DELETE FROM d4invoice.InvoiceFact;
        """;
    private const string AccountingResetSql = """
        DELETE FROM d4app.AccountingCorrection;
        DELETE FROM d4app.AccountingCase;
        DELETE FROM d4app.AccountingReportLine;
        DELETE FROM d4app.AccountingReport;
        DELETE FROM d4accounting.Posting WHERE OriginalId IS NOT NULL;
        DELETE FROM d4accounting.Posting;
        DELETE FROM d4accounting.Journal;
        DELETE FROM d4accounting.Receipt;
        """;

    private async Task Reset(string tenant, string sql)
    {
        await using var db = await Owner(tenant);
        await using var tx = await db.BeginTransactionAsync(ct);
        await db.ExecuteAsync(sql, transaction: tx);
        await tx.CommitAsync(ct);
    }

    private async Task<string> D4State(string tenant)
    {
        await using var db = await Reader(tenant);
        string body = await db.QuerySingleAsync<string>("""
            SELECT COALESCE((SELECT * FROM d4app.InvoiceIntent ORDER BY IntentId FOR JSON PATH), '')
                + COALESCE((SELECT * FROM d4app.InvoiceAttempt ORDER BY AttemptId FOR JSON PATH), '')
                + COALESCE((SELECT * FROM d4app.InvoiceCase ORDER BY CaseId FOR JSON PATH), '')
                + COALESCE((SELECT * FROM d4invoice.InvoiceFact ORDER BY FactId FOR JSON PATH), '')
                + COALESCE((SELECT * FROM d4accounting.Receipt ORDER BY Id FOR JSON PATH), '')
                + COALESCE((SELECT * FROM d4accounting.Posting ORDER BY Id FOR JSON PATH), '')
                + COALESCE((SELECT * FROM d4accounting.Journal ORDER BY Id FOR JSON PATH), '')
                + COALESCE((SELECT * FROM d4app.AccountingReportLine ORDER BY ReportId,SourceLineId FOR JSON PATH), '')
                + COALESCE((SELECT * FROM d4app.AccountingReport ORDER BY Id FOR JSON PATH), '')
                + COALESCE((SELECT * FROM d4app.AccountingCase ORDER BY Id FOR JSON PATH), '')
                + COALESCE((SELECT * FROM d4app.AccountingCorrection ORDER BY Id FOR JSON PATH), '')
            """);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body)));
    }

    private async Task Isolation(string tenant)
    {
        string other = Peer(tenant);
        string mineBefore = await D4State(tenant);
        string peerBefore = await D4State(other);
        var foreign = scenes[other];
        Equal("technical partner denies an application token", (await Request(tenant, "invoice", "/stub/accounting/snapshot",
            new { asOf = At("fullCutoffUtc") }, absolute: true)).Http, 403);
        Equal("claim/header tenant mismatch", (await Request(tenant, "invoice", "sources", peer: other)).Http, 403);
        Equal("foreign delivery blocked", (await Request(tenant, "invoice", "invoice/prepare",
            new { deliveryId = foreign.A10 })).Status, "SOURCE_NOT_COMMITTED");
        Equal("foreign accounting source blocked", (await Request(tenant, "accountant", $"accounting/{foreign.B20}/receive",
            new { transportId = Key() })).Http is >= 400 and < 500, true);
        await using var peerSql = await Reader(other);
        Guid intent = await peerSql.QuerySingleAsync<Guid>("SELECT IntentId FROM d4app.InvoiceIntent WHERE DeliveryId=@A10", foreign);
        Equal("foreign intent read", (await Request(tenant, "invoice", $"invoice/{intent}")).Http, 404);
        Equal("foreign intent lookup", (await Request(tenant, "invoice", $"invoice/{intent}/lookup", new { })).Http, 404);
        var report = await peerSql.QuerySingleAsync<Guid>("SELECT Id FROM d4app.AccountingReport WHERE Status='RECONCILED_OK'");
        var correction = await peerSql.QuerySingleAsync<Guid>("SELECT Id FROM d4app.AccountingCorrection");
        Equal("forged peer reconciliation denied", (await Request(tenant, "reconciler", "accounting/reconcile",
            new { from = At("fromUtc"), cutoff = At("fullCutoffUtc"), asOf = At("fullCutoffUtc") }, peer: other)).Http, 403);
        Equal("foreign reconciliation report read", (await Request(tenant, "reconciler", $"accounting/reports/{report}")).Http, 404);
        Equal("foreign correction approval", (await Request(tenant, "checker", $"accounting/corrections/{correction}/approve", new { })).Http, 404);
        Equal("own D.4 fingerprint", await D4State(tenant), mineBefore);
        Equal("peer D.4 fingerprint", await D4State(other), peerBefore);
        Equal("own source fingerprint", await SourceHash(tenant), scenes[tenant].SourceFingerprint);
        Equal("peer source fingerprint", await SourceHash(other), foreign.SourceFingerprint);
    }

    private async Task<object[]> SqlReceipt()
    {
        var data = new List<object>();
        foreach (string tenant in DemoRun.Tenants)
        {
            await using var db = await Reader(tenant);
            data.Add(new
            {
                tenant, slot = Slot(tenant), sqlUser = await db.ExecuteScalarAsync<string>("SELECT USER_NAME()"),
                sources = await db.QueryAsync("SELECT * FROM d4app.CommittedSource ORDER BY OccurredAt"),
                intents = await db.QueryAsync("SELECT * FROM d4app.InvoiceIntent ORDER BY IntentId"),
                attempts = await db.QueryAsync("SELECT * FROM d4app.InvoiceAttempt ORDER BY StartedAt,AttemptId"),
                invoiceFacts = await db.QueryAsync("SELECT * FROM d4invoice.InvoiceFact ORDER BY FactId"),
                invoiceCases = await db.QueryAsync("SELECT * FROM d4app.InvoiceCase ORDER BY CaseId"),
                receipts = await db.QueryAsync("SELECT * FROM d4accounting.Receipt ORDER BY ReceivedAt,Id"),
                postings = await db.QueryAsync("SELECT * FROM d4accounting.Posting ORDER BY PostedAt,Id"),
                reports = await db.QueryAsync("SELECT * FROM d4app.AccountingReport ORDER BY Id"),
                reportLines = await db.QueryAsync("SELECT * FROM d4app.AccountingReportLine ORDER BY ReportId,SourceLineId"),
                cases = await db.QueryAsync("SELECT * FROM d4app.AccountingCase ORDER BY Id"),
                corrections = await db.QueryAsync("SELECT * FROM d4app.AccountingCorrection ORDER BY Id"),
                beforeSourceHash = scenes[tenant].SourceFingerprint, afterSourceHash = await SourceHash(tenant)
            });
        }
        return data.ToArray();
    }

    private async Task Serve(Process process)
    {
        while (!process.HasExited)
        {
            foreach (string path in Directory.GetFiles(Checkpoints, "*.request.json"))
            {
                string output = path[..^".request.json".Length] + ".response.json";
                if (File.Exists(output)) continue;
                using var input = JsonDocument.Parse(await File.ReadAllTextAsync(path, ct));
                string slot = input.RootElement.GetProperty("slot").GetString()!;
                string tenant = slot switch { "A" => "tenant-a", "B" => "tenant-b", _ => throw new InvalidDataException("Unknown slot.") };
                string phase = input.RootElement.GetProperty("phase").GetString()!;
                if (Path.GetFileName(path) != $"{slot}-{phase}.request.json") throw new InvalidDataException("Invalid checkpoint.");
                switch (phase)
                {
                    case "clock-start": await d1.SetClock(At("operationUtc")); break;
                    case "clock-first-reconcile": await d1.SetClock(At("fullCutoffUtc")); break;
                    case "clock-correction": await d1.SetClock(At("correctionUtc")); break;
                    case "clock-reconcile": await d1.SetClock(At("finalAsOfUtc")); break;
                    case "restart-invoice": await restart(true); break;
                    case "revoke-invoice":
                    case "restore-invoice":
                        await using (var owner = await Owner(tenant))
                            await owner.ExecuteAsync("UPDATE d4app.ActorRole SET IsActive=@active WHERE ActorId='d4-invoice' AND RoleCode='invoice'",
                                new { active = phase == "restore-invoice" });
                        break;
                    default: throw new InvalidDataException("Unknown checkpoint phase " + phase);
                }
                await Save(output, new { ok = true, slot, phase });
            }
            await Task.Delay(60, ct);
        }
    }

    private async Task VerifyBrowser(string tenant, int exit)
    {
        string file = Path.Combine(Checkpoints, Slot(tenant) + "-browser.json");
        Equal("Playwright exit", exit, 0);
        Equal("tenant Playwright receipt", File.Exists(file), true);
        using var observed = JsonDocument.Parse(await File.ReadAllTextAsync(file, ct));
        var access = observed.RootElement.GetProperty("access");
        Equal("browser revoked status", access.GetProperty("revokedHttp").GetInt32(), 403);
        Equal("browser offline POSTs", access.GetProperty("offlinePostCount").GetInt32(), 0);
        Equal("browser offline queue", access.GetProperty("queuedAfterOffline").GetBoolean(), false);
        Equal("source fingerprint after live revoke/offline", await SourceHash(tenant), scenes[tenant].SourceFingerprint);
        await using var db = await Reader(tenant);
        Equal("invoice facts after browser", await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM d4invoice.InvoiceFact"), 2);
        Equal("browser financial deliveries", await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM d4accounting.Receipt"), 3);
        await VerifyRevokedRoles(tenant, db);
        Equal("browser correction", await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM d4accounting.Posting WHERE Kind='CORRECTION'"), 1);
        Equal("A15 has no fact after offline/revocation", await db.ExecuteScalarAsync<int>("""
            SELECT COUNT(*) FROM d4invoice.InvoiceFact f JOIN d4app.InvoiceIntent i ON i.IntentId=f.IntentId
            WHERE i.DeliveryId=@A15;
            """, scenes[tenant]), 0);
        Equal("browser historical DIFF retained", await db.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM d4app.AccountingReport WHERE Status='RECONCILED_DIFF' AND DiffCount=1 AND ExpectedCount=3"), 1);
        Equal("browser new report ties to closed case", await db.ExecuteScalarAsync<int>("""
            SELECT COUNT(*) FROM d4app.AccountingCase c JOIN d4app.AccountingReport r ON r.Id=c.ClosedReportId
            WHERE r.Status='RECONCILED_OK' AND r.OkCount=3;
            """), 1);
    }

    private async Task VerifyRevokedRoles(string tenant, SqlConnection reader)
    {
        var fixture = scenes[tenant];
        Guid intent = await reader.QuerySingleAsync<Guid>("SELECT IntentId FROM d4app.InvoiceIntent WHERE DeliveryId=@A15", fixture);
        Guid correction = await reader.QuerySingleAsync<Guid>("SELECT Id FROM d4app.AccountingCorrection");
        string before = await D4State(tenant);
        await using var owner = await Owner(tenant);
        const string grant = "UPDATE d4app.ActorRole SET IsActive=@active WHERE ActorId IN ('d4-invoice','d4-checker')";
        Equal("revoked actor roles", await owner.ExecuteAsync(grant, new { active = false }), 2);
        try
        {
            Equal("revoked invoice lookup", (await Request(tenant, "invoice", $"invoice/{intent}/lookup", new { })).Http, 403);
            Equal("revoked checker approval", (await Request(tenant, "checker", $"accounting/corrections/{correction}/approve", new { })).Http, 403);
        }
        finally { await owner.ExecuteAsync(grant, new { active = true }); }
        Equal("revoked API probes leave no effects", await D4State(tenant), before);
    }
}
