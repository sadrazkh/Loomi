using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
namespace Loomi.Data;
public class DesignTimeFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<AppDbContext>().UseSqlite("Data Source=Storage/loomi.db").Options);
}
