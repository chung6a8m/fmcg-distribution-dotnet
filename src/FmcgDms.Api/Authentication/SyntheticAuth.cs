using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

namespace FmcgDms.Api.Authentication;

/// <summary>Synthetic-only local test identity; never production identity provider.</summary>
public static class SyntheticAuth
{
    public const string TenantClaim = "tenant_id";
    public static void Configure(WebApplicationBuilder builder)
    {
        var cfg = builder.Configuration;
        bool enabled = cfg.GetValue<bool>("SyntheticAuth:Enabled");
        if (enabled && !builder.Environment.IsDevelopment() && !builder.Environment.IsEnvironment("IntegrationTest"))
            throw new InvalidOperationException("Synthetic auth forbidden outside dev/integration test.");
        string? key = cfg["SyntheticAuth:SigningKey"];
        if (enabled && (string.IsNullOrWhiteSpace(key) || Encoding.UTF8.GetByteCount(key) < 32))
            throw new InvalidOperationException("Synthetic signing key must be a secret of 32+ bytes.");

        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.MapInboundClaims = false;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = cfg["SyntheticAuth:Issuer"] ?? "fmcg-synthetic-app00",
                    ValidateAudience = true,
                    ValidAudience = cfg["SyntheticAuth:Audience"] ?? "fmcg-synthetic-app00",
                    ValidateLifetime = true,
                    RequireExpirationTime = true,
                    RequireSignedTokens = true,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(
                        Encoding.UTF8.GetBytes(enabled ? key! : new string('X', 64))),
                    ClockSkew = TimeSpan.FromSeconds(5)
                };
            });
        builder.Services.AddAuthorization();
    }

    public static string GetTenantId(ClaimsPrincipal principal) =>
        principal.FindFirst(TenantClaim)?.Value is { Length: > 0 } id
            ? id
            : throw new UnauthorizedAccessException("Missing validated tenant claim.");
}
