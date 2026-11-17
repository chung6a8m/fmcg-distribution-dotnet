using System.Data;
using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

namespace FmcgDms.Preprod.Identity;

public sealed record IssuedCredentials(string AccessToken, string RefreshToken,
    DateTimeOffset AccessExpiresUtc);

/// <summary>
/// Durable session-family rotation. Every refresh is a SQL transaction; a consumed
/// grant is evidence of reuse and revokes its entire family before returning denial.
/// No claims from a token imply tenant membership or a business permission.
/// </summary>
public sealed class IdentitySessionService(
    PreprodIdentityDb db, UserManager<PreprodUser> users, IdentityOptions keys,
    TimeProvider clock, ILogger<IdentitySessionService> logger)
{
    public static string HashRefresh(string raw) =>
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(raw)));

    private static string GenerateRefresh() =>
        Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));

    public async Task<IssuedCredentials?> LoginAsync(string username, string password,
        CancellationToken ct)
    {
        if (username.Length is < 1 or > 256 || password.Length is < 1 or > 4096)
            return null;
        var user = await users.FindByNameAsync(username);
        if (user is null || user.Disabled || user.TwoFactorEnabled || await users.IsLockedOutAsync(user))
            return null;
        if (!await users.CheckPasswordAsync(user, password))
        {
            if (!(await users.AccessFailedAsync(user)).Succeeded)
                return null;
            return null;
        }
        if (!(await users.ResetAccessFailedCountAsync(user)).Succeeded)
            return null;
        var stamp = await users.GetSecurityStampAsync(user);
        if (string.IsNullOrWhiteSpace(stamp)) return null;
        var now = clock.GetUtcNow().UtcDateTime;
        var session = new LoginSession
        {
            Id = Guid.NewGuid(), UserId = user.Id, Epoch = 0,
            SecurityStamp = stamp, CreatedUtc = now
        };
        var raw = GenerateRefresh();
        db.LoginSessions.Add(session);
        db.RefreshGrants.Add(new RefreshGrant
        {
            Id = Guid.NewGuid(), Session = session, TokenHash = HashRefresh(raw),
            CreatedUtc = now, ExpiresUtc = now.Add(IdentityOptions.RefreshLifetime)
        });
        await db.SaveChangesAsync(ct);
        return Issue(user.Id, session.Id, session.Epoch, raw, now);
    }

    public async Task<IssuedCredentials?> RotateAsync(string? raw, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(raw) || raw.Length > 256
            || raw.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_'))
            return null;
        var digest = HashRefresh(raw);
        var now = clock.GetUtcNow().UtcDateTime;
        // Claim by conditional write FIRST, to serialize two concurrent
        // refresh requests rather than both taking a shared read lock.
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        int claimed = await db.RefreshGrants
            .Where(x => x.TokenHash == digest && x.ConsumedUtc == null && x.ExpiresUtc > now)
            .ExecuteUpdateAsync(up => up.SetProperty(x => x.ConsumedUtc, now), ct);
        var prior = await db.RefreshGrants.AsNoTracking()
            .SingleOrDefaultAsync(x => x.TokenHash == digest, ct);
        if (prior is null) return null;
        var session = await db.LoginSessions.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == prior.SessionId, ct);
        if (session is null || session.RevokedUtc is not null) return null;
        if (claimed != 1)
        {
            // A consumed grant (including a concurrent refresh loser) is
            // an attempted replay: poison the complete family before denial.
            if (prior.ConsumedUtc is not null)
            {
                await RevokeSessionRowAsync(session.Id, now, ct);
                await tx.CommitAsync(ct);
                // B.8 will supply the immutable audit sink. Never emit the raw grant or digest.
                logger.LogWarning("IDENTITY_REFRESH_REUSE_REVOKED reason=CONSUMED_GRANT");
            }
            return null;
        }
        var user = await users.FindByIdAsync(session.UserId.ToString());
        if (user is null || user.Disabled || user.TwoFactorEnabled || await users.IsLockedOutAsync(user)
            || !string.Equals(await users.GetSecurityStampAsync(user), session.SecurityStamp,
                StringComparison.Ordinal))
        {
            await RevokeSessionRowAsync(session.Id, now, ct);
            await tx.CommitAsync(ct);
            return null;
        }
        var next = GenerateRefresh();
        db.RefreshGrants.Add(new RefreshGrant
        {
            Id = Guid.NewGuid(), SessionId = session.Id, TokenHash = HashRefresh(next),
            CreatedUtc = now, ExpiresUtc = now.Add(IdentityOptions.RefreshLifetime)
        });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Issue(user.Id, session.Id, session.Epoch, next, now);
    }

    public async Task<bool> ValidateAccessAsync(ClaimsPrincipal principal,
        CancellationToken ct)
    {
        if (!Guid.TryParse(principal.FindFirstValue(JwtRegisteredClaimNames.Sub), out var actor)
            || actor == Guid.Empty
            || !Guid.TryParse(principal.FindFirstValue(JwtRegisteredClaimNames.Sid), out var sessionId)
            || sessionId == Guid.Empty
            || !Guid.TryParse(principal.FindFirstValue(JwtRegisteredClaimNames.Jti), out _)
            || !long.TryParse(principal.FindFirstValue("session_epoch"),
                NumberStyles.None, CultureInfo.InvariantCulture, out var epoch) || epoch < 0)
            return false;
        var state = await db.LoginSessions.AsNoTracking()
            .Where(x => x.Id == sessionId)
            .Select(x => new
            {
                x.UserId, x.RevokedUtc, x.Epoch, SessionStamp = x.SecurityStamp,
                x.User.Disabled, x.User.TwoFactorEnabled, UserStamp = x.User.SecurityStamp,
                x.User.LockoutEnd, x.User.LockoutEnabled
            })
            .SingleOrDefaultAsync(ct);
        return state is not null && state.RevokedUtc is null && state.UserId == actor
            && state.Epoch == epoch && !state.Disabled && !state.TwoFactorEnabled
            && string.Equals(state.SessionStamp, state.UserStamp, StringComparison.Ordinal)
            && (!state.LockoutEnabled || state.LockoutEnd is null || state.LockoutEnd <= clock.GetUtcNow());
    }

    public async Task RevokeAsync(Guid actor, Guid sessionId, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        await db.LoginSessions.Where(s => s.Id == sessionId && s.UserId == actor && s.RevokedUtc == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedUtc, now)
                .SetProperty(x => x.Epoch, x => x.Epoch + 1), ct);
    }

    public async Task RevokeAllAsync(Guid actor, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        await db.LoginSessions.Where(s => s.UserId == actor && s.RevokedUtc == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedUtc, now)
                .SetProperty(x => x.Epoch, x => x.Epoch + 1), ct);
    }

    private Task<int> RevokeSessionRowAsync(Guid sessionId, DateTime now,
        CancellationToken ct) =>
        db.LoginSessions.Where(x => x.Id == sessionId && x.RevokedUtc == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedUtc, now)
                .SetProperty(x => x.Epoch, x => x.Epoch + 1), ct);

    private IssuedCredentials Issue(Guid actor, Guid sid, long epoch, string refresh,
        DateTime now)
    {
        var expiry = new DateTimeOffset(now, TimeSpan.Zero).Add(IdentityOptions.AccessLifetime);
        var signingKey = new SymmetricSecurityKey(keys.SigningKey) { KeyId = keys.KeyId };
        var token = new JwtSecurityToken(
            issuer: keys.Issuer, audience: keys.Audience,
            claims:
            [
                new Claim(JwtRegisteredClaimNames.Sub, actor.ToString("D")),
                new Claim(JwtRegisteredClaimNames.Sid, sid.ToString("D")),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("D")),
                new Claim("session_epoch", epoch.ToString(CultureInfo.InvariantCulture))
            ],
            notBefore: now, expires: expiry.UtcDateTime,
            signingCredentials: new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256));
        return new IssuedCredentials(new JwtSecurityTokenHandler().WriteToken(token), refresh, expiry);
    }
}
