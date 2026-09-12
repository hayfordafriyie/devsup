using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace DevSup.Infrastructure.Persistence;

public sealed class DevSupDbContextFactory : IDesignTimeDbContextFactory<DevSupDbContext>
{
    public DevSupDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<DevSupDbContext>()
            .UseSqlite("Data Source=devsup.db")
            .Options;

        return new DevSupDbContext(options);
    }
}