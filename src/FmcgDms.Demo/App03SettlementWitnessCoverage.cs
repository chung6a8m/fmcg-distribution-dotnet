using System.Text.Json;

namespace FmcgDms.Demo;

// Diagnostic completeness only. API markers and source/run/attempt binding require hosted log review.
internal static class App03SettlementWitnessCoverage
{
    private static readonly string[] Checkpoints = ["DEV-20261124-A", "DEV-20261125-A", "DEV-20261126-A"];
    private const double MaxSampleGapMs = 1000;

    internal sealed record RequestCoverage(string Checkpoint, string? Id, string? Outcome,
        string Coverage, int Samples, int UnavailableSamples, double? LargestGapMs);
    internal sealed record Receipt(string Schema, string Source, string RunId, string Status,
        string ApiCorrelation, string? ObserverStatus, string? TerminalReason, int SampleCount,
        int UnavailableSampleCount, string[] Issues, RequestCoverage[] Requests);

    internal static void Write(App03SettlementHttpWitness.Context witness)
    {
        try
        {
            var receipt = Inspect(witness);
            using var file = File.Open(Path.Combine(witness.Output, "catalog-witness-coverage.json"), FileMode.CreateNew);
            JsonSerializer.Serialize(file, receipt, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        }
        catch { try { Console.Error.WriteLine("APP-03 catalog witness coverage unavailable."); } catch { } }
    }

    internal static Receipt Inspect(App03SettlementHttpWitness.Context witness)
    {
        var issues = new List<string>();
        var requests = new List<RequestCoverage>();
        var receipts = new List<(string File, JsonElement Row)>();
        var samples = new List<(DateTimeOffset Utc, bool Unusable)>();
        string? observerStatus = null, terminalReason = null;
        bool observerIdentified = false;
        try
        {
            string[] files = Directory.GetFiles(witness.Output, "catalog-http-*.json");
            if (files.Length != 6) issues.Add("HTTP_RECORD_COUNT");
            foreach (string file in files.Take(6))
                receipts.Add((Path.GetFileName(file), Parse(ReadBounded(file))));
        }
        catch { issues.Add("HTTP_RECORD_UNAVAILABLE"); }

        try
        {
            var startup = Parse(ReadBounded(Path.Combine(witness.Output, "sql-catalog-observer.json")));
            observerStatus = startup.GetProperty("status").GetString();
            int pid = startup.GetProperty("pid").GetInt32();
            if (startup.GetProperty("schema").GetString() != "APP03_SETTLEMENT_SQL_OBSERVER_V1"
                || startup.GetProperty("source").GetString() != witness.Source
                || startup.GetProperty("runId").GetString() != witness.RunId
                || startup.GetProperty("profile").GetString() != "app03-6" || pid <= 0
                || observerStatus is not ("ready" or "startedWithoutSample"))
                throw new InvalidDataException();
            var rows = ReadBounded(Path.Combine(witness.Output, "sql-reads.ndjson"))
                .Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(Parse).ToArray();
            if (rows.Length < 3 || Event(rows[0]) != "start"
                || rows[0].GetProperty("runId").GetString() != witness.RunId
                || rows[0].GetProperty("profile").GetString() != "app03-6"
                || rows[0].GetProperty("pid").GetInt32() != pid || Event(rows[1]) != "available")
                throw new InvalidDataException();
            observerIdentified = true;
            if (Event(rows[^1]) == "stop") terminalReason = rows[^1].GetProperty("reason").GetString();
            else if (Event(rows[^1]) == "unavailable") terminalReason = "unavailable";
            if (terminalReason != "parentEnded") issues.Add("SQL_TERMINAL_INCOMPLETE");
            foreach (var row in rows.Skip(2))
            {
                string? kind = Event(row);
                if (kind is "sample" or "sampleUnavailable")
                    samples.Add((row.GetProperty("utc").GetDateTimeOffset(),
                        kind == "sampleUnavailable" || row.GetProperty("truncated").GetBoolean()));
            }
            if (samples.Count == 0 || samples.All(s => s.Unusable)) issues.Add("SQL_NO_USABLE_SAMPLE");
            if (samples.Zip(samples.Skip(1)).Any(p => p.First.Utc > p.Second.Utc))
            {
                issues.Add("SQL_TIME_ORDER");
                observerIdentified = false;
            }
        }
        catch { issues.Add("SQL_OBSERVER_UNAVAILABLE"); observerIdentified = false; }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (string checkpoint in Checkpoints)
        {
            string? id = null, outcome = null;
            try
            {
                var pair = receipts.Where(r => r.Row.GetProperty("checkpoint").GetString() == checkpoint).ToArray();
                if (pair.Length != 2) throw new InvalidDataException();
                var start = pair.Single(r => r.File.EndsWith("-start.json", StringComparison.Ordinal));
                var end = pair.Single(r => r.File.EndsWith("-end.json", StringComparison.Ordinal));
                id = start.Row.GetProperty("id").GetString();
                if (id is null || !Guid.TryParseExact(id, "N", out var guid) || guid.ToString("N") != id
                    || !ids.Add(id) || start.File != $"catalog-http-{id}-start.json" || end.File != $"catalog-http-{id}-end.json")
                    throw new InvalidDataException();
                foreach (var row in pair.Select(r => r.Row))
                    if (row.GetProperty("schema").GetString() != "APP03_SETTLEMENT_HTTP_V1"
                        || row.GetProperty("source").GetString() != witness.Source || row.GetProperty("runId").GetString() != witness.RunId
                        || row.GetProperty("id").GetString() != id || row.GetProperty("route").GetString() != App03SettlementHttpWitness.Route
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
                int before = samples.FindLastIndex(s => s.Utc <= from && !s.Unusable);
                int after = samples.FindIndex(s => s.Utc >= to && !s.Unusable);
                if (!observerIdentified || before < 0 || after < before)
                {
                    requests.Add(new(checkpoint, id, outcome, "INCOMPLETE", 0, 0, null));
                    continue;
                }
                // Bracket fast requests; absence of a DMV row is never proof of no contention.
                var window = samples.GetRange(before, after - before + 1);
                double gap = Math.Max((from - window[0].Utc).TotalMilliseconds, (window[^1].Utc - to).TotalMilliseconds);
                for (int i = 1; i < window.Count; i++)
                    gap = Math.Max(gap, (window[i].Utc - window[i - 1].Utc).TotalMilliseconds);
                int unavailable = window.Count(s => s.Unusable);
                requests.Add(new(checkpoint, id, outcome,
                    unavailable == 0 && gap <= MaxSampleGapMs ? "BRACKETED" : "INCOMPLETE",
                    window.Count - unavailable, unavailable, gap));
            }
            catch
            {
                issues.Add("HTTP_PAIR_INVALID:" + checkpoint);
                requests.Add(new(checkpoint, id, outcome, "INCOMPLETE", 0, 0, null));
            }
        }
        if (requests.Any(r => r.Coverage != "BRACKETED")) issues.Add("SQL_REQUEST_COVERAGE_INCOMPLETE");
        return new("APP03_SETTLEMENT_WITNESS_COVERAGE_V1", witness.Source, witness.RunId,
            issues.Count == 0 ? "CLIENT_SQL_READY_FOR_REVIEW" : "DIAGNOSTIC_EVIDENCE_INCOMPLETE",
            "REQUIRES_JOB_LOG_REVIEW", observerStatus, terminalReason,
            samples.Count(s => !s.Unusable), samples.Count(s => s.Unusable), issues.ToArray(), requests.ToArray());
    }

    private static string? Event(JsonElement row) => row.GetProperty("event").GetString();
    private static JsonElement Parse(string value)
    {
        using var document = JsonDocument.Parse(value);
        return document.RootElement.Clone();
    }
    private static string ReadBounded(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists || file.Length > 2 * 1024 * 1024 || file.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException();
        return File.ReadAllText(path);
    }
}
