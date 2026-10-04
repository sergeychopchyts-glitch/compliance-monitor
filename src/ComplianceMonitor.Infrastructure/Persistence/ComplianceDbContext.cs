using ComplianceMonitor.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace ComplianceMonitor.Infrastructure.Persistence;

public sealed class ComplianceDbContext(DbContextOptions<ComplianceDbContext> options) : DbContext(options)
{
    public const string ConnectionStringName = "ComplianceMonitor";

    internal DbSet<AnalysisEntity> Analyses => Set<AnalysisEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ComplianceDbContext).Assembly);
}
