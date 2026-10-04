using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using ComplianceMonitor.Api.Errors;
using ComplianceMonitor.Api.Features.Analyze;
using ComplianceMonitor.Api.Features.History;
using ComplianceMonitor.Api.Features.Summary;
using ComplianceMonitor.Api.RateLimiting;
using ComplianceMonitor.Application.Compliance;
using ComplianceMonitor.Infrastructure;
using ComplianceMonitor.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
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

builder.Services.AddOptions<AnalyzeRateLimitOptions>()
    .Bind(builder.Configuration.GetSection(AnalyzeRateLimitOptions.SectionName))
    .Validate(o => o.PermitLimit >= 1 && o.QueueLimit >= 0, "RateLimiting:Analyze needs PermitLimit >= 1 and QueueLimit >= 0.")
    .ValidateOnStart();
builder.Services.AddRateLimiter(options =>
{
    // One global concurrency limiter for /analyze (a constant partition key), configured from options per request.
    options.AddPolicy(AnalyzeRateLimitOptions.PolicyName, context =>
    {
        var limits = context.RequestServices.GetRequiredService<IOptions<AnalyzeRateLimitOptions>>().Value;
        return RateLimitPartition.GetConcurrencyLimiter(AnalyzeRateLimitOptions.PolicyName, _ => new ConcurrencyLimiterOptions
        {
            PermitLimit = limits.PermitLimit,
            QueueLimit = limits.QueueLimit,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
        });
    });
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = async (context, cancellationToken) =>
        await context.HttpContext.RequestServices.GetRequiredService<IProblemDetailsService>().WriteAsync(new ProblemDetailsContext
        {
            HttpContext = context.HttpContext,
            ProblemDetails = new ProblemDetails
            {
                Status = StatusCodes.Status429TooManyRequests,
                Type = ModelGatewayExceptionHandler.TypePrefix + "too-many-analyses",
                Title = "Too many analyses are running. Try again shortly.",
            },
        });
});

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    scope.ServiceProvider.GetRequiredService<ComplianceDbContext>().Database.Migrate();
}

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseRateLimiter();

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
