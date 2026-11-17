using System.IdentityModel.Tokens.Jwt;
using Microsoft.IdentityModel.JsonWebTokens;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

namespace FmcgDms.Preprod.Identity;

public static class IdentityHttp
{
    private const string CookieName = "__Host-fmcg-preprod-refresh";

    public static void AddPreprodIdentity(this IServiceCollection services, IdentityOptions identity)
    {
        identity.Validate();
        services.AddSingleton(identity);
        services.AddSingleton(TimeProvider.System);
        services.AddDbContext<PreprodIdentityDb>(o => o.UseSqlServer(identity.SqlConnectionString));
        services.AddIdentityCore<PreprodUser>(o =>
        {
            o.User.RequireUniqueEmail = false;
            o.Password.RequiredLength = 12;
            o.Password.RequireDigit = true;
            o.Password.RequireUppercase = true;
            o.Password.RequireLowercase = true;
            o.Password.RequireNonAlphanumeric = true;
            o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
            o.Lockout.MaxFailedAccessAttempts = 5;
            o.Lockout.AllowedForNewUsers = true;
        })
        .AddRoles<IdentityRole<Guid>>()
        .AddEntityFrameworkStores<PreprodIdentityDb>();
        services.AddScoped<IdentitySessionService>();
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(jwt =>
        {
            jwt.MapInboundClaims = false;
            jwt.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = identity.Issuer,
                ValidateAudience = true,
                ValidAudience = identity.Audience,
                ValidateLifetime = true,
                RequireExpirationTime = true,
                RequireSignedTokens = true,
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(identity.SigningKey)
                    { KeyId = identity.KeyId },
                ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
                ClockSkew = TimeSpan.Zero,
                NameClaimType = JwtRegisteredClaimNames.Sub
            };
            jwt.Events = new JwtBearerEvents
            {
                OnTokenValidated = async ctx =>
                {
                    try
                    {
                        // Do not let TokenValidationParameters try-all-keys
                        // accept an unknown or stale advertised kid.
                        var presentedKid = ctx.SecurityToken switch
                        {
                            JwtSecurityToken jwtToken => jwtToken.Header.Kid,
                            JsonWebToken jsonToken => jsonToken.Kid,
                            _ => null
                        };
                        if (!string.Equals(presentedKid, identity.KeyId, StringComparison.Ordinal))
                        {
                            ctx.Fail("Unrecognized key version");
                            return;
                        }
                        if (ctx.Principal is null || !await ctx.HttpContext.RequestServices
                            .GetRequiredService<IdentitySessionService>()
                            .ValidateAccessAsync(ctx.Principal, ctx.HttpContext.RequestAborted))
                            ctx.Fail("Session authority denied");
                    }
                    catch (Exception) { ctx.Fail("Session authority unavailable"); }
                }
            };
        });
        services.AddAuthorization();
    }

    public static void MapIdentityOnly(this IEndpointRouteBuilder endpoints)
    {
        // B.3 does not expose registration, tenant selection, roles, approval,
        // business actions, or any privileged Control Plane API.
        var auth = endpoints.MapGroup("/auth");
        auth.MapPost("/login", async (HttpContext ctx, LoginInput request,
            IdentitySessionService sessions, CancellationToken ct) =>
        {
            if (!ValidOrigin(ctx)) return Results.StatusCode(403);
            if (request.Username is null || request.Password is null)
                return Results.Unauthorized();
            var result = await sessions.LoginAsync(request.Username, request.Password, ct);
            if (result is null) return Results.Unauthorized();
            SetCookie(ctx, result.RefreshToken);
            return AccessResponse(ctx, result);
        }).AllowAnonymous();

        auth.MapPost("/refresh", async (HttpContext ctx, IdentitySessionService sessions,
            CancellationToken ct) =>
        {
            if (!ValidOrigin(ctx)) return Results.StatusCode(403);
            var raw = ctx.Request.Cookies[CookieName];
            var result = await sessions.RotateAsync(raw, ct);
            if (result is null)
            {
                ClearCookie(ctx);
                return Results.Unauthorized();
            }
            SetCookie(ctx, result.RefreshToken);
            return AccessResponse(ctx, result);
        }).AllowAnonymous();

        auth.MapPost("/logout", async (HttpContext ctx, IdentitySessionService sessions,
            CancellationToken ct) =>
        {
            if (!ValidOrigin(ctx)) return Results.StatusCode(403);
            var actor = Guid.Parse(ctx.User.FindFirstValue(JwtRegisteredClaimNames.Sub)!);
            var sid = Guid.Parse(ctx.User.FindFirstValue(JwtRegisteredClaimNames.Sid)!);
            await sessions.RevokeAsync(actor, sid, ct);
            ClearCookie(ctx);
            ctx.Response.Headers["Cache-Control"] = "no-store";
            return Results.NoContent();
        }).RequireAuthorization();

        auth.MapPost("/revoke-all", async (HttpContext ctx, IdentitySessionService sessions,
            CancellationToken ct) =>
        {
            if (!ValidOrigin(ctx)) return Results.StatusCode(403);
            var actor = Guid.Parse(ctx.User.FindFirstValue(JwtRegisteredClaimNames.Sub)!);
            await sessions.RevokeAllAsync(actor, ct);
            ClearCookie(ctx);
            ctx.Response.Headers.CacheControl = "no-store";
            return Results.NoContent();
        }).RequireAuthorization();

        auth.MapGet("/me", (HttpContext ctx) =>
        {
            ctx.Response.Headers.CacheControl = "no-store";
            return Results.Ok(new
            {
                actorId = ctx.User.FindFirstValue(JwtRegisteredClaimNames.Sub),
                sessionId = ctx.User.FindFirstValue(JwtRegisteredClaimNames.Sid),
                // No tenant authorization until B.4/B.5 authoritative binding.
                tenantAccess = "DENIED"
            });
        }).RequireAuthorization();
    }

    private static IResult AccessResponse(HttpContext context, IssuedCredentials credentials)
    {
        context.Response.Headers["Cache-Control"] = "no-store";
        context.Response.Headers["Pragma"] = "no-cache";
        return Results.Ok(new
        {
            accessToken = credentials.AccessToken,
            tokenType = "Bearer",
            expiresAtUtc = credentials.AccessExpiresUtc,
            tenantAccess = "DENIED"
        });
    }

    public static bool ValidOrigin(HttpContext context)
    {
        // Origin is mandatory for all cookie-dependent and state-changing routes.
        // Browsers on another origin cannot use the refresh cookie with SameSite=Strict.
        var origins = context.Request.Headers["Origin"];
        return origins.Count == 1
            && Uri.TryCreate(origins.ToString(), UriKind.Absolute, out var uri)
            && uri.Scheme == "https" && uri.IsDefaultPort
            && uri.UserInfo.Length == 0 && uri.AbsolutePath == "/"
            && uri.Query.Length == 0 && uri.Fragment.Length == 0
            && string.Equals(uri.Host, context.Request.Host.Host, StringComparison.OrdinalIgnoreCase)
            && context.Request.IsHttps;
    }

    private static void SetCookie(HttpContext context, string token) =>
        context.Response.Cookies.Append(CookieName, token, new CookieOptions
        {
            HttpOnly = true, Secure = true, SameSite = SameSiteMode.Strict,
            IsEssential = true, Path = "/",
            MaxAge = IdentityOptions.RefreshLifetime
            // Never set Domain: __Host- cookie must be host-only.
        });

    private static void ClearCookie(HttpContext context) =>
        context.Response.Cookies.Delete(CookieName, new CookieOptions
        {
            Secure = true, HttpOnly = true, SameSite = SameSiteMode.Strict,
            Path = "/"
        });

    public sealed record LoginInput(string? Username, string? Password);
}
