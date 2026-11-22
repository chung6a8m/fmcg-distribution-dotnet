using System.Data;
using System.Globalization;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Data.SqlClient;

namespace FmcgDms.Api.Persistence;

internal static class App02c6Clock
{
    private const string Key = "App02C6Utc";
    internal const string ConfigKey = "App02C6:ClockFile";
    internal const string SqlUtcNow = "COALESCE(CONVERT(datetime2(7),SESSION_CONTEXT(N'" + Key + "')),SYSUTCDATETIME())";

    internal static string? GetConfiguredPath(IConfiguration config, IHostEnvironment host)
    {
        if (config[ConfigKey] is not string path)
            return null;
        string nonce = config["App02C1:Nonce"] ?? "";
        if (!host.IsEnvironment("IntegrationTest") || !config.GetValue<bool>("SyntheticAuth:Enabled")
            || !config.GetValue<bool>("App02C2:DedicatedRuntime")
            || config.GetValue<bool>("App02Worker:Enabled")
            || Enumerable.Range(1, 5).Any(increment => !config.GetValue<bool>($"App02C{increment}:Enabled"))
            || !Regex.IsMatch(nonce, @"\A[a-f0-9]{32}\z")
            || !Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal)
            || !string.Equals(Path.GetFullPath(path), path, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetFileName(path), "clock.utc", StringComparison.Ordinal)
            || !string.Equals(Path.GetFileName(Path.GetDirectoryName(path)), nonce, StringComparison.Ordinal))
            throw new InvalidOperationException("C.6 clock requires a dedicated synthetic IntegrationTest session and its nonce-owned clock file.");
        Read(path);
        return path;
    }

    internal static Task<int> SetSessionAsync(SqlConnection db, string path, CancellationToken ct)
    {
        var args = new DynamicParameters();
        args.Add("Key", Key);
        args.Add("Utc", Read(path), DbType.DateTime2, scale: 7);
        return db.ExecuteAsync(new CommandDefinition(
            "EXEC sys.sp_set_session_context @key=@Key,@value=@Utc,@read_only=1", args, cancellationToken: ct));
    }

    private static DateTime Read(string path)
    {
        string value = File.ReadAllText(path).Trim();
        if (!DateTime.TryParseExact(value, "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var instant)
            || instant.Kind != DateTimeKind.Utc)
            throw new InvalidOperationException("C.6 clock file requires a UTC round-trip timestamp.");
        return instant;
    }
}
