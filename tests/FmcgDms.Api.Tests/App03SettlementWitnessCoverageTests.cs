using System.Text.Json;
using FmcgDms.Demo;
using Xunit;

namespace FmcgDms.Api.Tests;

public sealed class App03SettlementWitnessCoverageTests : IDisposable
{
    private readonly string output = Directory.CreateTempSubdirectory("app03-coverage-").FullName;
    private static readonly DateTimeOffset Origin = new(2026, 11, 28, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Complete_pairs_and_bracketed_samples_are_only_ready_for_review()
    {
        Build();
        var result = Inspect();
        Assert.Equal("CLIENT_SQL_READY_FOR_REVIEW", result.Status);
        Assert.Equal("REQUIRES_JOB_LOG_REVIEW", result.ApiCorrelation);
        Assert.Equal("parentEnded", result.TerminalReason);
        Assert.Empty(result.Issues);
        Assert.Equal(3, result.Requests.Length);
        Assert.All(result.Requests, request => Assert.Equal("BRACKETED", request.Coverage));
    }

    [Theory]
    [InlineData("sampleUnavailable")]
    [InlineData("gap")]
    [InlineData("truncated")]
    [InlineData("missingStop")]
    [InlineData("wrongPid")]
    [InlineData("missingPair")]
    public void Incomplete_evidence_is_not_promoted_to_ready(string scenario)
    {
        Build(scenario);
        var result = Inspect();
        Assert.Equal("DIAGNOSTIC_EVIDENCE_INCOMPLETE", result.Status);
        Assert.NotEmpty(result.Issues);
        Assert.Equal("REQUIRES_JOB_LOG_REVIEW", result.ApiCorrelation);
    }

    private App03SettlementWitnessCoverage.Receipt Inspect() =>
        App03SettlementWitnessCoverage.Inspect(new(output, "source", "run"));

    private void Build(string? scenario = null)
    {
        var rows = new List<object>
        {
            new { @event = "start", profile = "app03-6", runId = "run", pid = scenario == "wrongPid" ? 12 : 11 },
            new { @event = "available" }
        };
        Save("sql-catalog-observer.json", new { schema = "APP03_SETTLEMENT_SQL_OBSERVER_V1", source = "source",
            runId = "run", profile = "app03-6", pid = 11, status = "ready" });
        for (int i = 0; i < 3; i++)
        {
            string checkpoint = $"DEV-202611{24 + i}-A";
            string id = Guid.NewGuid().ToString("N");
            var startedUtc = Origin.AddSeconds(i * 5 + 1);
            var endedUtc = startedUtc.AddMilliseconds(300);
            Save($"catalog-http-{id}-start.json", new { schema = "APP03_SETTLEMENT_HTTP_V1", source = "source",
                runId = "run", checkpoint, id, route = App03SettlementHttpWitness.Route, method = "GET",
                apiPid = 1, vitePid = 2, startedUtc, timeoutMs = 3000 });
            if (!(i == 1 && scenario == "missingPair"))
                Save($"catalog-http-{id}-end.json", new { schema = "APP03_SETTLEMENT_HTTP_V1", source = "source",
                    runId = "run", checkpoint, id, route = App03SettlementHttpWitness.Route, method = "GET",
                    apiPid = 1, vitePid = 2, startedUtc, endedUtc, timeoutMs = 3000, elapsedMs = 300, outcome = "SUCCESS" });
            rows.Add(new { @event = "sample", utc = startedUtc.AddMilliseconds(-100), truncated = false });
            if (i == 1 && scenario == "sampleUnavailable")
                rows.Add(new { @event = "sampleUnavailable", utc = startedUtc.AddMilliseconds(100) });
            if (i == 1 && scenario == "truncated")
                rows.Add(new { @event = "sample", utc = startedUtc.AddMilliseconds(100), truncated = true });
            rows.Add(new { @event = "sample", utc = endedUtc.AddMilliseconds(i == 1 && scenario == "gap" ? 1500 : 100), truncated = false });
        }
        if (scenario != "missingStop") rows.Add(new { @event = "stop", reason = "parentEnded" });
        File.WriteAllLines(Path.Combine(output, "sql-reads.ndjson"), rows.Select(row => JsonSerializer.Serialize(row)));
    }

    private void Save(string name, object value) => File.WriteAllText(Path.Combine(output, name), JsonSerializer.Serialize(value));
    public void Dispose() => Directory.Delete(output, recursive: true);
}
