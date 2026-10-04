using System.Text.Json;
using System.Text.Json.Serialization;
using ComplianceMonitor.Api.Classification;
using ComplianceMonitor.Api.Endpoints;
using ComplianceMonitor.Api.Errors;
using ComplianceMonitor.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
// camelCase properties (web default); enums as strings. ComplianceResult and DecisionSource also carry
// [JsonStringEnumMemberName] so they serialize the same way outside HTTP (database, logs, tests).
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseUpper)));
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<HuggingFaceExceptionHandler>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddComplianceClassification(builder.Configuration);
builder.Services.AddDbContext<ComplianceDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString(ComplianceDbContext.ConnectionStringName)));
builder.Services.AddScoped<AnalysisStore>();

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
app.MapAnalysisEndpoints();

app.Run();
