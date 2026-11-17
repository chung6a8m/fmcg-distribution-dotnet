using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace FmcgDms.Preprod.Identity;

// Control-plane identity data only. No tenant DB bindings or business privileges.
public sealed class PreprodUser : IdentityUser<Guid>
{
    public bool Disabled { get; set; } = true; // provisioned accounts require explicit approval
    public string? ExternalIssuer { get; set; }
    public string? ExternalSubject { get; set; }
}

public sealed class LoginSession
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public PreprodUser User { get; set; } = null!;
    public long Epoch { get; set; }
    public string SecurityStamp { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
    public DateTime? RevokedUtc { get; set; }
}

public sealed class RefreshGrant
{
    public Guid Id { get; set; }
    public Guid SessionId { get; set; }
    public LoginSession Session { get; set; } = null!;
    // SHA256 of a 256-bit CSPRNG token, never the raw refresh secret.
    public string TokenHash { get; set; } = "";
    public DateTime ExpiresUtc { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime? ConsumedUtc { get; set; }
}

public sealed class PreprodIdentityDb(DbContextOptions<PreprodIdentityDb> options)
    : IdentityDbContext<PreprodUser, IdentityRole<Guid>, Guid>(options)
{
    public DbSet<LoginSession> LoginSessions => Set<LoginSession>();
    public DbSet<RefreshGrant> RefreshGrants => Set<RefreshGrant>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.Entity<PreprodUser>(e =>
        {
            e.Property(x => x.Disabled).IsRequired();
            e.Property(x => x.ExternalIssuer).HasMaxLength(200);
            e.Property(x => x.ExternalSubject).HasMaxLength(300);
            e.HasIndex(x => new { x.ExternalIssuer, x.ExternalSubject })
                .IsUnique().HasFilter("[ExternalIssuer] IS NOT NULL AND [ExternalSubject] IS NOT NULL");
        });
        builder.Entity<LoginSession>(e =>
        {
            e.ToTable("PreprodLoginSessions");
            e.HasKey(x => x.Id);
            e.Property(x => x.SecurityStamp).IsRequired().HasMaxLength(256);
            e.HasIndex(x => new { x.UserId, x.RevokedUtc });
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<RefreshGrant>(e =>
        {
            e.ToTable("PreprodRefreshGrants");
            e.HasKey(x => x.Id);
            e.Property(x => x.TokenHash).IsRequired().HasMaxLength(64).IsUnicode(false);
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasIndex(x => x.SessionId);
            e.HasOne(x => x.Session).WithMany().HasForeignKey(x => x.SessionId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }
}
