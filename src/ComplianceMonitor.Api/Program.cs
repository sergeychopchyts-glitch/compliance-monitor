using System.Text.Json;
using System.Text.Json.Serialization;
using ComplianceMonitor.Api.Errors;
using ComplianceMonitor.Api.Features.Analyze;
using ComplianceMonitor.Api.Features.History;
using ComplianceMonitor.Api.Features.Summary;
using ComplianceMonitor.Application.Compliance;
using ComplianceMonitor.Infrastructure;
using ComplianceMonitor.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddValidation();
// camelCase properties (web default); enums as uppercase strings: Complies -> "COMPLIES", MissingTemporalEvidence -> "MISSING_TEMPORAL_EVIDENCE".
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseUpper)));
// Built-in validation names errors after C# properties ("Action"); the contract is camelCase everywhere ("action").
builder.Services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
{
    if (context.ProblemDetails is HttpValidationProblemDetails validation)
    {
        validation.Errors = validation.Errors.ToDictionary(e => JsonNamingPolicy.CamelCase.ConvertName(e.Key), e => e.Value);
    }
});
builder.Services.AddExceptionHandler<ModelGatewayExceptionHandler>();

// Composition root: Application use case + Infrastructure implementations.
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddOptions<ComplianceSettings>()
    .Bind(builder.Configuration.GetSection(ComplianceSettings.SectionName))
    .Validate(s => s.ConfidenceThreshold is >= 0 and <= 1, "Compliance:ConfidenceThreshold must be between 0 and 1.")
    .ValidateOnStart();
builder.Services.AddSingleton(provider => provider.GetRequiredService<IOptions<ComplianceSettings>>().Value);
builder.Services.AddScoped<IComplianceAnalysisService, ComplianceAnalysisService>();
builder.Services.AddInfrastructure(builder.Configuration);

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    scope.ServiceProvider.GetRequiredService<ComplianceDbContext>().Database.Migrate();
}

app.UseExceptionHandler();
app.UseStatusCodePages();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

app.MapGet("/health", () => TypedResults.Ok(new { status = "ok" }))
    .WithName("Health");

app.MapGroup("")
    .WithTags("Analysis")
    .MapAnalyze()
    .MapHistory()
    .MapSummary();

app.Run();
