using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ComplianceMonitor.Infrastructure.Persistence;

/// <summary>Lets `dotnet ef` build the context without starting the API (no token or host needed).</summary>
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<ComplianceDbContext>
{
    public ComplianceDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<ComplianceDbContext>().UseSqlite("Data Source=compliance.db").Options);
}
