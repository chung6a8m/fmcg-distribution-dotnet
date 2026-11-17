using System.Net;
using System.Text.Json;

namespace FmcgDms.Preprod.Tenancy;

public sealed record TenantOperatorSecrets(string RegistryConnection, string? MasterConnection);

/// <summary>
/// Private operator-only Vault KV-v2 reader. A separate scoped token file is
/// needed; API and Worker secrets/policies must never grant this path.
/// </summary>
public sealed class VaultTenantOperatorSecrets(string address,
    string tokenFile="/run/secrets/vault_tenant_provisioning_token",
    HttpMessageHandler? testHandler=null)
{
    public async Task<TenantOperatorSecrets> ReadOperatorAsync(CancellationToken ct)
    {
        using var doc=await ReadAsync("tenant-provisioning/operator",ct);
        var data=doc.RootElement.GetProperty("data").GetProperty("data");
        var registry=data.GetProperty("registryConnectionString").GetString() ?? "";
        var master=data.TryGetProperty("masterConnectionString",out var m)?m.GetString():null;
        if(string.IsNullOrWhiteSpace(registry))
            throw new TenantLifecycleDeniedException("TENANT_OPERATOR_SECRET_INVALID");
        return new TenantOperatorSecrets(registry,master);
    }

    public async Task<string> ReadProbeAsync(Guid tenant,CancellationToken ct)
    {
        if(tenant==Guid.Empty) throw new TenantLifecycleDeniedException("TENANT_PROBE_INVALID");
        using var doc=await ReadAsync("tenant-provisioning/probe/"+tenant.ToString("N"),ct);
        var connection=doc.RootElement.GetProperty("data").GetProperty("data")
            .GetProperty("connectionString").GetString();
        if(string.IsNullOrWhiteSpace(connection))
            throw new TenantLifecycleDeniedException("TENANT_PROBE_SECRET_INVALID");
        return connection;
    }

    private async Task<JsonDocument> ReadAsync(string path,CancellationToken ct)
    {
        if(!Uri.TryCreate(address,UriKind.Absolute,out var uri)
            || uri.Scheme!="https" || uri.UserInfo.Length!=0 || uri.Query.Length!=0
            || uri.Fragment.Length!=0 || uri.AbsolutePath!="/")
            throw new TenantLifecycleDeniedException("TENANT_VAULT_TLS_REQUIRED");
        string token;
        try {token=(await File.ReadAllTextAsync(tokenFile,ct)).Trim();}
        catch(OperationCanceledException) when(ct.IsCancellationRequested){throw;}
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException)
        {throw new TenantLifecycleDeniedException("TENANT_VAULT_TOKEN_UNAVAILABLE");}
        if(token.Length<16 || token.Any(char.IsControl))
            throw new TenantLifecycleDeniedException("TENANT_VAULT_TOKEN_INVALID");
        using var http=new HttpClient(testHandler ?? new SocketsHttpHandler
        {
            AllowAutoRedirect=false,UseProxy=false,ConnectTimeout=TimeSpan.FromSeconds(3)
        }) {Timeout=TimeSpan.FromSeconds(5)};
        using var request=new HttpRequestMessage(HttpMethod.Get,new Uri(uri,"/v1/preprod/data/"+path));
        request.Headers.TryAddWithoutValidation("X-Vault-Token",token);
        try
        {
            using var response=await http.SendAsync(request,ct);
            if(response.StatusCode!=HttpStatusCode.OK)
                throw new TenantLifecycleDeniedException("TENANT_VAULT_SCOPE_DENIED");
            await using var stream=await response.Content.ReadAsStreamAsync(ct);
            var doc=await JsonDocument.ParseAsync(stream,cancellationToken:ct);
            if(doc.RootElement.GetProperty("data").GetProperty("metadata")
                .GetProperty("version").GetInt32()<1)
            {
                doc.Dispose();
                throw new TenantLifecycleDeniedException("TENANT_VAULT_VERSION_INVALID");
            }
            return doc;
        }
        catch(OperationCanceledException) when(ct.IsCancellationRequested){throw;}
        catch(TenantLifecycleDeniedException){throw;}
        catch(Exception ex) when(ex is HttpRequestException or TaskCanceledException
            or JsonException or InvalidOperationException or KeyNotFoundException
            or FormatException or ArgumentException)
        {throw new TenantLifecycleDeniedException("TENANT_VAULT_UNAVAILABLE");}
    }
}
