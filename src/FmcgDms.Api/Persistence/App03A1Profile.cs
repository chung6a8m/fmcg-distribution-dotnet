using Dapper;
using FmcgDms.Api.Tenancy;
using Microsoft.Data.SqlClient;

namespace FmcgDms.Api.Persistence;

public static class App03A1Profile
{
    public const string Name = "APP-03.1a-v1";
    private const string ConfigurationKey = "App03:Profile";
    internal const long BaseVersion = 202611220010;
    // F01 is independent of the F03 -> F04 -> F05 dependency chain and is additive on DEV-F05.
    internal static readonly (string Tag, long Version)[] Features =
    [
        (App02DevF03Schema.Profile, 202611230004),
        (App02DevF04Schema.Profile, 202611230005),
        (App02DevF05Schema.Profile, 202611230006),
        (App02DevF01Schema.Profile, 202611230002)
    ];

    internal static bool Enabled(IConfiguration config, IHostEnvironment host)
    {
        var profile = config[ConfigurationKey];
        bool visit = config.GetValue<bool>("DevF01:Enabled");
        if (string.IsNullOrEmpty(profile))
        {
            if (visit && (host.IsDevelopment() || host.IsEnvironment("IntegrationTest"))
                && Enumerable.Range(2, 7).Any(i => config.GetValue<bool>($"DevF{i:00}:Enabled")))
                throw new InvalidOperationException($"Combining DEV-F01 with other F features requires the {Name} profile.");
            return false;
        }
        if (profile != Name || (!host.IsDevelopment() && !host.IsEnvironment("IntegrationTest")))
            throw new InvalidOperationException("Unsupported APP-03 DEV profile or hosting environment.");
        if (!config.GetValue<bool>("SyntheticAuth:Enabled") || !config.GetValue<bool>("App02C2:DedicatedRuntime")
            || Enumerable.Range(1, 5).Any(i => !config.GetValue<bool>($"App02C{i}:Enabled"))
            || Features.Any(f => !config.GetValue<bool>($"{f.Tag}:Enabled")))
            throw new InvalidOperationException($"{Name} requires synthetic auth, dedicated C.1–C.5 and DEV-F01/F03/F04/F05.");
        if (new[] { "DevF02:Enabled", "DevF06:Enabled", "DevF07:Enabled", "DevF08:Enabled",
            "App02D1:Enabled", "App02D2:Enabled", "App02D3:Enabled", "App02D4:Enabled", "App02Worker:Enabled" }
            .Any(key => config.GetValue<bool>(key)) || !string.IsNullOrEmpty(config["App02D4:Profile"]))
            throw new InvalidOperationException($"{Name} does not support D, the worker or DEV-F02/F06/F07/F08.");
        return true;
    }

    internal static async Task VerifyTenantCatalog(TenantConnections connections, string? nonce, CancellationToken ct)
    {
        if (!Guid.TryParseExact(nonce, "N", out var runId) || runId.ToString("N") != nonce)
            throw new InvalidOperationException($"{Name} requires a lowercase GUID-N DEV session nonce.");
        foreach (string tenant in new[] { "tenant-a", "tenant-b" })
        {
            await using var db = await connections.OpenApp02c4Async(App02a3Sql.WorkerPrincipal(tenant), ct);
            var state = await ReadSchema(db, nonce, ct);
            RequireSchema(state, complete: true);
        }
    }

    internal static Task<SchemaState> ReadSchema(SqlConnection db, string nonce, CancellationToken ct = default) =>
        db.QuerySingleAsync<SchemaState>(new CommandDefinition("""
            IF OBJECT_ID(N'dbo.LocalDemoOwner', N'U') IS NULL OR OBJECT_ID(N'dbo.VersionInfo', N'U') IS NULL
                SELECT CAST(0 AS bit) AS Owned, CAST(0 AS bit) AS HasBase, '' AS FeatureVersions,
                       CAST(0 AS bit) AS HasFeatureTables;
            ELSE
                SELECT CAST(CASE WHEN EXISTS (SELECT 1 FROM dbo.LocalDemoOwner WHERE RunId=@nonce) THEN 1 ELSE 0 END AS bit) AS Owned,
                       CAST(CASE WHEN (SELECT COUNT(*) FROM dbo.VersionInfo
                           WHERE Version IN (202611220001,202611220002,202611220003,202611220004,@BaseVersion))=5
                           THEN 1 ELSE 0 END AS bit) AS HasBase,
                       COALESCE((SELECT STRING_AGG(CONVERT(varchar(max),Version), ',') FROM dbo.VersionInfo
                           WHERE Version>@BaseVersion), '') AS FeatureVersions,
                       CAST(CASE WHEN OBJECT_ID(N'dbo.F01Visit',N'U') IS NOT NULL
                           AND OBJECT_ID(N'dbo.F01CustomerScope',N'U') IS NOT NULL
                           AND OBJECT_ID(N'dbo.F03LotIssue',N'U') IS NOT NULL
                           AND OBJECT_ID(N'dbo.F04DeliveryPricing',N'U') IS NOT NULL
                           AND OBJECT_ID(N'dbo.F05ReturnCase',N'U') IS NOT NULL
                           AND OBJECT_ID(N'dbo.F05ReturnStage',N'U') IS NOT NULL
                           THEN 1 ELSE 0 END AS bit) AS HasFeatureTables;
            """, new { nonce, BaseVersion }, cancellationToken: ct));

    internal static void RequireSchema(SchemaState state, bool complete)
    {
        var installed = state.FeatureVersions.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(long.Parse).ToHashSet();
        if (!state.Owned || !state.HasBase || (complete && !state.HasFeatureTables) ||
            !installed.SetEquals(Features.Take(complete ? Features.Length : installed.Count).Select(f => f.Version)))
            throw new InvalidOperationException(complete
                ? $"{Name} requires its complete schema in both owned tenant databases."
                : $"{Name} migration requires an owned C.5 scratch baseline with a supported DEV-F migration history.");
    }

    internal sealed record SchemaState(bool Owned, bool HasBase, string FeatureVersions, bool HasFeatureTables);
}
