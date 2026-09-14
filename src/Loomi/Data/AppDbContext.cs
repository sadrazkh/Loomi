using Loomi.Models;
using Microsoft.EntityFrameworkCore;
namespace Loomi.Data;
public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<ImageProject> Projects => Set<ImageProject>();
    public DbSet<Generation> Generations => Set<Generation>();
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<BrowserAccount> Accounts => Set<BrowserAccount>();
    // No global query filter on UserId: the dispatcher runs without a user and would silently read an empty database.
    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<AppUser>().Property(x => x.Username).HasMaxLength(64);
        b.Entity<AppUser>().Property(x => x.NormalizedUsername).HasMaxLength(64);
        b.Entity<AppUser>().HasIndex(x => x.NormalizedUsername).IsUnique();
        b.Entity<BrowserAccount>().Property(x => x.Label).HasMaxLength(64);
        b.Entity<BrowserAccount>().Property(x => x.ProfileDirectory).HasMaxLength(64);
        b.Entity<BrowserAccount>().HasIndex(x => x.ProfileDirectory).IsUnique();
        b.Entity<ImageProject>().Property(x => x.Title).HasMaxLength(160);
        b.Entity<ImageProject>().HasIndex(x => x.UserId);
        b.Entity<Generation>().HasIndex(x => new { x.UserId, x.CreatedAt });
        b.Entity<Generation>().HasIndex(x => new { x.AccountId, x.CreatedAt });
        b.Entity<Generation>().Property(x => x.Prompt).HasMaxLength(12000);
        b.Entity<Generation>().HasOne(x => x.Project).WithMany(x => x.Generations).HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<Generation>().HasOne<Generation>().WithMany().HasForeignKey(x => x.ParentGenerationId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<Generation>().HasIndex(x => new { x.Status, x.CreatedAt });
    }
}
