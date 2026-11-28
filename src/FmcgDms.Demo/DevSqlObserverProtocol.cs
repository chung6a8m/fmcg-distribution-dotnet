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

    internal static bool VerifyStartup(string path, string runId, string profile, string scope, string tenant, int pid, out string status)
    {
        status = "unavailable";
        try
        {
            if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)) return false;
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            // The child sends its ready marker only after writing and flushing all three rows.
            using var first = JsonDocument.Parse(reader.ReadLine() ?? "");
            using var second = JsonDocument.Parse(reader.ReadLine() ?? "");
            using var third = JsonDocument.Parse(reader.ReadLine() ?? "");
            var row = first.RootElement;
            if (row.GetProperty("event").GetString() != "start" || row.GetProperty("pid").GetInt32() != pid ||
                row.GetProperty("profile").GetString() != profile || row.GetProperty("scope").GetString() != scope ||
                row.GetProperty("runId").GetString() != runId || row.GetProperty("tenant").GetString() != tenant ||
                second.RootElement.GetProperty("event").GetString() != "available") return false;
            var sample = third.RootElement;
            status = sample.GetProperty("event").GetString() switch
            {
                "sample" when !sample.GetProperty("truncated").GetBoolean() => "ready",
                "sample" or "sampleUnavailable" => "startedWithoutSample",
                _ => "unavailable"
            };
            return status != "unavailable";
        }
        catch { return false; }
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
