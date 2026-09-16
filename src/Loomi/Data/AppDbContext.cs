using Loomi.Models;
using Microsoft.EntityFrameworkCore;
namespace Loomi.Data;
public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<ImageProject> Projects => Set<ImageProject>();
    public DbSet<Generation> Generations => Set<Generation>();
    public DbSet<AppUser> Users => Set<AppUser>();
    // The table stays "Accounts" though the type is ProviderAccount, so nothing physical has to be renamed.
    public DbSet<ProviderAccount> Accounts => Set<ProviderAccount>();
    public DbSet<Upload> Uploads => Set<Upload>();
    public DbSet<GenerationInput> GenerationInputs => Set<GenerationInput>();
    public DbSet<CreditEntry> CreditEntries => Set<CreditEntry>();
    public DbSet<PricingRule> PricingRules => Set<PricingRule>();
    public DbSet<ApiToken> ApiTokens => Set<ApiToken>();
    public DbSet<TelegramLink> TelegramLinks => Set<TelegramLink>();
    public DbSet<LinkCode> LinkCodes => Set<LinkCode>();
    // No global query filter on UserId: the dispatcher runs without a user and would silently read an empty database.
    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<AppUser>().Property(x => x.Username).HasMaxLength(64);
        b.Entity<AppUser>().Property(x => x.NormalizedUsername).HasMaxLength(64);
        b.Entity<AppUser>().HasIndex(x => x.NormalizedUsername).IsUnique();
        b.Entity<ProviderAccount>().Property(x => x.Label).HasMaxLength(64);
        b.Entity<ProviderAccount>().Property(x => x.ProfileDirectory).HasMaxLength(64);
        // Filtered unique: a browser profile is claimed once, while any number of API accounts carry no profile at all.
        b.Entity<ProviderAccount>().HasIndex(x => x.ProfileDirectory).IsUnique().HasFilter("\"ProfileDirectory\" IS NOT NULL");
        b.Entity<ImageProject>().Property(x => x.Title).HasMaxLength(160);
        b.Entity<ImageProject>().HasIndex(x => x.UserId);
        b.Entity<Generation>().HasIndex(x => new { x.UserId, x.CreatedAt });
        b.Entity<Generation>().HasIndex(x => new { x.AccountId, x.CreatedAt });
        b.Entity<Generation>().Property(x => x.Prompt).HasMaxLength(12000);
        b.Entity<Generation>().HasOne(x => x.Project).WithMany(x => x.Generations).HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<Generation>().HasOne<Generation>().WithMany().HasForeignKey(x => x.ParentGenerationId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<Generation>().HasIndex(x => new { x.Status, x.CreatedAt });
        b.Entity<Generation>().HasMany(x => x.Inputs).WithOne().HasForeignKey(x => x.GenerationId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<Upload>().Property(x => x.Path).HasMaxLength(400);
        b.Entity<Upload>().Property(x => x.ContentType).HasMaxLength(64);
        b.Entity<Upload>().HasIndex(x => x.UserId);
        b.Entity<GenerationInput>().HasIndex(x => new { x.GenerationId, x.Order });
        b.Entity<CreditEntry>().Property(x => x.Note).HasMaxLength(400);
        b.Entity<CreditEntry>().HasIndex(x => new { x.UserId, x.CreatedAt });
        // One refund per generation: enforced in the index so a double-credit cannot be written even under a race.
        b.Entity<CreditEntry>().HasIndex(x => x.GenerationId).IsUnique().HasFilter("\"Kind\" = 2 AND \"GenerationId\" IS NOT NULL");
        b.Entity<PricingRule>().HasIndex(x => new { x.Provider, x.Operation }).IsUnique();
        b.Entity<ApiToken>().Property(x => x.Name).HasMaxLength(64);
        b.Entity<ApiToken>().Property(x => x.Prefix).HasMaxLength(16);
        b.Entity<ApiToken>().Property(x => x.Hash).HasMaxLength(128);
        b.Entity<ApiToken>().HasIndex(x => x.Prefix).IsUnique();
        b.Entity<ApiToken>().HasIndex(x => x.UserId);
        b.Entity<TelegramLink>().HasIndex(x => x.ChatId).IsUnique();
        b.Entity<TelegramLink>().HasIndex(x => x.UserId).IsUnique();
        b.Entity<LinkCode>().HasKey(x => x.Code);
        b.Entity<LinkCode>().Property(x => x.Code).HasMaxLength(16);
    }
}
