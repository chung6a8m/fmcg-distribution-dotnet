using System.Net;
using FmcgDms.Preprod.Runtime;
using FmcgDms.Preprod.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.RateLimiting;

var builder = WebApplication.CreateBuilder(args);
// Intentionally a NEW binary: there is no SyntheticAuth, LocalDB, business
// handler, tenant connection resolver, posting route or synthetic Worker here.
try
{
    PreprodStartupGuard.ValidateHostVariables("api",
        Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT"),
        Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"));
    PreprodStartupGuard.Validate(builder.Configuration, builder.Environment.EnvironmentName, "api");
}
catch (RuntimeDeniedException denied)
{
    Console.Error.WriteLine("RUNTIME_START_DENIED code=" + denied.ReasonCode);
    throw;
}

// Identity is independent of the synthetic API and is opt-in only after a
// scoped Vault secret and separately provisioned control-plane DB exist.
bool identityEnabled = builder.Configuration.GetValue<bool>("Identity:Enabled");
IdentityOptions? activeIdentity = null;
if (identityEnabled)
{
    try
    {
        activeIdentity = await VaultIdentitySecrets.ReadAsync(builder.Configuration,
            CancellationToken.None);
        builder.Services.AddPreprodIdentity(activeIdentity);
        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddFixedWindowLimiter("preprod-auth", limit =>
            {
                limit.PermitLimit = 20; // global PREPROD auth budget, not a production/WAF substitute
                limit.Window = TimeSpan.FromMinutes(1);
                limit.QueueLimit = 0;
                limit.AutoReplenishment = true;
            });
        });
    }
    catch (IdentityStartupDeniedException denied)
    {
        Console.Error.WriteLine("IDENTITY_START_DENIED code=" + denied.Code);
        throw;
    }
}
var trustedProxy = IPAddress.Parse(builder.Configuration["Runtime:TrustedProxyIp"]!);
var publicHost = builder.Configuration["Runtime:PublicHost"]!;
builder.Services.AddSingleton(new VaultAdmissionClient(builder.Configuration, "api"));
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.ForwardLimit = 1;
    options.KnownProxies.Add(trustedProxy);
});

var app = builder.Build();
try
{
    // Fail startup BEFORE listening if identity, Vault, TLS or policy is absent.
    await app.Services.GetRequiredService<VaultAdmissionClient>().ValidateAsync(CancellationToken.None);
}
catch (RuntimeDeniedException denied)
{
    app.Logger.LogCritical("RUNTIME_START_DENIED code={ReasonCode}", denied.ReasonCode);
    throw;
}

if (identityEnabled)
{
    // Never auto-create/upgrade a privileged SQL database at API boot. B.4
    // controls operator-approved provisioning and migrations; unknown schema
    // or unavailable authority is a hard startup denial.
    try
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PreprodIdentityDb>();
        if (!await db.Database.CanConnectAsync())
            throw new IdentityStartupDeniedException("IDENTITY_SCHEMA_UNAVAILABLE");
        var count = await db.Database.SqlQueryRaw<int>(
            "SELECT COUNT(*) AS [Value] FROM sys.tables WHERE name IN ('AspNetUsers','PreprodLoginSessions','PreprodRefreshGrants')")
            .SingleAsync();
        if (count != 3)
            throw new IdentityStartupDeniedException("IDENTITY_SCHEMA_UNAVAILABLE");
    }
    catch (Exception)
    {
        app.Logger.LogCritical("IDENTITY_START_DENIED code=IDENTITY_SCHEMA_UNAVAILABLE");
        throw new IdentityStartupDeniedException("IDENTITY_SCHEMA_UNAVAILABLE");
    }
}
// Check socket origin BEFORE X-Forwarded-* rewrite; a direct connection or
// a forged header from another container is not a trusted reverse proxy.
app.Use(async (ctx, next) =>
{
    if (!PreprodIngress.FromTrustedProxy(ctx.Connection.RemoteIpAddress, trustedProxy))
    {
        app.Logger.LogWarning("RUNTIME_REQUEST_DENIED code=PROXY_BYPASS");
        ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }
    await next(ctx);
});
app.UseForwardedHeaders();
app.Use(async (ctx, next) =>
{
    if (!PreprodIngress.HasSecureHost(ctx.Request.IsHttps, ctx.Request.Host.Host, publicHost))
    {
        app.Logger.LogWarning("RUNTIME_REQUEST_DENIED code=TLS_OR_HOST");
        ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }
    await next(ctx);
});

if (identityEnabled)
{
    app.UseRouting();
    app.UseRateLimiter();
    app.Use(async (ctx, next) =>
    {
        if (ctx.Request.Path.StartsWithSegments("/auth", StringComparison.OrdinalIgnoreCase))
        {
            const long maxBodySize = 8 * 1024;
            if (ctx.Request.ContentLength > maxBodySize)
            {
                ctx.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                return;
            }
            var maxBody = ctx.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (maxBody is { IsReadOnly: false })
                maxBody.MaxRequestBodySize = maxBodySize;
            try
            {
                // Vault token is re-read from its read-only file on every request.
                // Revoked/rotated Identity secret => deny further issuance until
                // operator-approved reload, never fall back to cached key.
                await app.Services.GetRequiredService<VaultAdmissionClient>()
                    .ValidateAsync(ctx.RequestAborted);
                var liveIdentity = await VaultIdentitySecrets.ReadAsync(
                    builder.Configuration, ctx.RequestAborted);
                activeIdentity!.EnsureCurrent(liveIdentity);
            }
            catch (Exception ex) when (ex is RuntimeDeniedException or IdentityStartupDeniedException)
            {
                app.Logger.LogWarning("IDENTITY_REQUEST_DENIED code=VAULT_AUTHORITY_UNAVAILABLE");
                ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                return;
            }
        }
        await next(ctx);
    });
    app.UseAuthentication();
    app.UseAuthorization();
    app.MapIdentityOnly();
}
// B.2 remains NON-BUSINESS, NO_GO even when probes pass.
app.MapGet("/health/live", () => Results.Ok(new { state = "live", admission = "NO_GO" }));
app.MapGet("/health/ready", async (VaultAdmissionClient vault, CancellationToken ct) =>
{
    try
    {
        await vault.ValidateAsync(ct);
        if (identityEnabled)
        {
            var live = await VaultIdentitySecrets.ReadAsync(builder.Configuration, ct);
            activeIdentity!.EnsureCurrent(live);
        }
        return Results.Ok(new { state = "foundation_ready", admission = "NO_GO" });
    }
    catch (Exception ex) when (ex is RuntimeDeniedException or IdentityStartupDeniedException)
    {
        app.Logger.LogWarning("RUNTIME_REQUEST_DENIED code=VAULT_READINESS");
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }
});
app.MapFallback(() => Results.NotFound());
await app.RunAsync();
public partial class Program { }
