using Dapper;
using FmcgDms.Api.Tenancy;
using Microsoft.Data.SqlClient;

namespace FmcgDms.Api.Persistence;

public static class App03A1Profile
{
    public const string Name = "APP-03.1a-v1";
    public const string SettlementName = "APP-03.6-v1";
    public const string FinanceName = "APP-03.7-v1";
    public const string CloseReadinessName = "APP-05.1-v1";
    public const string SuccessorName = "APP-05.3-v1";
    public const string TripClosureName = "APP-05.2-v1";
    public const string CloseReadinessBootstrapVersion = "APP-05.1-DAILY-CLOSE-v1";
    public const string FieldName = "APP-03.5-v1";
    public const string DayBootstrapVersion = "APP-03.2-VND-v1";
    internal const string DayBootstrapKey = "App03:DayBootstrap";
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
        var bootstrap = config[DayBootstrapKey];
        bool supported = profile is Name or FieldName or SettlementName or FinanceName or CloseReadinessName or TripClosureName or SuccessorName;
        if (bootstrap is not null && (bootstrap != DayBootstrapVersion || !supported
            || App02c6Clock.GetConfiguredPath(config, host) is null))
            throw new InvalidOperationException("APP-03.2 requires its supported bootstrap version and a dedicated APP-03 clock.");
        bool visit = config.GetValue<bool>("DevF01:Enabled");
        if (string.IsNullOrEmpty(profile))
        {
            if (visit && (host.IsDevelopment() || host.IsEnvironment("IntegrationTest"))
                && Enumerable.Range(2, 7).Any(i => config.GetValue<bool>($"DevF{i:00}:Enabled")))
                throw new InvalidOperationException($"Combining DEV-F01 with other F features requires the {Name} profile.");
            return false;
        }
        if (!supported || (!host.IsDevelopment() && !host.IsEnvironment("IntegrationTest")))
            throw new InvalidOperationException("Unsupported APP-03 DEV profile or hosting environment.");
        if (!config.GetValue<bool>("SyntheticAuth:Enabled") || !config.GetValue<bool>("App02C2:DedicatedRuntime")
            || Enumerable.Range(1, 5).Any(i => !config.GetValue<bool>($"App02C{i}:Enabled"))
            || Features.Any(f => !config.GetValue<bool>($"{f.Tag}:Enabled")))
            throw new InvalidOperationException($"{Name} requires synthetic auth, dedicated C.1–C.5 and DEV-F01/F03/F04/F05.");
        bool finance = profile is FinanceName or CloseReadinessName or TripClosureName or SuccessorName;
        bool settlement = finance || profile == SettlementName;
        bool field = settlement || profile == FieldName;
        if (field && (bootstrap != DayBootstrapVersion || !config.GetValue<bool>("App03:Workspace")
            || !config.GetValue<bool>("App02D1:Enabled") || !config.GetValue<bool>("App02D2:Enabled")))
            throw new InvalidOperationException($"{FieldName} requires the dedicated workspace, business day, D.1 and D.2.");
        string[] settlementFlags = ["DevF02:Enabled", "DevF07:Enabled", "App02D3:Enabled"];
        if (settlement && settlementFlags.Any(key => !config.GetValue<bool>(key)))
            throw new InvalidOperationException($"{SettlementName} requires D.3, DEV-F02 and DEV-F07.");
        string[] financeFlags = ["DevF06:Enabled", "DevF08:Enabled", "App02D4:Enabled"];
        if (finance && (!host.IsEnvironment("IntegrationTest") ||
            financeFlags.Any(key => !config.GetValue<bool>(key)) || config["App02D4:Profile"] != "D4-v0.1"))
            throw new InvalidOperationException($"{FinanceName} requires guarded D.4 and DEV-F06/F08.");
        if ((!settlement && settlementFlags.Any(key => config.GetValue<bool>(key))) || (!field && (config.GetValue<bool>("App02D1:Enabled") || config.GetValue<bool>("App02D2:Enabled"))) ||
            config.GetValue<bool>("App02Worker:Enabled") || (!finance &&
            (financeFlags.Any(key => config.GetValue<bool>(key)) || !string.IsNullOrEmpty(config["App02D4:Profile"]))))
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
            RequireSchema(state, complete: true, field: connections.UsesApp03Field, settlement: connections.UsesApp03Settlement,
                finance: connections.UsesApp03Finance, closeReadiness: connections.UsesApp05CloseReadiness,
                tripClosure: connections.UsesApp05TripClosure, successor: connections.UsesApp05Successor);
            if (connections.UsesApp03Settlement) await VerifySettlementSchema(db, ct);
            if (connections.UsesApp03Finance && !await db.ExecuteScalarAsync<bool>(new CommandDefinition("""
                SELECT CONVERT(bit, CASE WHEN OBJECT_ID(N'd4app.FinanceQueueProvenance',N'V') IS NOT NULL
                    AND OBJECT_ID(N'd4app.FinanceCases',N'V') IS NOT NULL
                    AND OBJECT_ID(N'd4app.FinanceCaseLinks',N'V') IS NOT NULL
                    AND OBJECT_ID(N'd4app.FinanceCorrectionPosting',N'V') IS NOT NULL
                    AND OBJECT_ID(N'dbo.F06Period',N'U') IS NOT NULL AND OBJECT_ID(N'dbo.F08Treasury',N'U') IS NOT NULL
                    THEN 1 ELSE 0 END);
                """, cancellationToken: ct)))
                throw new InvalidOperationException($"{FinanceName} requires the complete Finance schema in both tenants.");
            if (connections.UsesApp03Field && !await db.ExecuteScalarAsync<bool>(new CommandDefinition("""
                SELECT CONVERT(bit, CASE WHEN OBJECT_ID(N'dbo.App03BSalePricing',N'U') IS NOT NULL THEN 1 ELSE 0 END);
                """, cancellationToken: ct)))
                throw new InvalidOperationException($"{FieldName} requires the D.1/D.2 and B pricing schema in both tenants.");
            if (connections.UsesApp03BusinessDay && !await db.ExecuteScalarAsync<bool>(new CommandDefinition("""
                IF OBJECT_ID(N'dbo.LocalApp03Bootstrap',N'U') IS NULL SELECT CAST(0 AS bit);
                ELSE SELECT CAST(CASE WHEN EXISTS(SELECT 1 FROM dbo.LocalApp03Bootstrap WHERE Slot=1 AND Version=@Version)
                    THEN 1 ELSE 0 END AS bit);
                """, new { Version = DayBootstrapVersion }, cancellationToken: ct)))
                throw new InvalidOperationException("APP-03.2 requires its installed bootstrap in both tenant databases.");
            if (connections.UsesApp05CloseReadiness && !await db.ExecuteScalarAsync<bool>(new CommandDefinition("""
                IF OBJECT_ID(N'dbo.LocalApp05Bootstrap',N'U') IS NULL
                    OR OBJECT_ID(N'dbo.LocalApp05TripBootstrap',N'U') IS NULL
                    OR OBJECT_ID(N'dbo.App05TripOperator',N'U') IS NULL
                    OR OBJECT_ID(N'dbo.App05FollowUp',N'U') IS NULL
                    OR OBJECT_ID(N'dbo.App05FollowUpAssignment',N'U') IS NULL
                    SELECT CAST(0 AS bit);
                ELSE SELECT CAST(CASE WHEN EXISTS(SELECT 1 FROM dbo.LocalApp05Bootstrap WHERE Slot=1 AND Version=@Version)
                    AND NOT EXISTS (SELECT 1 FROM dbo.Trip t WHERE t.TenantId=@tenant AND t.BusinessDate IS NOT NULL
                        AND NOT EXISTS(SELECT 1 FROM dbo.LocalApp05TripBootstrap prepared WHERE prepared.TripId=t.Id))
                    THEN 1 ELSE 0 END AS bit);
                """, new { Version = CloseReadinessBootstrapVersion, tenant }, cancellationToken: ct)))
                throw new InvalidOperationException($"{CloseReadinessName} requires the prepared fixture in both tenant databases.");
            if (connections.UsesApp05TripClosure && !await db.ExecuteScalarAsync<bool>(new CommandDefinition("""
                SELECT CONVERT(bit,CASE WHEN OBJECT_ID(N'dbo.App05TripClosure',N'U') IS NOT NULL
                    AND OBJECT_ID(N'dbo.App05TripCloseRejection',N'U') IS NOT NULL
                    AND OBJECT_ID(N'dbo.App05ZeroStockHandoffLine',N'U') IS NOT NULL THEN 1 ELSE 0 END);
                """, cancellationToken: ct)))
                throw new InvalidOperationException($"{TripClosureName} requires its complete closure schema in both tenant databases.");
            if (connections.UsesApp05Successor && !await db.ExecuteScalarAsync<bool>(new CommandDefinition("""
                SELECT CONVERT(bit,CASE WHEN OBJECT_ID(N'dbo.App05ASuccessor',N'U') IS NOT NULL
                    AND OBJECT_ID(N'dbo.App05AHeldTransfer',N'U') IS NOT NULL
                    AND OBJECT_ID(N'dbo.App05AHeldTransferLot',N'U') IS NOT NULL
                    AND OBJECT_ID(N'dbo.App05ASuccessorCommand',N'U') IS NOT NULL
                    AND OBJECT_ID(N'dbo.App05ASuccessorReserve',N'U') IS NOT NULL
                    AND OBJECT_ID(N'dbo.App05AExecutionRight',N'V') IS NOT NULL
                    AND COL_LENGTH(N'dbo.SaleDelivery',N'App05BindingId') IS NOT NULL THEN 1 ELSE 0 END);
                """, cancellationToken: ct)))
                throw new InvalidOperationException($"{SuccessorName} requires the complete successor schema in both tenant databases.");
        }
    }

    private static async Task VerifySettlementSchema(SqlConnection db, CancellationToken ct)
    {
        if (!await db.ExecuteScalarAsync<bool>(new CommandDefinition("""
            SELECT CAST(CASE WHEN OBJECT_ID(N'dbo.D3CashierTrip',N'U') IS NOT NULL
                AND OBJECT_ID(N'dbo.D3TripUnloadLine',N'U') IS NOT NULL
                AND OBJECT_ID(N'dbo.App03LotUnload',N'U') IS NOT NULL
                AND OBJECT_ID(N'dbo.App03SettlementDay',N'U') IS NOT NULL
                AND OBJECT_ID(N'dbo.F02Advance',N'U') IS NOT NULL
                AND OBJECT_ID(N'dbo.F07Program',N'U') IS NOT NULL THEN 1 ELSE 0 END AS bit);
            """, cancellationToken: ct)))
            throw new InvalidOperationException($"{SettlementName} requires the complete handoff schema in both tenants.");
    }

    internal static IEnumerable<(string? Tag, long Version)> MigrationSteps(bool field, bool settlement, bool finance,
        bool closeReadiness = false, bool tripClosure = false, bool successor = false)
    {
        if (successor && !tripClosure)
            throw new InvalidOperationException($"{SuccessorName} requires the {TripClosureName} predecessor.");
        if (tripClosure && !closeReadiness)
            throw new InvalidOperationException($"{TripClosureName} requires the {CloseReadinessName} predecessor.");
        if (closeReadiness && !(field && settlement && finance))
            throw new InvalidOperationException($"{CloseReadinessName} requires its complete APP-03 finance predecessor.");
        foreach (var feature in Features) yield return feature;
        if (field) yield return (FieldName, App03FieldSchema.Version);
        if (settlement)
        {
            yield return (App02DevF02Schema.Profile, 202611230003);
            yield return (App02DevF07Schema.Profile, 202611230008);
            yield return (SettlementName, App03SettlementSchema.Version);
        }
        if (finance)
        {
            yield return (null, 202611230001);
            yield return (App02DevF06Schema.Profile, 202611230007);
            yield return (App02DevF08Schema.Profile, 202611230009);
            yield return (FinanceName, App03FinanceSchema.Version);
            yield return (FinanceName, App03FinanceReviewSchema.Version);
            yield return (FinanceName, App03FinanceCaseLinksSchema.Version);
            yield return (FinanceName, App03OwnerSchema.Version);
        }
        if (closeReadiness) yield return (CloseReadinessName, App05FollowUpSchema.Version);
        if (tripClosure) yield return (TripClosureName, App05TripClosureSchema.Version);
        if (successor)
        {
            yield return (SuccessorName, App05AFulfillmentSchema.Version);
            yield return (SuccessorName, App05ASuccessorReserveSchema.Version);
            yield return (SuccessorName, App05ASuccessorDeliverySchema.Version);
        }
    }

    internal static Task<SchemaState> ReadSchema(SqlConnection db, string nonce, CancellationToken ct = default) =>
        db.QuerySingleAsync<SchemaState>(new CommandDefinition("""
            IF OBJECT_ID(N'dbo.LocalDemoOwner', N'U') IS NULL OR OBJECT_ID(N'dbo.VersionInfo', N'U') IS NULL
                SELECT CAST(0 AS bit) AS Owned, CAST(0 AS bit) AS HasBase, '' AS FeatureVersions,
                       CAST(0 AS bit) AS HasFeatureTables, CAST(0 AS bit) AS HasD1D2;
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
                           THEN 1 ELSE 0 END AS bit) AS HasFeatureTables,
                       CAST(CASE WHEN (SELECT COUNT(*) FROM dbo.VersionInfo WHERE Version IN (202611220006,202611220007,202611220008))=3
                           AND OBJECT_ID(N'dbo.D2ShipToBinding',N'U') IS NOT NULL
                           AND OBJECT_ID(N'dbo.D2SaleCommandScope',N'U') IS NOT NULL
                           AND OBJECT_ID(N'dbo.BQuota',N'U') IS NOT NULL THEN 1 ELSE 0 END AS bit) AS HasD1D2;
            """, new { nonce, BaseVersion }, cancellationToken: ct));

    internal static void RequireSchema(SchemaState state, bool complete, bool field = false, bool settlement = false,
        bool finance = false, bool closeReadiness = false, bool tripClosure = false, bool successor = false)
    {
        var installed = state.FeatureVersions.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(long.Parse).ToHashSet();
        var versions = MigrationSteps(field, settlement, finance, closeReadiness, tripClosure, successor);
        if (!state.Owned || !state.HasBase || (field && !state.HasD1D2) || (complete && !state.HasFeatureTables) ||
            !installed.SetEquals((complete ? versions : versions.Take(installed.Count)).Select(f => f.Version)))
            throw new InvalidOperationException(complete
                ? $"{Name} requires its complete schema in both owned tenant databases."
                : $"{Name} migration requires an owned C.5 scratch baseline with a supported DEV-F migration history.");
    }

    internal sealed record SchemaState(bool Owned, bool HasBase, string FeatureVersions, bool HasFeatureTables, bool HasD1D2);
}
