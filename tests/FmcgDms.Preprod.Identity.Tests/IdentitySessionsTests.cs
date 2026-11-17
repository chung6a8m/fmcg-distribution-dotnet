using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using FmcgDms.Preprod.Identity;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Xunit;
using Keys = FmcgDms.Preprod.Identity.IdentityOptions;

namespace FmcgDms.Preprod.Identity.Tests;

public sealed class IdentitySessionsTests
{
    private const string Password = "ValidPass9!Longer";

    [Fact]
    public async Task Identity_password_login_denies_disabled_and_wrong_secret()
    {
        await using var env = await Fixture.CreateAsync();
        var user = env.User;
        user.Disabled = true;
        Assert.True((await env.Users.UpdateAsync(user)).Succeeded);
        Assert.Null(await env.Sessions.LoginAsync("operator", Password, TestContext.Current.CancellationToken));
        user.Disabled = false;
        Assert.True((await env.Users.UpdateAsync(user)).Succeeded);
        Assert.Null(await env.Sessions.LoginAsync("operator", "invalid-password", TestContext.Current.CancellationToken));
        var issued = await env.Sessions.LoginAsync("operator", Password, TestContext.Current.CancellationToken);
        Assert.NotNull(issued);
        Assert.NotEqual(issued.AccessToken, issued.RefreshToken);
        Assert.True(issued.RefreshToken.Length >= 40);
        Assert.False(await env.Db.RefreshGrants.AnyAsync(g => g.TokenHash == issued.RefreshToken, TestContext.Current.CancellationToken));
        Assert.True(await env.Db.RefreshGrants.AnyAsync(g =>
            g.TokenHash == IdentitySessionService.HashRefresh(issued.RefreshToken), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Mfa_enabled_user_cannot_use_password_only_login_or_existing_session()
    {
        await using var env = await Fixture.CreateAsync();
        var initial = (await env.Sessions.LoginAsync("operator", Password, TestContext.Current.CancellationToken))!;
        Assert.True(await env.Sessions.ValidateAccessAsync(Principal(initial.AccessToken), TestContext.Current.CancellationToken));
        var originalStamp = await env.Users.GetSecurityStampAsync(env.User);

        // Simulate an administrator requiring MFA while preserving the existing
        // security stamp: the B.3 password-only flow MUST NOT silently bypass it.
        env.User.TwoFactorEnabled = true;
        Assert.True((await env.Users.UpdateAsync(env.User)).Succeeded);
        Assert.Equal(originalStamp, await env.Users.GetSecurityStampAsync(env.User));

        Assert.Null(await env.Sessions.LoginAsync("operator", Password, TestContext.Current.CancellationToken));
        Assert.False(await env.Sessions.ValidateAccessAsync(Principal(initial.AccessToken), TestContext.Current.CancellationToken));
        Assert.Null(await env.Sessions.RotateAsync(initial.RefreshToken, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Refresh_reuse_revokes_family_and_all_issued_access()
    {
        await using var env = await Fixture.CreateAsync();
        var first = (await env.Sessions.LoginAsync("operator", Password, TestContext.Current.CancellationToken))!;
        var second = (await env.Sessions.RotateAsync(first.RefreshToken, TestContext.Current.CancellationToken))!;
        Assert.NotNull(second);
        Assert.NotEqual(first.RefreshToken, second.RefreshToken);
        Assert.True(await env.Sessions.ValidateAccessAsync(Principal(second.AccessToken), TestContext.Current.CancellationToken));

        // Replaying the consumed grant is not simply a 401. It poisons the
        // entire family, including its last unconsumed grant and bearer access.
        Assert.Null(await env.Sessions.RotateAsync(first.RefreshToken, TestContext.Current.CancellationToken));
        Assert.False(await env.Sessions.ValidateAccessAsync(Principal(second.AccessToken), TestContext.Current.CancellationToken));
        Assert.Null(await env.Sessions.RotateAsync(second.RefreshToken, TestContext.Current.CancellationToken));
        Assert.All(await env.Db.LoginSessions.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken),
            s => Assert.NotNull(s.RevokedUtc));
    }

    [Fact]
    public async Task Logout_revoke_all_security_stamp_and_actor_binding()
    {
        await using var env = await Fixture.CreateAsync();
        var a = (await env.Sessions.LoginAsync("operator", Password, TestContext.Current.CancellationToken))!;
        var b = (await env.Sessions.LoginAsync("operator", Password, TestContext.Current.CancellationToken))!;
        Assert.True(await env.Sessions.ValidateAccessAsync(Principal(a.AccessToken), TestContext.Current.CancellationToken));
        Assert.True(await env.Sessions.ValidateAccessAsync(Principal(b.AccessToken), TestContext.Current.CancellationToken));

        var sa = Jwt(a.AccessToken).Claims.Single(x => x.Type == JwtRegisteredClaimNames.Sid).Value;
        await env.Sessions.RevokeAsync(env.User.Id, Guid.Parse(sa), TestContext.Current.CancellationToken);
        Assert.False(await env.Sessions.ValidateAccessAsync(Principal(a.AccessToken), TestContext.Current.CancellationToken));
        Assert.True(await env.Sessions.ValidateAccessAsync(Principal(b.AccessToken), TestContext.Current.CancellationToken));

        var invalid = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(JwtRegisteredClaimNames.Sub, Guid.NewGuid().ToString()),
            new Claim(JwtRegisteredClaimNames.Sid, Jwt(b.AccessToken).Claims
                .Single(x => x.Type == JwtRegisteredClaimNames.Sid).Value),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new Claim("session_epoch", "0")
        ], "Bearer"));
        Assert.False(await env.Sessions.ValidateAccessAsync(invalid, TestContext.Current.CancellationToken));

        Assert.True((await env.Users.UpdateSecurityStampAsync(env.User)).Succeeded);
        Assert.False(await env.Sessions.ValidateAccessAsync(Principal(b.AccessToken), TestContext.Current.CancellationToken));

        var c = (await env.Sessions.LoginAsync("operator", Password, TestContext.Current.CancellationToken))!;
        await env.Sessions.RevokeAllAsync(env.User.Id, TestContext.Current.CancellationToken);
        Assert.False(await env.Sessions.ValidateAccessAsync(Principal(c.AccessToken), TestContext.Current.CancellationToken));
        Assert.Null(await env.Sessions.RotateAsync(c.RefreshToken, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Jwt_has_explicit_issuer_audience_signature_kid_and_no_tenant_permission()
    {
        await using var env = await Fixture.CreateAsync();
        var auth = (await env.Sessions.LoginAsync("operator", Password, TestContext.Current.CancellationToken))!;
        var jwt = Jwt(auth.AccessToken);
        Assert.Equal("test-v1", jwt.Header.Kid);
        Assert.Equal("HS256", jwt.Header.Alg);
        Assert.DoesNotContain(jwt.Claims, c => c.Type.Contains("tenant", StringComparison.OrdinalIgnoreCase)
            || c.Type.Contains("role", StringComparison.OrdinalIgnoreCase)
            || c.Type.Contains("permission", StringComparison.OrdinalIgnoreCase));
        var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
        var validation = new TokenValidationParameters
        {
            ValidateIssuer = true, ValidIssuer = env.Keys.Issuer,
            ValidateAudience = true, ValidAudience = env.Keys.Audience,
            ValidateLifetime = true, RequireExpirationTime = true,
            RequireSignedTokens = true, ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(env.Keys.SigningKey)
                { KeyId = env.Keys.KeyId },
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256], ClockSkew = TimeSpan.Zero
        };
        handler.ValidateToken(auth.AccessToken, validation, out _);
        Assert.ThrowsAny<SecurityTokenException>(() =>
            handler.ValidateToken(auth.AccessToken, new TokenValidationParameters
            {
                ValidateIssuer = true, ValidIssuer = "https://attacker.invalid",
                ValidateAudience = true, ValidAudience = env.Keys.Audience,
                RequireSignedTokens = true, ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(env.Keys.SigningKey),
                ValidateLifetime = true
            }, out _));
        Assert.ThrowsAny<SecurityTokenException>(() =>
            handler.ValidateToken(auth.AccessToken, new TokenValidationParameters
            {
                ValidateIssuer = true, ValidIssuer = env.Keys.Issuer,
                ValidateAudience = true, ValidAudience = "peer-tenant",
                RequireSignedTokens = true, ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(env.Keys.SigningKey),
                ValidateLifetime = true
            }, out _));
        Assert.ThrowsAny<SecurityTokenException>(() =>
            handler.ValidateToken(auth.AccessToken, new TokenValidationParameters
            {
                ValidateIssuer = true, ValidIssuer = env.Keys.Issuer,
                ValidateAudience = true, ValidAudience = env.Keys.Audience,
                RequireSignedTokens = true, ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(RandomNumberGenerator.GetBytes(64)),
                ValidateLifetime = true
            }, out _));
    }

    [Fact]
    public void Rotated_or_revoked_Vault_key_and_sql_binding_fail_closed()
    {
        var current = new Keys(
            "Server=control.private;Database=fmcg_identity;Encrypt=True;TrustServerCertificate=False",
            RandomNumberGenerator.GetBytes(64), "key-a",
            "https://preprod.example.invalid", "fmcg-preprod-identity");
        current.Validate();
        current.EnsureCurrent(current);
        Assert.Throws<IdentityStartupDeniedException>(() =>
            current.EnsureCurrent(current with { KeyId = "key-b" }));
        Assert.Throws<IdentityStartupDeniedException>(() =>
            current.EnsureCurrent(current with { SigningKey = RandomNumberGenerator.GetBytes(64) }));
        Assert.Throws<IdentityStartupDeniedException>(() =>
            current.EnsureCurrent(current with { SqlConnectionString =
                "Server=peer.private;Database=fmcg_identity;Encrypt=True;TrustServerCertificate=False" }));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("https://preprod.example.invalid", true)]
    [InlineData("http://preprod.example.invalid", false)]
    [InlineData("https://evil.example.invalid", false)]
    [InlineData("https://preprod.example.invalid.evil.invalid", false)]
    [InlineData("https://preprod.example.invalid:8443", false)]
    public void Cookie_state_changes_require_exact_https_origin(string? origin, bool expected)
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("preprod.example.invalid");
        if (origin is not null) context.Request.Headers["Origin"] = origin;
        Assert.Equal(expected, IdentityHttp.ValidOrigin(context));
    }

    [Fact]
    public void Refresh_cookie_is_host_only_secure_httponly_strict_and_revocable()
    {
        var ctx = new DefaultHttpContext();
        IdentityHttp.SetCookie(ctx, "nonsecret-unit-test-value");
        var header = ctx.Response.Headers.SetCookie.ToString().ToLowerInvariant();
        Assert.StartsWith("__host-fmcg-preprod-refresh=", header);
        Assert.Contains("; path=/", header);
        Assert.Contains("; secure", header);
        Assert.Contains("; httponly", header);
        Assert.Contains("; samesite=strict", header);
        Assert.Contains("max-age=", header);
        Assert.DoesNotContain("domain=", header);

        ctx.Response.Headers.Remove("Set-Cookie");
        IdentityHttp.ClearCookie(ctx);
        var cleared = ctx.Response.Headers.SetCookie.ToString().ToLowerInvariant();
        Assert.StartsWith("__host-fmcg-preprod-refresh=", cleared);
        Assert.Contains("; path=/", cleared);
        Assert.Contains("; secure", cleared);
        Assert.Contains("; httponly", cleared);
        Assert.Contains("; samesite=strict", cleared);
        Assert.Contains("expires=", cleared); // ASP.NET Core expires the deleted cookie.
        Assert.DoesNotContain("domain=", cleared);
    }

    [Fact]
    public void Missing_or_weak_Vault_material_refuses_activation()
    {
        Assert.Throws<IdentityStartupDeniedException>(() =>
            new Keys("LocalDB", new byte[64], "v1", "https://issuer.invalid", "preprod").Validate());
        Assert.Throws<IdentityStartupDeniedException>(() =>
            new Keys("Server=private", new byte[32], "v1", "https://issuer.invalid", "preprod").Validate());
        Assert.Throws<IdentityStartupDeniedException>(() =>
            new Keys("Server=private", new byte[64], "v1", "http://issuer.invalid", "preprod").Validate());
        Assert.Throws<IdentityStartupDeniedException>(() =>
            new Keys("Server=private;Database=identity;Encrypt=False", new byte[64],
                "v1", "https://issuer.invalid", "preprod").Validate());
        Assert.Throws<IdentityStartupDeniedException>(() =>
            new Keys("Server=private;Database=identity;Encrypt=True;TrustServerCertificate=True",
                new byte[64], "v1", "https://issuer.invalid", "preprod").Validate());
    }

    private static JwtSecurityToken Jwt(string token) => new JwtSecurityTokenHandler().ReadJwtToken(token);

    private static ClaimsPrincipal Principal(string token) =>
        new(new ClaimsIdentity(Jwt(token).Claims, "Bearer"));

    private sealed class Fixture : IAsyncDisposable
    {
        public required SqliteConnection Connection { get; init; }
        public required ServiceProvider Provider { get; init; }
        public required AsyncServiceScope Scope { get; init; }
        public required PreprodIdentityDb Db { get; init; }
        public required UserManager<PreprodUser> Users { get; init; }
        public required IdentitySessionService Sessions { get; init; }
        public required PreprodUser User { get; init; }
        public required Keys Keys { get; init; }

        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            var keys = new Keys(
                "Server=control.private;Database=fmcg_identity;Encrypt=True",
                RandomNumberGenerator.GetBytes(64), "test-v1",
                "https://preprod.example.invalid", "fmcg-preprod-identity");
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton(keys);
            services.AddSingleton(TimeProvider.System);
            services.AddDbContext<PreprodIdentityDb>(o => o.UseSqlite(connection));
            services.AddIdentityCore<PreprodUser>()
                .AddRoles<IdentityRole<Guid>>()
                .AddEntityFrameworkStores<PreprodIdentityDb>();
            services.AddScoped<IdentitySessionService>();
            var root = services.BuildServiceProvider();
            var scope = root.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<PreprodIdentityDb>();
            await db.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
            var users = scope.ServiceProvider.GetRequiredService<UserManager<PreprodUser>>();
            var user = new PreprodUser
            {
                Id = Guid.NewGuid(), UserName = "operator", Disabled = false,
                LockoutEnabled = true
            };
            Assert.True((await users.CreateAsync(user, Password)).Succeeded);
            return new Fixture
            {
                Connection = connection, Provider = root, Scope = scope, Db = db,
                Users = users, User = user,
                Sessions = scope.ServiceProvider.GetRequiredService<IdentitySessionService>(),
                Keys = keys
            };
        }

        public async ValueTask DisposeAsync()
        {
            await Scope.DisposeAsync();
            await Provider.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }
}
