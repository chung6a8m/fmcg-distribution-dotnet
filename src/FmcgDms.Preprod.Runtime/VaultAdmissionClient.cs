using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace FmcgDms.Preprod.Runtime;

/// <summary>
/// B.2 Vault KV-v2 *read authorization witness*, not a key export or full IAM.
/// Separate externally issued workload tokens are mounted read-only; no env token,
/// in-repository secret, plaintext fallback, response logging or redirect following.
/// </summary>
public sealed class VaultAdmissionClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly Uri _endpoint;
    private readonly string _tokenFile;
    private readonly string _role;

    public VaultAdmissionClient(IConfiguration config, string role, HttpMessageHandler? handler = null)
    {
        _role = role;
        _tokenFile = config["Vault:TokenFile"]
            ?? throw new RuntimeDeniedException("VAULT_TOKEN_UNAVAILABLE");
        var address = new Uri(config["Vault:Address"]
            ?? throw new RuntimeDeniedException("VAULT_TLS_REQUIRED"), UriKind.Absolute);
        if (address.Scheme != Uri.UriSchemeHttps || address.UserInfo.Length != 0)
            throw new RuntimeDeniedException("VAULT_TLS_REQUIRED");
        _endpoint = new Uri(address, "/v1/" + config["Vault:ProbePath"]);
        _http = handler is null
            ? new HttpClient(new SocketsHttpHandler
              {
                  AllowAutoRedirect = false,
                  UseProxy = false,
                  ConnectTimeout = TimeSpan.FromSeconds(3)
              })
            : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromSeconds(5);
    }

    public async Task ValidateAsync(CancellationToken ct)
    {
        string token;
        try
        {
            token = (await File.ReadAllTextAsync(_tokenFile, ct)).Trim();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (IOException) { throw new RuntimeDeniedException("VAULT_TOKEN_UNAVAILABLE"); }
        catch (UnauthorizedAccessException) { throw new RuntimeDeniedException("VAULT_TOKEN_UNAVAILABLE"); }

        if (token.Length < 16 || token.Any(char.IsControl))
            throw new RuntimeDeniedException("VAULT_TOKEN_UNAVAILABLE");

        using var request = new HttpRequestMessage(HttpMethod.Get, _endpoint);
        request.Headers.TryAddWithoutValidation("X-Vault-Token", token);
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (HttpRequestException) { throw new RuntimeDeniedException("VAULT_UNAVAILABLE"); }
        catch (TaskCanceledException) { throw new RuntimeDeniedException("VAULT_UNAVAILABLE"); }

        using (response)
        {
            if (response.StatusCode != HttpStatusCode.OK)
                throw new RuntimeDeniedException("VAULT_ACCESS_DENIED");

            try
            {
                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
                var payload = document.RootElement.GetProperty("data");
                int version = payload.GetProperty("metadata").GetProperty("version").GetInt32();
                var marker = payload.GetProperty("data");
                if (version < 1
                    || marker.GetProperty("principal").GetString() != _role
                    || marker.GetProperty("admission").GetString() != "FOUNDATION_ONLY")
                    throw new RuntimeDeniedException("VAULT_MARKER_INVALID");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (RuntimeDeniedException) { throw; }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException
                    or KeyNotFoundException or FormatException)
            {
                throw new RuntimeDeniedException("VAULT_MARKER_INVALID");
            }
        }
    }

    public void Dispose() => _http.Dispose();
}
