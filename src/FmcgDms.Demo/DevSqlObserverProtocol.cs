using System.Text.Json;

namespace FmcgDms.Demo;

internal static class DevSqlObserverProtocol
{
    // Fixed profile/scope and phase labels only: never accept arbitrary paths or business identifiers.
    internal static bool IsScope(string profile, string scope) =>
        (profile is "app03-5" or "app03-6") && scope == "resume"
        || profile == "app02d2" && scope is ("D2-07-A" or "D2-07-B" or "D2-14-A" or "D2-14-B"
            or "D2-15-A" or "D2-15-B" or "A-to-B" or "B-to-A" or "initial")
        || (profile is "dev-f01" or "dev-f06") && scope == "initial";

    internal static bool IsD2Phase(string value) =>
        value is "initial" or "origin-seed" or "peer-seed" or "peer-checks" or "seed"
            or "ack" or "restart-lookup" or "replay-checks" or "channel-off" or "channel-on"
            or "evidence-foreign-seed" or "evidence-prepare"
            or "evidence-cash-missing" or "evidence-cash-amount" or "evidence-cash-pending"
            or "evidence-cash-future" or "evidence-cash-self" or "evidence-cash-foreign"
            or "evidence-bank-missing" or "evidence-bank-pending" or "evidence-credit-missing"
            or "evidence-bank-binding" or "evidence-credit-origin" or "evidence-credit-reserve"
            or "evidence-credit-excess" or "evidence-credit-foreign" or "evidence-posting"
            or "evidence-cash-reuse" or "evidence-credit-reuse";

    internal sealed record StartupProof(string Status, DateTimeOffset HandshakeUtc,
        DateTimeOffset ChildStartUtc, DateTimeOffset AvailableUtc,
        DateTimeOffset FirstSampleStartedUtc, DateTimeOffset FirstSampleUtc);

    internal sealed record StartupDecision(StartupProof? Proof, string Failure);

    // Read the first three flushed records from the owned child, not the async stdout marker.
    // Partial/malformed data is not proof. The caller fences admission with its monotonic deadline.
    internal static StartupProof? ReadStartup(string path, string source, string runId, string profile,
        string scope, string tenant, int pid, DateTimeOffset notBeforeUtc, DateTimeOffset observedUtc)
    {
        try
        {
            if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)) return null;
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            using var first = JsonDocument.Parse(reader.ReadLine() ?? "");
            using var second = JsonDocument.Parse(reader.ReadLine() ?? "");
            using var third = JsonDocument.Parse(reader.ReadLine() ?? "");
            var row = first.RootElement;
            if (row.GetProperty("event").GetString() != "start" ||
                row.GetProperty("schema").GetString() != "DEV_SQL_OBSERVER_V1" ||
                row.GetProperty("source").GetString() != source || row.GetProperty("pid").GetInt32() != pid ||
                row.GetProperty("observerPid").GetInt32() != pid ||
                row.GetProperty("profile").GetString() != profile || row.GetProperty("scope").GetString() != scope ||
                row.GetProperty("runId").GetString() != runId || row.GetProperty("tenant").GetString() != tenant)
                return null;
            var available = second.RootElement;
            var sample = third.RootElement;
            if (available.GetProperty("event").GetString() != "available" ||
                available.GetProperty("sampleIntervalMs").GetInt32() != 250 ||
                available.GetProperty("commandTimeoutMs").GetInt32() != 1000 ||
                sample.GetProperty("finalSample").GetBoolean()) return null;

            var handshakeUtc = row.GetProperty("handshakeUtc").GetDateTimeOffset();
            var childStartUtc = row.GetProperty("utc").GetDateTimeOffset();
            var availableUtc = available.GetProperty("utc").GetDateTimeOffset();
            var sampleStartUtc = sample.GetProperty("startedUtc").GetDateTimeOffset();
            var sampleUtc = sample.GetProperty("utc").GetDateTimeOffset();
            if (handshakeUtc < notBeforeUtc || childStartUtc < handshakeUtc ||
                availableUtc < childStartUtc || sampleStartUtc < availableUtc ||
                sampleUtc < sampleStartUtc || sampleUtc > observedUtc) return null;

            string status;
            if (sample.GetProperty("event").GetString() == "sample" &&
                sample.GetProperty("rows").ValueKind == JsonValueKind.Array &&
                sample.GetProperty("rows").GetArrayLength() <= 32)
                status = sample.GetProperty("truncated").GetBoolean() ? "startedWithoutSample" : "ready";
            else if (sample.GetProperty("event").GetString() == "sampleUnavailable" &&
                sample.GetProperty("sqlNumber").GetInt32() == -2 &&
                sample.GetProperty("reason").GetString() == "commandTimeout")
                status = "startedWithoutSample";
            else return null;
            return new(status, handshakeUtc, childStartUtc, availableUtc, sampleStartUtc, sampleUtc);
        }
        catch { return null; }
    }

    // No stdout dependency and no late admission: read/validate, then recheck the
    // same monotonic budget, caller cancellation and child liveness before returning.
    // Polling only reads the evidence file; it does not retry a request or SQL operation.
    internal static async Task<StartupDecision> WaitForStartup(Func<StartupProof?> readProof,
        Func<bool> childExited, Func<TimeSpan> elapsed, TimeSpan budget,
        Func<TimeSpan, CancellationToken, Task> delay, CancellationToken ct)
    {
        var poll = TimeSpan.FromMilliseconds(25);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (elapsed() >= budget) return new(null, "deadline");
            if (childExited()) return new(null, "childExited");
            var proof = readProof();
            ct.ThrowIfCancellationRequested();
            var remaining = budget - elapsed();
            if (remaining <= TimeSpan.Zero) return new(null, "deadline");
            if (childExited()) return new(null, "childExited");
            if (proof is not null) return new(proof, "none");
            await delay(remaining < poll ? remaining : poll, ct);
        }
    }

    // Every wait has an explicit bound. Returning unconfirmed is never a claim of
    // termination; the parent must fence scratch cleanup in that disposition.
    internal static async Task<string> Reap(Action requestStop, Action closeJob, Action kill,
        Func<Task> waitForExit, TimeSpan graceful, TimeSpan forced)
    {
        try
        {
            requestStop();
            await waitForExit().WaitAsync(graceful);
            return "exited";
        }
        catch { }
        try
        {
            closeJob();
            await waitForExit().WaitAsync(forced);
            return "jobClosed";
        }
        catch { }
        try
        {
            kill();
            await waitForExit().WaitAsync(forced);
            return "killed";
        }
        catch { return "unconfirmed"; }
    }
}
