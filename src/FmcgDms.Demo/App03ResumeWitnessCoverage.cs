using System.Text.Json;

namespace FmcgDms.Demo;

// A missing observation is not a successful probe or proof of absence of SQL contention.
internal static class App03ResumeWitnessCoverage
{
    internal sealed record Probe(string Checkpoint, string Tenant, string? Id, string? Outcome,
        int? Status, string? Assertion, string Coverage, double? LargestGapMs);
    internal sealed record Receipt(string Schema, string Source, string RunId, string Profile, string Status,
        string Correlation, string[] Issues, Probe[] Probes);

    internal static void Write(App03SettlementHttpWitness.Context witness, string profile)
    {
        try
        {
            using var file = File.Open(Path.Combine(witness.Output, "resume-witness-coverage.json"), FileMode.CreateNew);
            JsonSerializer.Serialize(file, Inspect(witness, profile), new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        }
        catch { try { Console.Error.WriteLine("APP-03 resume coverage unavailable."); } catch { } }
    }

    internal static Receipt Inspect(App03SettlementHttpWitness.Context witness, string profile)
    {
        string[] checkpoints = profile switch
        {
            "app03-5" => ["nextday-1"],
            "app03-6" => ["nextday-1", "nextday-2", "nextday-3", "same-day-final"],
            _ => throw new ArgumentException("Unknown resume witness profile.")
        };
        var issues = new List<string>();
        var probes = new List<Probe>();
        var rows = new List<(string File, JsonElement Row)>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            var paths = Directory.GetFiles(witness.Output, "resume-session-http-*.json");
            if (paths.Length != checkpoints.Length * 4) issues.Add("HTTP_RECORD_COUNT");
            if (paths.Length <= checkpoints.Length * 4)
                foreach (string path in paths) rows.Add((Path.GetFileName(path), Read(path)));
        }
        catch { issues.Add("HTTP_RECORD_UNAVAILABLE"); }

        foreach (string tenant in new[] { "tenant-a", "tenant-b" })
        {
            List<(DateTimeOffset Utc, bool Unusable)>? samples = null;
            try
            {
                bool legacy = profile == "app03-6" && tenant == "tenant-a";
                string suffix = tenant == "tenant-a" ? "a" : "b";
                var startup = Read(Path.Combine(witness.Output, legacy ? "sql-catalog-observer.json" : $"sql-resume-{suffix}-observer.json"));
                if (startup.GetProperty("schema").GetString() != "APP03_SETTLEMENT_SQL_OBSERVER_V1"
                    || startup.GetProperty("source").GetString() != witness.Source
                    || startup.GetProperty("runId").GetString() != witness.RunId
                    || startup.GetProperty("profile").GetString() != profile || startup.GetProperty("tenant").GetString() != tenant
                    || startup.GetProperty("status").GetString() is not ("ready" or "startedWithoutSample"))
                    throw new InvalidDataException();
                int pid = startup.GetProperty("pid").GetInt32();
                var sql = ReadText(Path.Combine(witness.Output, legacy ? "sql-reads.ndjson" : $"sql-resume-{suffix}.ndjson"))
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(Parse).ToArray();
                if (pid <= 0 || sql.Length < 3 || Event(sql[0]) != "start" || Event(sql[1]) != "available"
                    || sql[0].GetProperty("pid").GetInt32() != pid || sql[0].GetProperty("runId").GetString() != witness.RunId
                    || sql[0].GetProperty("profile").GetString() != profile || sql[0].GetProperty("tenant").GetString() != tenant
                    || Event(sql[^1]) != "stop" || sql[^1].GetProperty("reason").GetString() != "parentEnded")
                    throw new InvalidDataException();
                var observations = sql.Skip(2).Where(r => Event(r) is "sample" or "sampleUnavailable")
                    .Select(r => (Utc: r.GetProperty("utc").GetDateTimeOffset(),
                        Unusable: Event(r) == "sampleUnavailable" || r.GetProperty("truncated").GetBoolean())).ToList();
                if (observations.Count == 0 || observations.Zip(observations.Skip(1)).Any(p => p.First.Utc > p.Second.Utc))
                    throw new InvalidDataException();
                samples = observations;
            }
            catch { issues.Add("SQL_OBSERVER_INCOMPLETE:" + tenant); }

            foreach (string checkpoint in checkpoints)
            {
                string? id = null, outcome = null, assertion = null;
                int? status = null;
                double? gap = null;
                string coverage = "INCOMPLETE";
                try
                {
                    var pair = rows.Where(r => r.Row.GetProperty("checkpoint").GetString() == checkpoint
                        && r.Row.GetProperty("tenant").GetString() == tenant).ToArray();
                    if (pair.Length != 2) throw new InvalidDataException();
                    var start = pair.Single(r => r.File.EndsWith("-start.json", StringComparison.Ordinal));
                    var end = pair.Single(r => r.File.EndsWith("-end.json", StringComparison.Ordinal));
                    id = start.Row.GetProperty("id").GetString();
                    if (id is null || !Guid.TryParseExact(id, "N", out var guid) || guid.ToString("N") != id || !ids.Add(id)
                        || start.File != $"resume-session-http-{id}-start.json" || end.File != $"resume-session-http-{id}-end.json")
                        throw new InvalidDataException();
                    foreach (var row in pair.Select(r => r.Row))
                        if (row.GetProperty("schema").GetString() != "APP03_RESUME_HTTP_V1" || row.GetProperty("source").GetString() != witness.Source
                            || row.GetProperty("runId").GetString() != witness.RunId || row.GetProperty("id").GetString() != id
                            || row.GetProperty("route").GetString() != App03SettlementHttpWitness.SessionRoute
                            || row.GetProperty("method").GetString() != "GET" || row.GetProperty("timeoutMs").GetDouble() != 3000
                            || row.GetProperty("apiPid").GetInt32() <= 0 || row.GetProperty("vitePid").GetInt32() <= 0)
                            throw new InvalidDataException();
                    var from = start.Row.GetProperty("startedUtc").GetDateTimeOffset();
                    var to = end.Row.GetProperty("endedUtc").GetDateTimeOffset();
                    if (end.Row.GetProperty("startedUtc").GetDateTimeOffset() != from || to < from
                        || end.Row.GetProperty("elapsedMs").GetDouble() < 0
                        || start.Row.GetProperty("apiPid").GetInt32() != end.Row.GetProperty("apiPid").GetInt32()
                        || start.Row.GetProperty("vitePid").GetInt32() != end.Row.GetProperty("vitePid").GetInt32())
                        throw new InvalidDataException();
                    outcome = end.Row.GetProperty("outcome").GetString();
                    var statusValue = end.Row.GetProperty("status");
                    if (statusValue.ValueKind != JsonValueKind.Null) status = statusValue.GetInt32();
                    assertion = end.Row.GetProperty("assertion").GetString();
                    if (outcome == "RESPONSE_RECEIVED")
                    {
                        if (status is null || status < 100 || status > 599 || assertion is not ("PASSED" or "EXPECTED_FORBIDDEN" or "HTTP_STATUS_REJECTED" or "ASSERTION_FAILED"))
                            throw new InvalidDataException();
                        if ((assertion == "EXPECTED_FORBIDDEN" && status != 403)
                            || (assertion == "PASSED" && (status < 200 || status > 299))
                            || (assertion == "HTTP_STATUS_REJECTED" && status >= 200 && status <= 299))
                            throw new InvalidDataException();
                        var received = end.Row.GetProperty("responseReceivedUtc").GetDateTimeOffset();
                        if (received < from || received > to || end.Row.GetProperty("responseElapsedMs").GetDouble() < 0)
                            throw new InvalidDataException();
                    }
                    else if (outcome is not ("HTTP_TIMEOUT" or "CALLER_CANCELLED" or "CANCELLED_UNKNOWN"
                            or "HTTP_TRANSPORT_FAILURE" or "HTTP_STATUS_FAILURE" or "UNEXPECTED_FAILURE")
                        || assertion != "NOT_RUN"
                        || (outcome == "HTTP_STATUS_FAILURE" ? status is null || status < 100 || status > 599 : status is not null)
                        || end.Row.GetProperty("responseReceivedUtc").ValueKind != JsonValueKind.Null
                        || end.Row.GetProperty("responseElapsedMs").ValueKind != JsonValueKind.Null)
                        throw new InvalidDataException();
                    if (samples is not null)
                    {
                        int before = samples.FindLastIndex(s => s.Utc <= from && !s.Unusable);
                        int after = samples.FindIndex(s => s.Utc >= to && !s.Unusable);
                        if (before >= 0 && after >= before)
                        {
                            var window = samples.GetRange(before, after - before + 1);
                            double largest = Math.Max((from - window[0].Utc).TotalMilliseconds, (window[^1].Utc - to).TotalMilliseconds);
                            for (int i = 1; i < window.Count; i++)
                                largest = Math.Max(largest, (window[i].Utc - window[i - 1].Utc).TotalMilliseconds);
                            gap = largest;
                            if (largest <= 1000 && window.All(s => !s.Unusable)) coverage = "BRACKETED";
                        }
                    }
                }
                catch { issues.Add("HTTP_PAIR_INVALID_OR_MISSING:" + checkpoint + ":" + tenant); }
                if (coverage != "BRACKETED") issues.Add("SQL_REQUEST_COVERAGE_INCOMPLETE:" + checkpoint + ":" + tenant);
                probes.Add(new(checkpoint, tenant, id, outcome, status, assertion, coverage, gap));
            }
        }
        return new("APP03_RESUME_WITNESS_COVERAGE_V1", witness.Source, witness.RunId, profile,
            issues.Count == 0 ? "CLIENT_SQL_READY_FOR_REVIEW" : "DIAGNOSTIC_EVIDENCE_INCOMPLETE",
            "REQUIRES_PROXY_AND_API_JOB_LOG_REVIEW", issues.ToArray(), probes.ToArray());
    }

    private static string? Event(JsonElement row) => row.GetProperty("event").GetString();
    private static JsonElement Read(string path) => Parse(ReadText(path));
    private static JsonElement Parse(string value)
    {
        using var document = JsonDocument.Parse(value);
        return document.RootElement.Clone();
    }

    private static string ReadText(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists || file.Length > 2 * 1024 * 1024 || file.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException();
        return File.ReadAllText(path);
    }
}
