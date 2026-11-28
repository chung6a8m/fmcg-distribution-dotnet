namespace FmcgDms.Api.Persistence;

/// <summary>
/// Single-reader, in-memory hints for at most the two synthetic tenants.
/// No receipt is owned by a timer; restart reconstructs hints from SQL.
/// </summary>
internal sealed class App02a3DueWakeSchedule
{
    internal readonly record struct Wake(string Tenant, DateTimeOffset At, bool RefreshOnly);
    internal static readonly TimeSpan OverdueYield = TimeSpan.FromMilliseconds(250);
    internal static readonly TimeSpan ReadFailureDelay = TimeSpan.FromSeconds(5);
    private readonly Dictionary<string, Wake> wakes = new(StringComparer.Ordinal);

    private static void CheckTenant(string tenant)
    {
        if (tenant is not ("tenant-a" or "tenant-b"))
            throw new ArgumentOutOfRangeException(nameof(tenant));
    }

    internal DateTimeOffset? Next => wakes.Values.Select(x => (DateTimeOffset?)x.At).Min();

    internal void SetFromSql(string tenant, DateTimeOffset? due,
        DateTimeOffset now, bool sweepFailed = false)
    {
        CheckTenant(tenant);
        if (due is null)
        {
            wakes.Remove(tenant);
            return;
        }
        // The SQL row remains the fence. Yield for an already-due remainder
        // (e.g. TOP(100) batch), and bound rediscovery after a failed sweep.
        // Neither delay changes the persisted retry or permits an early claim.
        DateTimeOffset at = due.Value;
        if (at <= now)
            at = now.Add(sweepFailed ? ReadFailureDelay : OverdueYield);
        wakes[tenant] = new Wake(tenant, at, RefreshOnly: false);
    }

    internal void SetReadFailure(string tenant, DateTimeOffset now)
    {
        CheckTenant(tenant);
        // Do not assume an unreadable SQL schedule means there is work due.
        // Retry only the read, independently of business retry accounting.
        wakes[tenant] = new Wake(tenant, now.Add(ReadFailureDelay), RefreshOnly: true);
    }

    internal Wake? TakeDue(DateTimeOffset now)
    {
        Wake? result = null;
        foreach (Wake wake in wakes.Values)
            if (wake.At <= now && (result is null || wake.At < result.Value.At))
                result = wake;
        if (result is { } ready)
            wakes.Remove(ready.Tenant);
        return result;
    }
}
