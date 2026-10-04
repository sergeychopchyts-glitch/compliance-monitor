using ComplianceMonitor.Application.Abstractions;
using ComplianceMonitor.Infrastructure.Caching;
using ComplianceMonitor.Infrastructure.Integrations.HuggingFace;
using ComplianceMonitor.Infrastructure.Integrations.HuggingFace.Strategies;
using ComplianceMonitor.Infrastructure.Persistence;
using ComplianceMonitor.Infrastructure.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ComplianceMonitor.Infrastructure;

public static class DependencyInjection
{
    /// <summary>Registers the implementations of the Application abstractions.</summary>
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddDbContext<ComplianceDbContext>(options =>
            options.UseSqlite(configuration.GetConnectionString(ComplianceDbContext.ConnectionStringName)));
        services.AddScoped<AnalysisRepository>();
        services.AddHybridCache();
        services.AddScoped<IAnalysisRepository, CachedAnalysisRepository>();
        services.AddHealthChecks().AddCheck<DatabaseHealthCheck>(DatabaseHealthCheck.Name, tags: [DatabaseHealthCheck.ReadyTag]);

        services.AddHuggingFaceClient(configuration);
        services.AddSingleton<IPromptStrategy, ComplianceZeroShotPromptStrategy>();
        services.AddTransient<IComplianceModelGateway, HuggingFaceComplianceModelGateway>();

        return services;
    }
}
