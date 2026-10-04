using ComplianceMonitor.Application.Compliance;
using ComplianceMonitor.Infrastructure;

namespace ComplianceMonitor.Tests;

/// <summary>The dependency rule: Api -> Application <- Infrastructure. Application knows nothing about HTTP, EF Core or the model provider.</summary>
public sealed class ArchitectureTests
{
    [Theory]
    [InlineData("ComplianceMonitor.Api")]
    [InlineData("ComplianceMonitor.Infrastructure")]
    [InlineData("Microsoft.AspNetCore")]
    [InlineData("Microsoft.EntityFrameworkCore")]
    [InlineData("Microsoft.Extensions.Http")]
    [InlineData("Polly")]
    public void Application_DoesNotReference(string forbidden)
    {
        var references = typeof(ComplianceAnalysisService).Assembly.GetReferencedAssemblies().Select(a => a.Name!);

        Assert.DoesNotContain(references, name => name.StartsWith(forbidden, StringComparison.Ordinal));
    }

    [Fact]
    public void Infrastructure_DoesNotReferenceTheApi() =>
        Assert.DoesNotContain(
            typeof(DependencyInjection).Assembly.GetReferencedAssemblies().Select(a => a.Name!),
            name => name.StartsWith("ComplianceMonitor.Api", StringComparison.Ordinal));
}
