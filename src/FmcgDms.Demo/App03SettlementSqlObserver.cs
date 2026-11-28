namespace FmcgDms.Demo;

// Post-browser observation uses a separately killable managed child; browser
// PowerShell evidence remains unchanged and no business path is instrumented by default.
internal static class App03SettlementSqlObserver
{
    internal static bool Enabled => DevSqlObserver.Hosted &&
        Environment.GetEnvironmentVariable("FMCG_APP03_SETTLEMENT_WITNESS") == "1";

    internal static App03SettlementHttpWitness.Context? TryCreateContext(string root, string runId,
        string output, string source, string profile = "app03-6")
    {
        if (!Enabled || profile is not ("app03-5" or "app03-6")) return null;
        string? directory = DevSqlObserver.EvidenceDirectory(root, runId, profile, "resume");
        return directory is not null && string.Equals(Path.GetFullPath(output), directory, StringComparison.OrdinalIgnoreCase)
            ? new(directory, source, runId) : null;
    }

    internal static Task<DevSqlObserver?> Start(string root, string runId, string output,
        string source, CancellationToken ct, string profile = "app03-6", string tenant = "tenant-a") =>
        TryCreateContext(root, runId, output, source, profile) is null
            ? Task.FromResult<DevSqlObserver?>(null)
            : DevSqlObserver.Start(root, source, runId, profile, "resume", tenant, ct);
}
