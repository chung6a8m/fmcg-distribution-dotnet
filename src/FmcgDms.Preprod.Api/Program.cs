using System.Net;
using FmcgDms.Preprod.Runtime;
using Microsoft.AspNetCore.HttpOverrides;

var builder = WebApplication.CreateBuilder(args);
// Intentionally a NEW binary: there is no SyntheticAuth, LocalDB, business
// handler, tenant connection resolver, posting route or synthetic Worker here.
PreprodStartupGuard.Validate(builder.Configuration, builder.Environment.EnvironmentName, "api");

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

// B.2 is intentionally NON-BUSINESS, NO_GO even when probes pass.
app.MapGet("/health/live", () => Results.Ok(new { state = "live", admission = "NO_GO" }));
app.MapGet("/health/ready", async (VaultAdmissionClient vault, CancellationToken ct) =>
{
    try
    {
        await vault.ValidateAsync(ct);
        return Results.Ok(new { state = "foundation_ready", admission = "NO_GO" });
    }
    catch (RuntimeDeniedException)
    {
        app.Logger.LogWarning("RUNTIME_REQUEST_DENIED code=VAULT_READINESS");
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }
});
app.MapFallback(() => Results.NotFound());
await app.RunAsync();
public partial class Program { }
