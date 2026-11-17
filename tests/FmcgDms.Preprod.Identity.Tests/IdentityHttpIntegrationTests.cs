using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using FmcgDms.Preprod.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Xunit;
using Keys = FmcgDms.Preprod.Identity.IdentityOptions;

namespace FmcgDms.Preprod.Identity.Tests;

// Actual ASP.NET Core routing, JwtBearer middleware and Identity endpoint integration.
// Disposable SQLite is a CI fixture only, NOT physical PREPROD SQL evidence.
public sealed class IdentityHttpIntegrationTests
{
    private const string Password = "ValidPass9!Longer";
    private const string Origin = "https://preprod.example.invalid";

    [Fact]
    public async Task Http_routes_enforce_origin_cookie_rotation_replay_and_denied_business_grants()
    {
        await using var f = await Fixture.CreateAsync();
        var ct = TestContext.Current.CancellationToken;

        using (var noOrigin = await f.Post("/auth/login", new { Username = "operator", Password }, null))
            Assert.Equal(HttpStatusCode.Forbidden, noOrigin.StatusCode);
        using (var peerOrigin = await f.Post("/auth/login", new { Username = "operator", Password }, "https://peer.invalid"))
            Assert.Equal(HttpStatusCode.Forbidden, peerOrigin.StatusCode);
        using (var anonymous = await f.Client.GetAsync("/auth/me", ct))
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        using (var prohibited = await f.Client.GetAsync("/api/synthetic/diagnostics", ct))
            Assert.Equal(HttpStatusCode.NotFound, prohibited.StatusCode);
        using (var admin = await f.Client.GetAsync("/api/admin/tenants", ct))
            Assert.Equal(HttpStatusCode.NotFound, admin.StatusCode);

        using var login = await f.Post("/auth/login", new { Username = "operator", Password }, Origin);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.Equal("no-store", login.Headers.CacheControl?.ToString());
        var firstCookie = Cookie(login);
        Assert.Contains("__Host-fmcg-preprod-refresh=", firstCookie);
        var firstToken = (await login.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken: ct))!.accessToken;
        Assert.False(string.IsNullOrWhiteSpace(firstToken));

        using (var me = await f.Me(firstToken))
        {
            Assert.Equal(HttpStatusCode.OK, me.StatusCode);
            var json = await me.Content.ReadAsStringAsync(ct);
            Assert.Contains("\"tenantAccess\":\"DENIED\"", json);
            Assert.DoesNotContain("roles", json, StringComparison.OrdinalIgnoreCase);
        }
        using (var wrongOrigin = await f.Post("/auth/refresh", null, "https://peer.invalid", firstCookie))
            Assert.Equal(HttpStatusCode.Forbidden, wrongOrigin.StatusCode);

        using var refreshed = await f.Post("/auth/refresh", null, Origin, firstCookie);
        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
        var secondCookie = Cookie(refreshed);
        Assert.NotEqual(firstCookie, secondCookie);
        var secondToken = (await refreshed.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken: ct))!.accessToken;
        using (var beforeReplay = await f.Me(secondToken))
            Assert.Equal(HttpStatusCode.OK, beforeReplay.StatusCode);

        using (var replay = await f.Post("/auth/refresh", null, Origin, firstCookie))
            Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
        using (var afterReplay = await f.Me(secondToken))
            Assert.Equal(HttpStatusCode.Unauthorized, afterReplay.StatusCode);
        using (var replaySuccessor = await f.Post("/auth/refresh", null, Origin, secondCookie))
            Assert.Equal(HttpStatusCode.Unauthorized, replaySuccessor.StatusCode);
        using (var noBearer = await f.Post("/auth/logout", null, Origin))
            Assert.Equal(HttpStatusCode.Unauthorized, noBearer.StatusCode);
    }

    [Fact]
    public async Task Http_JwtBearer_rejects_expired_unknown_kid_wrong_issuer_audience_key_and_session()
    {
        await using var f = await Fixture.CreateAsync();
        using var login = await f.Post("/auth/login", new { Username = "operator", Password }, Origin);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var token = (await login.Content.ReadFromJsonAsync<TokenResponse>(
            cancellationToken: TestContext.Current.CancellationToken))!.accessToken;
        using (var valid = await f.Me(token)) Assert.Equal(HttpStatusCode.OK, valid.StatusCode);

        var variants = new[]
        {
            NewToken(f.Keys, token, expired: true),
            NewToken(f.Keys, token, kid: "retired-key"),
            NewToken(f.Keys, token, issuer: "https://evil.invalid"),
            NewToken(f.Keys, token, audience: "peer-tenant"),
            NewToken(f.Keys, token, wrongSignature: true),
            NewToken(f.Keys, token, wrongSession: true),
            NewToken(f.Keys, token, missingJti: true),
            NewToken(f.Keys, token, wrongEpoch: true)
        };
        foreach (var invalid in variants)
        {
            using var denied = await f.Me(invalid);
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        }
    }

    private static string NewToken(Keys keys, string current,
        bool expired = false, string? kid = null, string? issuer = null, string? audience = null,
        bool wrongSignature = false, bool wrongSession = false, bool missingJti = false,
        bool wrongEpoch = false)
    {
        var parsed = new JwtSecurityTokenHandler().ReadJwtToken(current);
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            JwtRegisteredClaimNames.Sub, JwtRegisteredClaimNames.Sid,
            JwtRegisteredClaimNames.Jti, "session_epoch"
        };
        var claims = parsed.Claims.Where(c => allowed.Contains(c.Type))
            .Select(c => new Claim(c.Type,
                c.Type == JwtRegisteredClaimNames.Sid && wrongSession ? Guid.NewGuid().ToString("D")
                : c.Type == "session_epoch" && wrongEpoch ? "999" : c.Value))
            .Where(c => !missingJti || c.Type != JwtRegisteredClaimNames.Jti)
            .ToList();
        var key = new SymmetricSecurityKey(
            wrongSignature ? RandomNumberGenerator.GetBytes(64) : keys.SigningKey)
        { KeyId = kid ?? keys.KeyId };
        var token = new JwtSecurityToken(
            issuer: issuer ?? keys.Issuer, audience: audience ?? keys.Audience,
            claims: claims, notBefore: DateTime.UtcNow.AddMinutes(-20),
            expires: DateTime.UtcNow.AddMinutes(expired ? -10 : 5),
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static string Cookie(HttpResponseMessage response)
    {
        var header = Assert.Single(response.Headers.GetValues("Set-Cookie"));
        Assert.Contains("; secure", header, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("; httponly", header, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("; samesite=strict", header, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("domain=", header, StringComparison.OrdinalIgnoreCase);
        return header.Split(';')[0];
    }

    private sealed record TokenResponse(string accessToken);

    private sealed class Fixture : IAsyncDisposable
    {
        public required WebApplication Application { get; init; }
        public required HttpClient Client { get; init; }
        public required SqliteConnection Connection { get; init; }
        public required Keys Keys { get; init; }

        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            var keys = new Keys(
                "Server=control.private;Database=ci_identity;Encrypt=True;TrustServerCertificate=False",
                RandomNumberGenerator.GetBytes(64), "ci-key-v1",
                Origin, "fmcg-preprod-identity");
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Services.AddPreprodIdentity(keys, opt => opt.UseSqlite(connection));
            builder.Services.AddRateLimiter(limiter =>
                limiter.AddFixedWindowLimiter("preprod-auth", policy =>
                {
                    policy.PermitLimit = 1000;
                    policy.Window = TimeSpan.FromMinutes(1);
                    policy.QueueLimit = 0;
                }));
            var app = builder.Build();
            app.UseRouting();
            app.UseRateLimiter();
            app.UseAuthentication();
            app.UseAuthorization();
            app.MapIdentityOnly();
            await app.StartAsync(TestContext.Current.CancellationToken);
            using (var scope = app.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<PreprodIdentityDb>();
                await db.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
                var users = scope.ServiceProvider.GetRequiredService<UserManager<PreprodUser>>();
                var user = new PreprodUser
                {
                    Id = Guid.NewGuid(), UserName = "operator", Disabled = false,
                    LockoutEnabled = true
                };
                Assert.True((await users.CreateAsync(user, Password)).Succeeded);
            }
            var client = app.GetTestClient();
            client.BaseAddress = new Uri(Origin);
            return new Fixture { Application = app, Client = client, Connection = connection, Keys = keys };
        }

        public async Task<HttpResponseMessage> Post(
            string path, object? body, string? origin, string? cookie = null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, path);
            if (body is not null) request.Content = JsonContent.Create(body);
            if (origin is not null) request.Headers.TryAddWithoutValidation("Origin", origin);
            if (cookie is not null) request.Headers.TryAddWithoutValidation("Cookie", cookie);
            return await Client.SendAsync(request, TestContext.Current.CancellationToken);
        }

        public async Task<HttpResponseMessage> Me(string jwt)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/auth/me");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
            return await Client.SendAsync(request, TestContext.Current.CancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Application.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }
}
