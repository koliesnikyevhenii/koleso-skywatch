using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Skywatch.Worker.Persistence;

/// <summary>For `dotnet ef migrations add` only: the tools never start the host or read configuration.</summary>
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<SkywatchDbContext>
{
    public SkywatchDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<SkywatchDbContext>().UseSqlite("Data Source=design-time.db").Options);
}
