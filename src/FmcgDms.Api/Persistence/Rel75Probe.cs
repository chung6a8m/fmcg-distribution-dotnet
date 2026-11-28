using Microsoft.Data.SqlClient;
using System.Text.RegularExpressions;

namespace FmcgDms.Api.Persistence;

/// <summary>REL-75.4 experiment checkpoints; never an API or a fault injector.
/// Only an explicitly launched IntegrationTest synthetic child on its owned
/// LocalDB scratch can rendezvous with pre-created local kernel events.
/// No SQL changes, arbitrary paths, sleeps in the ordinary runtime, or retries.
/// </summary>
internal static class Rel75Probe
{
    private static readonly string[] Phases =
        ["SweepBeforeSelect", "CBeforePrerequisite", "PAfterRole", "PBeforeCommit", "OutboxAfterClaimRead"];

    internal static bool Enabled(IConfiguration config, IHostEnvironment host) =>
        host.IsEnvironment("IntegrationTest") && config.GetValue<bool>("SyntheticAuth:Enabled")
        && config.GetValue<bool>("Rel75Probe:Enabled");

    internal static async Task CheckpointAsync(IConfiguration config, IHostEnvironment host,
        SqlConnection db, string tenant, string phase, CancellationToken ct)
    {
        if (!Enabled(config, host)) return;
        string? nonce = config["Rel75Probe:Nonce"];
        string? target = config["Rel75Probe:Tenant"];
        string[] selected = (config["Rel75Probe:Phases"] ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries);
        if (!OperatingSystem.IsWindows() || nonce is null
            || !Regex.IsMatch(nonce, @"\A[a-f0-9]{32}\z", RegexOptions.CultureInvariant)
            || target is not ("tenant-a" or "tenant-b")
            || selected.Any(p => !Phases.Contains(p, StringComparer.Ordinal))
            || selected.Distinct(StringComparer.Ordinal).Count() != selected.Length
            || !db.DataSource.StartsWith(@"(localdb)\", StringComparison.OrdinalIgnoreCase)
            || tenant is not ("tenant-a" or "tenant-b")
            || db.Database != $"Fmcg_App00_{nonce}_{(tenant == "tenant-a" ? "A" : "B")}")
            throw new InvalidOperationException("REL75_INVALID_PROBE_BOUNDARY");
        if (target != tenant || !selected.Contains(phase, StringComparer.Ordinal)) return;

        string prefix = $@"Local\FmcgRel75-{nonce}-{tenant}-{phase}";
        using var reached = EventWaitHandle.OpenExisting(prefix + "-reached");
        using var release = EventWaitHandle.OpenExisting(prefix + "-release");
        reached.Set();
        int result = await Task.Run(() => WaitHandle.WaitAny([release, ct.WaitHandle],
            TimeSpan.FromSeconds(12)), ct);
        ct.ThrowIfCancellationRequested();
        if (result != 0) throw new InvalidOperationException("REL75_BARRIER_DEADLINE");
    }
}
