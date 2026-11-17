using System.Net;
using Microsoft.Data.SqlClient;

namespace FmcgDms.Preprod.Tenancy;

/// <summary>
/// Operator executable never accepts SQL secrets in flags/env/config, never
/// targets LocalDB or a system registry, and fails closed on TLS downgrade.
/// Tests use the core library against disposable LocalDB, not this admission.
/// </summary>
public static class TenantOperatorAdmission
{

    /// <summary>
    /// Called BEFORE Vault token-file access. The endpoint must match a
    /// read-only independently approved operator trust anchor; an environment
    /// variable cannot redirect the Vault token to an attacker-owned host.
    /// </summary>
    public static void ValidateVaultAnchor(string? environment, string? vaultAddress,
        string approvedEndpointFile="/run/secrets/b4_approved_vault_endpoint")
    {
        if(environment!="Preprod" || !Uri.TryCreate(vaultAddress,UriKind.Absolute,out var uri)
            || uri.Scheme!="https" || uri.UserInfo.Length>0 || uri.Query.Length>0
            || uri.Fragment.Length>0 || uri.AbsolutePath!="/"
            || uri.Host.Equals("localhost",StringComparison.OrdinalIgnoreCase)
            || IPAddress.TryParse(uri.Host,out var address) && !IsPrivateIpv4(address))
            throw new TenantLifecycleDeniedException("TENANT_OPERATOR_VAULT_ENDPOINT_DENIED");
        string expected;
        try {expected=File.ReadAllText(approvedEndpointFile).Trim();}
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException)
        {throw new TenantLifecycleDeniedException("TENANT_OPERATOR_VAULT_PIN_MISSING");}
        if(!string.Equals(uri.AbsoluteUri,expected,StringComparison.Ordinal))
            throw new TenantLifecycleDeniedException("TENANT_OPERATOR_VAULT_PIN_MISMATCH");
    }

    public static void Validate(string? environment, string? vaultAddress,
        string registryConnection, string? masterConnection=null)
    {
        if(environment!="Preprod" || !Uri.TryCreate(vaultAddress,UriKind.Absolute,out var uri)
            || uri.Scheme!="https" || uri.UserInfo.Length>0 || uri.Query.Length>0
            || uri.Fragment.Length>0 || uri.AbsolutePath!="/"
            || uri.Host.Equals("localhost",StringComparison.OrdinalIgnoreCase)
            || IPAddress.TryParse(uri.Host,out var address)
                && !IsPrivateIpv4(address))
            throw new TenantLifecycleDeniedException("TENANT_OPERATOR_PREPROD_TLS_REQUIRED");
        var registry=Parse(registryConnection);
        if(IsSystemDb(registry.InitialCatalog))
            throw new TenantLifecycleDeniedException("TENANT_OPERATOR_REGISTRY_DB_DENIED");
        if(masterConnection is not null)
        {
            var master=Parse(masterConnection);
            if(!master.InitialCatalog.Equals("master",StringComparison.OrdinalIgnoreCase)
                || !string.Equals(master.DataSource,registry.DataSource,StringComparison.OrdinalIgnoreCase)
                || !registry.IntegratedSecurity && !master.IntegratedSecurity
                    && string.Equals(master.UserID,registry.UserID,StringComparison.OrdinalIgnoreCase))
                throw new TenantLifecycleDeniedException("TENANT_OPERATOR_ADMIN_SCOPE_DENIED");
        }
    }

    private static SqlConnectionStringBuilder Parse(string connection)
    {
        SqlConnectionStringBuilder cs;
        try {cs=new SqlConnectionStringBuilder(connection);}
        catch(ArgumentException) {throw new TenantLifecycleDeniedException("TENANT_OPERATOR_SQL_CONFIG_INVALID");}
        if(string.IsNullOrWhiteSpace(cs.DataSource) || string.IsNullOrWhiteSpace(cs.InitialCatalog)
            || cs.DataSource.Contains("localdb",StringComparison.OrdinalIgnoreCase)
            || cs.DataSource.Contains("localhost",StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(cs.AttachDBFilename) || cs.TrustServerCertificate
            || cs.Encrypt==SqlConnectionEncryptOption.Optional)
            throw new TenantLifecycleDeniedException("TENANT_OPERATOR_SQL_TLS_REQUIRED");
        return cs;
    }

    private static bool IsSystemDb(string db) =>
        string.IsNullOrWhiteSpace(db) || new[]{"master","model","msdb","tempdb"}
            .Contains(db,StringComparer.OrdinalIgnoreCase);

    private static bool IsPrivateIpv4(IPAddress ip)
    {
        var b=ip.GetAddressBytes();
        return b.Length==4 && (b[0]==10 || b[0]==172 && b[1]>=16 && b[1]<=31
            || b[0]==192 && b[1]==168);
    }
}
