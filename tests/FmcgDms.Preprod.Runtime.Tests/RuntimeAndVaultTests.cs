using System.Net;
using System.Text;
using FmcgDms.Preprod.Runtime;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace FmcgDms.Preprod.Runtime.Tests;

public sealed class RuntimeAndVaultTests
{
    [Fact]
    public void Runtime01_base_config_is_foundation_only()
    {
        PreprodStartupGuard.Validate(Settings(), "Preprod", "api");
        PreprodStartupGuard.Validate(Settings("worker"), "Preprod", "worker");
    }

    [Theory]
    [InlineData("SyntheticAuth:Enabled", "true", "SYNTHETIC_OR_DIRECT_DB_FORBIDDEN")]
    [InlineData("App02Worker:Enabled", "true", "SYNTHETIC_OR_DIRECT_DB_FORBIDDEN")]
    [InlineData("SyntheticAuth:SigningKey", "development-key", "SYNTHETIC_OR_DIRECT_DB_FORBIDDEN")]
    [InlineData("Jwt:SigningKey", "development-key", "SYNTHETIC_OR_DIRECT_DB_FORBIDDEN")]
    [InlineData("TenantDatabases:tenant-a", "server=peer", "SYNTHETIC_OR_DIRECT_DB_FORBIDDEN")]
    [InlineData("ConnectionStrings:Default", "Server=peer", "SYNTHETIC_OR_DIRECT_DB_FORBIDDEN")]
    [InlineData("Runtime:Mode", "Production", "MODE_NOT_AUTHORIZED")]
    [InlineData("Runtime:Admission", "GO", "MODE_NOT_AUTHORIZED")]
    [InlineData("Runtime:BusinessWritesEnabled", "true", "TENANT_ADMISSION_NOT_IMPLEMENTED")]
    [InlineData("Runtime:ControlPlaneEnabled", "true", "TENANT_ADMISSION_NOT_IMPLEMENTED")]
    [InlineData("Runtime:TenantRoutingEnabled", "true", "TENANT_ADMISSION_NOT_IMPLEMENTED")]
    [InlineData("Vault:Address", "http://vault.private:8200", "VAULT_TLS_REQUIRED")]
    [InlineData("Vault:Address", "https://localhost:8200", "VAULT_TLS_REQUIRED")]
    [InlineData("Vault:Address", "https://8.8.8.8:8200", "VAULT_TLS_REQUIRED")]
    [InlineData("Vault:Address", "https://[2001:4860:4860::8888]:8200", "VAULT_TLS_REQUIRED")]
    [InlineData("Vault:ProbePath", "preprod/data/runtime/worker", "VAULT_SCOPE_INVALID")]
    [InlineData("Runtime:TrustedProxyIp", "0.0.0.0", "PROXY_TRUST_INVALID")]
    [InlineData("Runtime:TrustedProxyIp", "8.8.8.8", "PROXY_TRUST_INVALID")]
    [InlineData("Runtime:PublicHost", "*", "PUBLIC_HOST_INVALID")]
    public void Runtime01_rejects_unsafe_config(string key, string value, string expected)
    {
        var cfg = Settings(changes: new() { [key] = value });
        Assert.Equal(expected, Assert.Throws<RuntimeDeniedException>(() =>
            PreprodStartupGuard.Validate(cfg, "Preprod", "api")).ReasonCode);
    }

    [Fact]
    public void Runtime01_rejects_host_environment_and_worker_role_mismatch()
    {
        Assert.Equal("ENVIRONMENT_MISMATCH",
            Assert.Throws<RuntimeDeniedException>(() =>
                PreprodStartupGuard.Validate(Settings(), "Development", "api")).ReasonCode);
        Assert.Equal("MODE_NOT_AUTHORIZED",
            Assert.Throws<RuntimeDeniedException>(() =>
                PreprodStartupGuard.Validate(Settings("worker"), "Preprod", "api")).ReasonCode);
    }

    [Theory]
    [InlineData("api", "Preprod", "Development")]
    [InlineData("api", "Development", "Preprod")]
    [InlineData("api", null, "Preprod")]
    [InlineData("worker", "Production", null)]
    [InlineData("worker", "Preprod", "Development")]
    public void Runtime01_rejects_ambiguous_host_environment_variables(
        string role, string? dotnet, string? aspnet)
    {
        var ex = Assert.Throws<RuntimeDeniedException>(() =>
            PreprodStartupGuard.ValidateHostVariables(role, dotnet, aspnet));
        Assert.Equal("ENVIRONMENT_MISMATCH", ex.ReasonCode);
    }

    [Fact]
    public void Runtime01_accepts_consistent_preprod_host_variables()
    {
        PreprodStartupGuard.ValidateHostVariables("api", "Preprod", "Preprod");
        PreprodStartupGuard.ValidateHostVariables("worker", "Preprod", null);
    }

    [Fact]
    public void Runtime02_forged_headers_do_not_turn_direct_access_into_proxy()
    {
        var proxy = IPAddress.Parse("172.30.50.10");
        Assert.False(PreprodIngress.FromTrustedProxy(IPAddress.Parse("172.30.50.11"), proxy));
        Assert.False(PreprodIngress.FromTrustedProxy(IPAddress.Loopback, proxy));
        Assert.True(PreprodIngress.FromTrustedProxy(proxy, proxy));
        Assert.False(PreprodIngress.HasSecureHost(false, "preprod.example.invalid", "preprod.example.invalid"));
        Assert.False(PreprodIngress.HasSecureHost(true, "localhost", "preprod.example.invalid"));
        Assert.True(PreprodIngress.HasSecureHost(true, "preprod.example.invalid", "preprod.example.invalid"));
    }

    [Fact]
    public async Task Runtime02_vault_policy_missing_token_wrong_principal_and_rotation_fail_closed()
    {
        var file = Path.GetTempFileName();
        try
        {
            // One token is deliberately denied by Vault; no secret value in exception.
            await File.WriteAllTextAsync(file, new string('x', 24), TestContext.Current.CancellationToken);
            var config = Settings(changes: new() { ["Vault:TokenFile"] = file });
            var client = new VaultAdmissionClient(config, "api", new FakeVaultHandler(req =>
            {
                Assert.Equal("/v1/preprod/data/runtime/api", req.RequestUri!.AbsolutePath);
                var token = req.Headers.GetValues("X-Vault-Token").Single();
                if (token == new string('x', 24))
                    return new HttpResponseMessage(HttpStatusCode.Forbidden);
                if (token == new string('y', 24))
                    return Ok("api");
                return Ok("worker");
            }));
            Assert.Equal("VAULT_ACCESS_DENIED",
                (await Assert.ThrowsAsync<RuntimeDeniedException>(() =>
                    client.ValidateAsync(TestContext.Current.CancellationToken))).ReasonCode);

            await File.WriteAllTextAsync(file, new string('y', 24), TestContext.Current.CancellationToken);
            await client.ValidateAsync(TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(file, new string('z', 24), TestContext.Current.CancellationToken);
            Assert.Equal("VAULT_MARKER_INVALID",
                (await Assert.ThrowsAsync<RuntimeDeniedException>(() =>
                    client.ValidateAsync(TestContext.Current.CancellationToken))).ReasonCode);
            client.Dispose();
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public async Task Runtime02_vault_outage_denied_and_no_plaintext_fallback()
    {
        var file = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(file, new string('k', 24), TestContext.Current.CancellationToken);
            var config = Settings(changes: new() { ["Vault:TokenFile"] = file });
            using var client = new VaultAdmissionClient(config, "api",
                new FakeVaultHandler(_ => throw new HttpRequestException("secret-containing-inner-message")));
            var ex = await Assert.ThrowsAsync<RuntimeDeniedException>(() =>
                client.ValidateAsync(TestContext.Current.CancellationToken));
            Assert.Equal("VAULT_UNAVAILABLE", ex.ReasonCode);
            Assert.DoesNotContain("secret-containing", ex.Message, StringComparison.Ordinal);
        }
        finally { File.Delete(file); }
    }

    private static IConfiguration Settings(string role = "api",
        Dictionary<string, string?>? changes = null)
    {
        var values = new Dictionary<string, string?>
        {
            ["Runtime:Environment"] = "Preprod",
            ["Runtime:Role"] = role,
            ["Runtime:Mode"] = "FOUNDATION_ONLY",
            ["Runtime:Admission"] = "NO_GO",
            ["Runtime:TrustedProxyIp"] = "172.30.50.10",
            ["Runtime:PublicHost"] = "preprod.example.invalid",
            ["Vault:Address"] = "https://vault.private.example.invalid:8200",
            ["Vault:ProbePath"] = $"preprod/data/runtime/{role}",
            ["Vault:TokenFile"] = $"/run/secrets/vault_{role}_token"
        };
        if (changes is not null)
            foreach (var pair in changes)
                values[pair.Key] = pair.Value;
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static HttpResponseMessage Ok(string role) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(
            "{\"data\":{\"metadata\":{\"version\":1},\"data\":{\"principal\":\""
            + role + "\",\"admission\":\"FOUNDATION_ONLY\"}}}",
            Encoding.UTF8, "application/json")
    };

    private sealed class FakeVaultHandler(Func<HttpRequestMessage, HttpResponseMessage> result)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken ct) => Task.FromResult(result(request));
    }
}
