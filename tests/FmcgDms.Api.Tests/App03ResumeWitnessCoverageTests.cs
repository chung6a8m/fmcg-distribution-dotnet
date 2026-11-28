using System.Text.Json;
using FmcgDms.Demo;
using Xunit;

namespace FmcgDms.Api.Tests;

public sealed class App03ResumeWitnessCoverageTests : IDisposable
{
    private readonly string output = Directory.CreateTempSubdirectory("app03-resume-coverage-").FullName;

    [Theory]
    [InlineData("complete", "CLIENT_SQL_READY_FOR_REVIEW")]
    [InlineData("missingPeer", "DIAGNOSTIC_EVIDENCE_INCOMPLETE")]
    [InlineData("wrongTenant", "DIAGNOSTIC_EVIDENCE_INCOMPLETE")]
    [InlineData("gap", "DIAGNOSTIC_EVIDENCE_INCOMPLETE")]
    [InlineData("missingEnd", "DIAGNOSTIC_EVIDENCE_INCOMPLETE")]
    [InlineData("lastTimeoutNoDrain", "DIAGNOSTIC_EVIDENCE_INCOMPLETE")]
    [InlineData("lastTimeoutDrained", "CLIENT_SQL_READY_FOR_REVIEW")]
    [InlineData("badOutcome", "DIAGNOSTIC_EVIDENCE_INCOMPLETE")]
    public void Coverage_requires_both_tenants_and_matching_pairs(string scenario, string expected)
    {
        var start = new DateTimeOffset(2026, 11, 28, 0, 0, 0, TimeSpan.Zero).AddSeconds(1);
        var end = start.AddMilliseconds(100);
        foreach (string tenant in new[] { "tenant-a", "tenant-b" })
        {
            bool lastTimeout = tenant == "tenant-b" && scenario.StartsWith("lastTimeout", StringComparison.Ordinal);
            string suffix = tenant == "tenant-a" ? "a" : "b";
            string id = Guid.NewGuid().ToString("N");
            string route = App03SettlementHttpWitness.SessionRoute;
            const string checkpoint = "nextday-1", source = "source", runId = "run", profile = "app03-5";
            Save($"resume-session-http-{id}-start.json", new { schema = "APP03_RESUME_HTTP_V1", source, runId, checkpoint,
                tenant, id, route, method = "GET", apiPid = 1, vitePid = 2, startedUtc = start, timeoutMs = 3000 });
            if (!(tenant == "tenant-b" && scenario == "missingEnd"))
                Save($"resume-session-http-{id}-end.json", new { schema = "APP03_RESUME_HTTP_V1", source, runId, checkpoint,
                    tenant, id, route, method = "GET", apiPid = 1, vitePid = 2, startedUtc = start, endedUtc = end,
                    elapsedMs = 100, timeoutMs = 3000, outcome = lastTimeout ? "HTTP_TIMEOUT" : "RESPONSE_RECEIVED",
                    status = lastTimeout ? (int?)null : 200, assertion = lastTimeout ? "NOT_RUN" : scenario == "badOutcome" ? "EXPECTED_FORBIDDEN" : "PASSED",
                    responseReceivedUtc = lastTimeout ? (DateTimeOffset?)null : end, responseElapsedMs = lastTimeout ? (double?)null : 100 });
            if (tenant == "tenant-b" && scenario == "missingPeer") continue;
            string observerTenant = tenant == "tenant-b" && scenario == "wrongTenant" ? "tenant-a" : tenant;
            Save($"sql-resume-{suffix}-observer.json", new { schema = "APP03_SETTLEMENT_SQL_OBSERVER_V1", source, runId,
                profile, tenant = observerTenant, pid = 11, status = "ready" });
            object[] samples = [
                new { @event = "start", profile, runId, tenant, pid = 11 },
                new { @event = "available" },
                new { @event = "sample", utc = start.AddMilliseconds(-100), truncated = false },
                new { @event = "sample", utc = end.AddMilliseconds(scenario == "gap" ? 1500 : lastTimeout && scenario == "lastTimeoutNoDrain" ? -10 : 100), truncated = false },
                new { @event = "stop", reason = "parentEnded" }
            ];
            File.WriteAllLines(Path.Combine(output, $"sql-resume-{suffix}.ndjson"), samples.Select(s => JsonSerializer.Serialize(s)));
        }
        var receipt = App03ResumeWitnessCoverage.Inspect(new(output, "source", "run"), "app03-5");
        Assert.Equal(expected, receipt.Status);
        Assert.Equal("REQUIRES_PROXY_AND_API_JOB_LOG_REVIEW", receipt.Correlation);
        Assert.Equal(2, receipt.Probes.Length);
        Assert.Equal(expected == "CLIENT_SQL_READY_FOR_REVIEW", receipt.Issues.Length == 0);
        if (scenario.StartsWith("lastTimeout", StringComparison.Ordinal))
        {
            var last = receipt.Probes.Single(p => p.Tenant == "tenant-b");
            Assert.Equal("HTTP_TIMEOUT", last.Outcome);
            Assert.Equal("NOT_RUN", last.Assertion);
            Assert.Equal(scenario == "lastTimeoutDrained" ? "BRACKETED" : "INCOMPLETE", last.Coverage);
        }
    }

    private void Save(string path, object row) => File.WriteAllText(Path.Combine(output, path), JsonSerializer.Serialize(row));
    public void Dispose() => Directory.Delete(output, recursive: true);
}
