using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace FmcgDms.Preprod.Identity;

public sealed class IdentityStartupDeniedException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

/// <summary>
/// Read only the dedicated API Identity KV-v2 secret over a private TLS Vault endpoint.
/// Never accepts credentials from request/headers/env/appsettings or follows redirects.
/// </summary>
public static class VaultIdentitySecrets
{
    public static async Task<IdentityOptions> ReadAsync(IConfiguration config,
        CancellationToken ct, HttpMessageHandler? testHandler = null)
    {
        if (config["Identity:Scope"] != "AUTH_ONLY"
            || config["Runtime:Mode"] != "FOUNDATION_ONLY"
            || config["Runtime:Admission"] != "NO_GO")
            throw new IdentityStartupDeniedException("IDENTITY_SCOPE_DENIED");
        if (!Uri.TryCreate(config["Vault:Address"], UriKind.Absolute, out var address)
            || address.Scheme != "https" || address.UserInfo.Length != 0
            || address.Query.Length != 0 || address.Fragment.Length != 0
            || address.AbsolutePath != "/")
            throw new IdentityStartupDeniedException("IDENTITY_VAULT_TLS_REQUIRED");
        if (config["Vault:TokenFile"] != "/run/secrets/vault_api_token")
            throw new IdentityStartupDeniedException("IDENTITY_VAULT_SCOPE_DENIED");

        string token;
        try { token = (await File.ReadAllTextAsync(config["Vault:TokenFile"]!, ct)).Trim(); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (IOException) { throw new IdentityStartupDeniedException("IDENTITY_VAULT_UNAVAILABLE"); }
        catch (UnauthorizedAccessException) { throw new IdentityStartupDeniedException("IDENTITY_VAULT_UNAVAILABLE"); }
        if (token.Length < 16 || token.Any(char.IsControl))
            throw new IdentityStartupDeniedException("IDENTITY_VAULT_UNAVAILABLE");

        using var http = new HttpClient(testHandler ?? new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(3)
        }) { Timeout = TimeSpan.FromSeconds(5) };
        using var request = new HttpRequestMessage(HttpMethod.Get,
            new Uri(address, "/v1/preprod/data/identity/api"));
        request.Headers.TryAddWithoutValidation("X-Vault-Token", token);
        try
        {
            using var response = await http.SendAsync(request, ct);
            if (response.StatusCode != HttpStatusCode.OK)
                throw new IdentityStartupDeniedException("IDENTITY_VAULT_ACCESS_DENIED");
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var root = doc.RootElement.GetProperty("data");
            if (root.GetProperty("metadata").GetProperty("version").GetInt32() < 1)
                throw new IdentityStartupDeniedException("IDENTITY_SECRET_INVALID");
            var values = root.GetProperty("data");
            var key = Convert.FromBase64String(values.GetProperty("signingKeyBase64").GetString() ?? "");
            var options = new IdentityOptions(
                values.GetProperty("connectionString").GetString() ?? "",
                key, values.GetProperty("keyId").GetString() ?? "",
                values.GetProperty("issuer").GetString() ?? "",
                values.GetProperty("audience").GetString() ?? "");
            options.Validate();
            return options;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (IdentityStartupDeniedException) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new IdentityStartupDeniedException("IDENTITY_VAULT_UNAVAILABLE");
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException
            or FormatException or ArgumentException)
        {
            throw new IdentityStartupDeniedException("IDENTITY_SECRET_INVALID");
        }
    }
}

public sealed record IdentityOptions(string SqlConnectionString, byte[] SigningKey,
    string KeyId, string Issuer, string Audience)
{
    public static readonly TimeSpan AccessLifetime = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan RefreshLifetime = TimeSpan.FromDays(7);

    public void Validate()
    {
        if (SigningKey.Length < 64
            || KeyId.Length is < 1 or > 80 || KeyId.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_')
            || !Uri.TryCreate(Issuer, UriKind.Absolute, out var issuer) || issuer.Scheme != "https"
            || issuer.UserInfo.Length != 0 || issuer.Query.Length != 0 || issuer.Fragment.Length != 0
            || string.IsNullOrWhiteSpace(Audience) || Audience.Length > 128 || Audience.Any(char.IsControl)
            || string.IsNullOrWhiteSpace(SqlConnectionString)
            || SqlConnectionString.Contains("AttachDbFilename", StringComparison.OrdinalIgnoreCase)
            || SqlConnectionString.Contains("LocalDB", StringComparison.OrdinalIgnoreCase))
            throw new IdentityStartupDeniedException("IDENTITY_SECRET_INVALID");
    }
}
