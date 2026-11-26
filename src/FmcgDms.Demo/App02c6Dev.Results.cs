using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using System.Xml.Linq;
using Dapper;
using FmcgDms.Api.Authentication;
using FmcgDms.Api.Persistence;
using FmcgDms.Api.Tenancy;
using Microsoft.Extensions.Configuration;
using Microsoft.Data.SqlClient;

namespace FmcgDms.Demo;

internal sealed partial class App02c6Dev
{
    private sealed record CaseResult(string CaseId, string Scope, object Expected, object Actual, object[] Diff, string Status);
    private const string Api = "/api/synthetic/";

    internal async Task Complete(int browserExit, HttpClient client, string baseUrl, string tokenA, string tokenB,
        string actor, string checkerToken, string systemAdminToken, string warehouseToken, Func<string> newSession, Func<Task> restart, CancellationToken ct)
    {
        var cases = new List<CaseResult>();
        string browserDirectory = Path.Combine(root, "web", "test-results-app02c6", run.Id);
        string browserReport = Path.Combine(browserDirectory, "junit.xml");
        var browserAssertions = new List<JsonElement>();
        string browserOutputs = Path.Combine(browserDirectory, "browser");
        if (Directory.Exists(browserOutputs))
        {
            foreach (string file in Directory.EnumerateFiles(browserOutputs, "sql-assertions.json", SearchOption.AllDirectories))
            {
                using var evidence = JsonDocument.Parse(await File.ReadAllTextAsync(file, ct));
                browserAssertions.AddRange(evidence.RootElement.EnumerateArray().Select(element => element.Clone()));
            }
        }

        foreach (string caseId in new[] { "DEV-01", "DEV-03", "DEV-05", "DEV-06", "DEV-11" })
        {
            string[] slots = caseId == "DEV-11" ? ["A"] : ["A", "B"];
            foreach (string slot in slots)
            {
                var requestFiles = Directory.GetFiles(CheckpointPath, $"{caseId}-{slot}-*.request.json").Order().ToArray();
                string[] responses = requestFiles.Select(p => p[..^".request.json".Length] + ".response.json")
                    .Where(File.Exists).ToArray();
                var tests = File.Exists(browserReport)
                    ? XDocument.Load(browserReport).Descendants("testcase")
                        .Where(test =>
                        {
                            string name = (string?)test.Attribute("name") ?? "";
                            return name.Contains(caseId, StringComparison.Ordinal) &&
                                (name.StartsWith(slot + " ", StringComparison.Ordinal) || name.Contains("both directions", StringComparison.Ordinal));
                        }).ToArray()
                    : [];
                var assertions = browserAssertions.Where(entry =>
                    entry.TryGetProperty("caseId", out var id) && id.GetString() == caseId &&
                    entry.TryGetProperty("slot", out var tenant) && tenant.GetString() == slot).ToArray();
                var diff = assertions.SelectMany(entry => entry.GetProperty("diff").EnumerateArray()).Cast<object>().ToArray();
                bool passed = browserExit == 0 && tests.Length > 0 && responses.Length > 0 && assertions.Length > 0 && diff.Length == 0 &&
                    tests.All(test => test.Element("failure") is null && test.Element("skipped") is null);
                if (!passed && diff.Length == 0) diff = ["Required case-specific browser/SQL evidence is missing or failed."];
                cases.Add(new(caseId, slot, assertions.Select(entry => entry.GetProperty("expected")),
                    new { browserExit, checkpointFiles = responses.Select(Path.GetFileName).ToArray(), matchingTests = tests.Length, assertions },
                    diff, passed ? "PASS" : "BLOCKED"));
            }
        }
        await Record("DEV-09", "A/B", () => Security(client, baseUrl, tokenA, tokenB, systemAdminToken, warehouseToken, actor, ct));
        await Record("DEV-10", "A", () => Recovery(client, baseUrl, tokenA, newSession, restart, ct));

        try
        {
            var advanced = await App02c6Advanced.Run(run, client, baseUrl, tokenA, actor, checkerToken, Checker, SetClock, ct);
            await WriteJson(Path.Combine(directory, "advanced-sql.json"), advanced);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            await WriteJson(Path.Combine(directory, "advanced-error.json"), new { error = Describe(ex) });
        }

        string advancedPath = Path.Combine(directory, "advanced-sql.json");
        if (File.Exists(advancedPath))
        {
            using var results = JsonDocument.Parse(await File.ReadAllTextAsync(advancedPath, ct));
            foreach (var result in results.RootElement.EnumerateArray())
            {
                string id = result.GetProperty("caseId").GetString()!;
                bool pass = result.TryGetProperty("matches", out var match) && match.ValueKind == JsonValueKind.True;
                cases.Add(new(id, "A", result.GetProperty("expected").Clone(), result.GetProperty("actual").Clone(),
                    pass ? [] : [result.Clone()], pass ? "PASS" : "FAIL"));
            }
        }

        foreach (string missing in Enumerable.Range(1, 11).Select(i => $"DEV-{i:00}")
            .Except(cases.Select(c => c.CaseId), StringComparer.Ordinal))
            cases.Add(new(missing, "A", "Approved matrix v0.1", "No completed evidence", ["Case was not completed"], "NOT_RUN"));

        string report = Path.Combine(directory, "sql-review.json");
        await WriteJson(report, new
        {
            source, apiSha256, runId = run.Id, instance = DemoRun.Instance, host = Environment.MachineName,
            observedUtc = DateTimeOffset.UtcNow, matrixSha256 = MatrixSha,
            tenantDatabases = DemoRun.Tenants.Select(t => new { tenant = t, database = new SqlConnectionStringBuilder(run.Connection(t)).InitialCatalog }),
            browserReportSha256 = File.Exists(browserReport) ? Hash(browserReport) : null,
            browserFixtureSha256 = Hash(Path.Combine(root, "web", "e2e-app02c6", "sales.spec.ts")),
            webLockSha256 = Hash(Path.Combine(root, "web", "package-lock.json")),
            checkpoints = Directory.GetFiles(CheckpointPath, "*.response.json").Order()
                .Select(p => new { file = Path.GetFileName(p), sha256 = Hash(p) }),
            physical = "NOT_RUN",
            cases = cases.OrderBy(c => c.CaseId).ThenBy(c => c.Scope)
        });
        Console.WriteLine($"APP-02C.6 SQL comparison: {report}");
        if (cases.Any(c => c.Status != "PASS"))
            throw new InvalidOperationException("C.6 required case evidence is incomplete or mismatched.");

        async Task Record(string id, string scope, Func<Task<CaseResult>> action)
        {
            try { cases.Add(await action()); }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                cases.Add(new(id, scope, "Approved matrix v0.1", Describe(ex), ["Case did not complete"], "FAIL"));
            }
        }
    }

    private sealed record PeerFixture(Guid Id, string Version);
    private async Task<CaseResult> Security(HttpClient client, string baseUrl, string tokenA, string tokenB,
        string admin, string warehouse, string actor, CancellationToken ct)
    {
        var outcomes = new List<object>();
        var differences = new List<object>();
        foreach (var (tenant, peer, token) in new[] { ("tenant-a", "tenant-b", tokenA), ("tenant-b", "tenant-a", tokenB) })
        {
            await using var reader = await ReadOnly(run, peer);
            var target = await reader.QuerySingleAsync<PeerFixture>("""
                SELECT Id,CONVERT(varchar(16),CONVERT(binary(8),RowVersion),2) AS Version FROM dbo.Customer WHERE Code=N'C2-FIXTURE'
                """);
            var before = await Observe(run, peer);
            using var read = await Send(client, baseUrl, $"app02c1/customers/{target.Id}", token, ct);
            using var write = await Send(client, baseUrl, "app02c1/customers/save", token, ct,
                new C1CustomerInput(Guid.NewGuid(), target.Id, target.Version, null, "Peer write probe"));
            using var forged = await Send(client, baseUrl, "app02c1/session?tenantId=" + peer, token, ct, forgedTenant: peer);
            using var forgedBody = JsonDocument.Parse(await forged.Content.ReadAsStringAsync(ct));
            string? effectiveTenant = forgedBody.RootElement.GetProperty("tenantId").GetString();
            var after = await Observe(run, peer);
            bool unsafeRefused = await RefuseMisbound(tenant, peer, ct);
            var observation = new { tenant, peer, read = (int)read.StatusCode, write = (int)write.StatusCode,
                effectiveTenant, peerBefore = before.Fingerprint, peerAfter = after.Fingerprint, unsafeRefused };
            outcomes.Add(observation);
            if (read.StatusCode != HttpStatusCode.NotFound || write.IsSuccessStatusCode || effectiveTenant != tenant ||
                before.Fingerprint != after.Fingerprint || !unsafeRefused) differences.Add(observation);
        }
        using var deniedAdmin = await Send(client, baseUrl, "app02c1/customers", admin, ct);
        using var deniedWarehouse = await Send(client, baseUrl, "app02c5/ar", warehouse, ct);
        await run.Verify("tenant-a");
        await using var db = new SqlConnection(run.Connection("tenant-a"));
        const string grant = """
            UPDATE dbo.App02ActorRoleGrant SET IsActive=@active,ChangedBy=N'c6-dev-launcher',ChangedAtUtc=SYSUTCDATETIME()
            WHERE TenantId='tenant-a' AND ActorId=@actor AND RoleCode='sales'
            """;
        HttpStatusCode revoked;
        await db.ExecuteAsync(grant, new { active = false, actor });
        try
        {
            using var denied = await Send(client, baseUrl, "app02c5/ar", tokenA, ct);
            revoked = denied.StatusCode;
        }
        finally { await db.ExecuteAsync(grant, new { active = true, actor }); }
        await db.ExecuteAsync("UPDATE dbo.C4DeliveryWriter SET ActorId=@checker WHERE ActorId=@actor", new { actor, checker = Checker });
        try
        {
            using var unassigned = await Send(client, baseUrl, "app02c5/ar", tokenA, ct);
            using var report = JsonDocument.Parse(await unassigned.Content.ReadAsStringAsync(ct));
            int visible = report.RootElement.GetProperty("sources").GetArrayLength();
            var authority = new { admin = (int)deniedAdmin.StatusCode, wrongRole = (int)deniedWarehouse.StatusCode,
                revoked = (int)revoked, unassignedVisible = visible };
            outcomes.Add(authority);
            if (deniedAdmin.StatusCode != HttpStatusCode.Forbidden || deniedWarehouse.StatusCode != HttpStatusCode.Forbidden ||
                revoked != HttpStatusCode.Forbidden || visible != 0) differences.Add(authority);
        }
        finally
        {
            await db.ExecuteAsync("UPDATE dbo.C4DeliveryWriter SET ActorId=@actor WHERE ActorId=@checker", new { actor, checker = Checker });
        }

        return new("DEV-09", "A/B", "404 peer read, rejected peer write, unchanged peer rows, current tenant and authority",
            outcomes, differences.ToArray(), differences.Count == 0 ? "PASS" : "FAIL");
    }

    private async Task<bool> RefuseMisbound(string tenant, string peer, CancellationToken ct)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["SyntheticAuth:Enabled"] = "true",
            [$"TenantDatabases:{tenant}"] = run.Connection(peer)
        }).Build();
        using var host = Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder().UseEnvironment("IntegrationTest").Build();
        var environment = host.Services.GetRequiredService<Microsoft.Extensions.Hosting.IHostEnvironment>();
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim("tenant_id", tenant)], "C6 guard check"));
        try
        {
            await using var unexpected = await new TenantConnections(config, environment).OpenSyntheticScratchAsync(principal, ct);
            return false;
        }
        catch (UnauthorizedAccessException) { return true; }
    }

    private sealed record Receipt(Guid CommandId, Guid TripId, Guid ManifestLineId, int ShipToVersion,
        decimal Quantity, Guid? SourceId);
    private async Task<CaseResult> Recovery(HttpClient client, string baseUrl, string token, Func<string> newSession, Func<Task> restart,
        CancellationToken ct)
    {
        await using var reader = await ReadOnly(run, "tenant-a");
        var receipts = (await reader.QueryAsync<Receipt>("""
            SELECT e.ClientCommandId AS CommandId,e.TripId,e.ManifestLineId,e.ShipToVersion,e.Quantity,d.BusinessSourceId AS SourceId
            FROM dbo.C4DeliveryEvidence e LEFT JOIN dbo.SaleDelivery d ON d.TenantId=e.TenantId AND d.C4EvidenceId=e.Id
            WHERE e.Quantity IN (13,25) ORDER BY e.OccurredAtUtc,e.Id
            """)).ToArray();
        string browserEvidence = Path.Combine(root, "web", "test-results-app02c6", run.Id, "browser");
        Guid lostAck = Guid.Empty;
        foreach (string file in Directory.EnumerateFiles(browserEvidence, "browser-evidence.json", SearchOption.AllDirectories))
        {
            using var evidence = JsonDocument.Parse(await File.ReadAllTextAsync(file, ct));
            if (evidence.RootElement.GetProperty("slot").GetString() == "A")
                lostAck = evidence.RootElement.GetProperty("deliveryCommandId").GetGuid();
        }
        var applied = receipts.Single(r => r.CommandId == lostAck && r.SourceId is not null);
        var pending = receipts.First(r => r.Quantity == 13 && r.SourceId is null);
        var before = await Observe(run, "tenant-a");
        await restart();
        using var recoveredSession = CreateEvidenceClient();
        string freshToken = newSession();
        using var lookup = await Send(recoveredSession, baseUrl, $"app02c4/trips/{applied.TripId}/commands/{applied.CommandId}", freshToken, ct);
        using var lookupBody = JsonDocument.Parse(await lookup.Content.ReadAsStringAsync(ct));
        var input = new C4DeliveryInput(applied.CommandId, applied.TripId, applied.ManifestLineId, applied.ShipToVersion, applied.Quantity);
        var replies = await Task.WhenAll(
            Send(client, baseUrl, "app02c4/deliver", token, ct, input),
            Send(recoveredSession, baseUrl, "app02c4/deliver", freshToken, ct, input));
        try
        {
            var recovered = new List<Guid?>();
            foreach (var reply in replies)
            {
                using var body = JsonDocument.Parse(await reply.Content.ReadAsStringAsync(ct));
                recovered.Add(reply.IsSuccessStatusCode && body.RootElement.GetProperty("replayed").GetBoolean()
                    ? body.RootElement.GetProperty("sourceId").GetGuid() : null);
            }
            using var changed = await Send(client, baseUrl, "app02c4/deliver", token, ct, input with { Quantity = 24 });
            var after = await Observe(run, "tenant-a");
            var actual = new
            {
                lookup = lookupBody.RootElement.Clone(), recovered, changed = (int)changed.StatusCode,
                pendingCommand = pending.CommandId, appliedCommand = applied.CommandId,
                freshCredential = freshToken != token,
                before = before.Fingerprint, after = after.Fingerprint
            };
            bool matches = freshToken != token && lookup.IsSuccessStatusCode && lookupBody.RootElement.GetProperty("sourceId").GetGuid() == applied.SourceId &&
                recovered.All(id => id == applied.SourceId) && changed.StatusCode == HttpStatusCode.Conflict &&
                before.Fingerprint == after.Fingerprint;
            return new("DEV-10", "A", "Durable source, two replays, 409 changed payload, no duplicate effects", actual,
                matches ? [] : [actual], matches ? "PASS" : "FAIL");
        }
        finally { foreach (var reply in replies) reply.Dispose(); }
    }

    private static string Describe(Exception ex) => ex is SqlException sql
        ? $"{sql.GetType().Name} {sql.Number}: {sql.Message}" : $"{ex.GetType().Name}: {ex.Message}";

    private static async Task<HttpResponseMessage> Send(HttpClient client, string root, string suffix, string token,
        CancellationToken ct, object? body = null, string? forgedTenant = null)
    {
        using var message = new HttpRequestMessage(body is null ? HttpMethod.Get : HttpMethod.Post, root + Api + suffix);
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (forgedTenant is not null) message.Headers.Add("X-Tenant-Id", forgedTenant);
        if (body is not null) message.Content = JsonContent.Create(body);
        return await client.SendAsync(message, ct);
    }
}
