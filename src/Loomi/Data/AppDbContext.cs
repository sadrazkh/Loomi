using Loomi.Models;
using Microsoft.EntityFrameworkCore;
namespace Loomi.Data;
public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<ImageProject> Projects => Set<ImageProject>();
    public DbSet<Generation> Generations => Set<Generation>();
    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<ImageProject>().Property(x => x.Title).HasMaxLength(160);
        b.Entity<Generation>().Property(x => x.Prompt).HasMaxLength(12000);
        b.Entity<Generation>().HasOne(x => x.Project).WithMany(x => x.Generations).HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<Generation>().HasOne<Generation>().WithMany().HasForeignKey(x => x.ParentGenerationId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<Generation>().HasIndex(x => new { x.Status, x.CreatedAt });
    }
}
