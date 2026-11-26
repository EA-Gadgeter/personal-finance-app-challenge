using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Finance.Api.Auth;

internal sealed class AuthDbContext(DbContextOptions<AuthDbContext> options)
    : IdentityUserContext<IdentityUser>(options)
{
    public DbSet<AuthSession> Sessions => Set<AuthSession>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        base.OnModelCreating(model);

        model.Entity<IdentityUser>()
            .HasIndex(user => user.NormalizedEmail)
            .HasDatabaseName("EmailIndex")
            .IsUnique();

        model.Entity<AuthSession>()
            .HasOne<IdentityUser>()
            .WithMany()
            .HasForeignKey(session => session.UserId);
    }
}

internal sealed class AuthSession
{
    public Guid Id { get; init; }
    public required string UserId { get; init; }
    public DateTimeOffset IdleExpiresAt { get; set; }
}
