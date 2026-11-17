using System.Net;
using Microsoft.Extensions.Configuration;

namespace FmcgDms.Preprod.Runtime;

/// <summary>
/// B.2 can run only an inert PREPROD foundation. It does not authorize a tenant,
/// business API, production IAM, or a durable Worker lease.
/// </summary>
public static class PreprodStartupGuard
{
    public static void Validate(IConfiguration config, string environment, string role)
    {
        if (environment != "Preprod" || config["Runtime:Environment"] != "Preprod")
            Deny("ENVIRONMENT_MISMATCH");

        if (role is not ("api" or "worker")
            || config["Runtime:Role"] != role
            || config["Runtime:Mode"] != "FOUNDATION_ONLY"
            || config["Runtime:Admission"] != "NO_GO")
            Deny("MODE_NOT_AUTHORIZED");

        if (config.GetValue<bool>("SyntheticAuth:Enabled")
            || config.GetValue<bool>("App02Worker:Enabled")
            || !string.IsNullOrWhiteSpace(config["SyntheticAuth:SigningKey"])
            || !string.IsNullOrWhiteSpace(config["Jwt:SigningKey"])
            || !string.IsNullOrWhiteSpace(config["Jwt:Key"])
            || !string.IsNullOrWhiteSpace(config["Authentication:SigningKey"])
            || config.GetSection("TenantDatabases").GetChildren().Any()
            || !string.IsNullOrWhiteSpace(config["TenantDatabases"])
            || config.GetSection("ConnectionStrings").GetChildren().Any())
            Deny("SYNTHETIC_OR_DIRECT_DB_FORBIDDEN");

        if (config.GetValue<bool>("Runtime:BusinessWritesEnabled")
            || config.GetValue<bool>("Runtime:ControlPlaneEnabled")
            || config.GetValue<bool>("Runtime:TenantRoutingEnabled"))
            Deny("TENANT_ADMISSION_NOT_IMPLEMENTED");

        if (!Uri.TryCreate(config["Vault:Address"], UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length != 0
            || uri.Query.Length != 0 || uri.Fragment.Length != 0
            || uri.AbsolutePath != "/" || uri.Host == "localhost"
            || IPAddress.TryParse(uri.Host, out var hostIp) && IPAddress.IsLoopback(hostIp!))
            Deny("VAULT_TLS_REQUIRED");

        string expected = role == "api" ? "preprod/data/runtime/api" : "preprod/data/runtime/worker";
        if (config["Vault:ProbePath"] != expected
            || config["Vault:TokenFile"] != $"/run/secrets/vault_{role}_token")
            Deny("VAULT_SCOPE_INVALID");

        if (role == "api")
        {
            if (!IPAddress.TryParse(config["Runtime:TrustedProxyIp"], out var proxy)
                || proxy.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork
                || !IsPrivateIpv4(proxy))
                Deny("PROXY_TRUST_INVALID");

            string? host = config["Runtime:PublicHost"];
            if (string.IsNullOrWhiteSpace(host) || host == "*"
                || host.Contains('/') || host.Contains(':') || host.Contains(' ')
                || host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
                Deny("PUBLIC_HOST_INVALID");
        }
    }

    public static bool IsPrivateIpv4(IPAddress ip)
    {
        var octets = ip.GetAddressBytes();
        return octets.Length == 4 && (octets[0] == 10
            || octets[0] == 172 && octets[1] is >= 16 and <= 31
            || octets[0] == 192 && octets[1] == 168);
    }

    private static void Deny(string reason) => throw new RuntimeDeniedException(reason);
}

public sealed class RuntimeDeniedException(string reasonCode) : Exception(reasonCode)
{
    public string ReasonCode { get; } = reasonCode;
}
