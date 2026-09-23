using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using System.Text.Json.Serialization;
using QualityLab.Api.Application.Panels;
using QualityLab.Api.Application.Sensory;
using QualityLab.Api.Application.Specs;
using QualityLab.Api.Application.Determinations;
using QualityLab.Api.Infrastructure.Auth;
using QualityLab.Api.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

// ── Controllers ──────────────────────────────────────────────────────────────
builder.Services
    .AddControllers()
    .AddJsonOptions(options => options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

// ── Persistence — Pomelo MySQL (same as processing-service) ──────────────────
var connectionString = builder.Configuration.GetConnectionString("QualityLabDb");

if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException(
        "ConnectionStrings:QualityLabDb is not configured. "
        + "Set it in appsettings.Development.json, user-secrets, or the QLS_DB_CONNECTION env var.");
}

var serverVersion = new MySqlServerVersion(new Version(8, 4, 0));
builder.Services.AddDbContext<QualityLabDbContext>(options =>
    options.UseMySql(connectionString, serverVersion));

// ── Authentication & Authorization (shared JWT from Auth Service) ────────────
builder.Services.AddQualityLabAuthentication(builder.Configuration);
builder.Services.AddQualityLabAuthorization();
builder.Services.AddQualityLabCors(builder.Configuration);

// ── Time ─────────────────────────────────────────────────────────────────────
builder.Services.AddSingleton(TimeProvider.System);

// ── Application services ────────────────────────────────────────────────────
builder.Services.AddScoped<IChemicalPanelService, ChemicalPanelService>();
builder.Services.AddScoped<ISensoryEvaluationService, SensoryEvaluationService>();
builder.Services.AddScoped<ISpecThresholdService, SpecThresholdService>();
builder.Services.AddScoped<IDeterminationService, DeterminationService>();

// ── Health + Swagger ────────────────────────────────────────────────────────
builder.Services.AddProblemDetails();
builder.Services.AddHealthChecks()
    .AddMySql(connectionString);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new Microsoft.OpenApi.OpenApiInfo
    {
        Title = "Quality Lab Service",
        Version = "v1",
        Description = "Wonrich Dairy — Quality Lab testing service for finished-product batches"
    });

    // JWT auth in Swagger — matches processing service pattern
    options.AddSecurityDefinition("Bearer", new Microsoft.OpenApi.OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = Microsoft.OpenApi.SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = Microsoft.OpenApi.ParameterLocation.Header,
        Description = "Access token from POST /api/auth/login. Paste the token only (without Bearer prefix)."
    });

    options.AddSecurityRequirement(document => new Microsoft.OpenApi.OpenApiSecurityRequirement
    {
        [new Microsoft.OpenApi.OpenApiSecuritySchemeReference("Bearer", document)] = []
    });

    // XML comments
    var xmlFile = $"{System.Reflection.Assembly.GetExecutingAssembly().GetName().Name}.xml";
    var xmlPath = Path.Combine(AppContext.BaseDirectory, xmlFile);
    if (File.Exists(xmlPath))
        options.IncludeXmlComments(xmlPath);
});

var app = builder.Build();

// ── Auto-migrate in Dev/Staging ─────────────────────────────────────────────
if (app.Environment.IsDevelopment() || app.Environment.IsStaging())
{
    try
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<QualityLabDbContext>();
        if (db.Database.ProviderName?.Contains("MySql", StringComparison.OrdinalIgnoreCase) == true)
        {
            db.Database.Migrate();
        }
    }
    catch (Exception ex)
    {
        var logger = app.Services.GetRequiredService<ILogger<Program>>();
        logger.LogWarning(ex, "Auto-migrate skipped — database not reachable at startup.");
    }
}

// ── Swagger (disabled in Production) ────────────────────────────────────────
if (!app.Environment.IsProduction())
{
    app.UseSwagger();
    app.UseSwaggerUI(options =>
    {
        options.SwaggerEndpoint("/swagger/v1/swagger.json", "Quality Lab Service v1");
        options.RoutePrefix = "swagger";
    });
}

// ── Pipeline ────────────────────────────────────────────────────────────────
app.UseQualityLabCors();
app.UseAuthentication();
app.UseAuthorization();

// Health is anonymous (container runtime probes)
app.MapHealthChecks("/health", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    ResponseWriter = async (context, report) =>
    {
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(JsonSerializer.Serialize(new
        {
            status = report.Status.ToString(),
            checks = report.Entries.ToDictionary(
                entry => entry.Key,
                entry => new { status = entry.Value.Status.ToString(), description = entry.Value.Description }),
        }));
    }
}).AllowAnonymous();

// Root descriptor
app.MapGet("/", (IWebHostEnvironment env) => Results.Ok(new
{
    service = "Wonrich Quality Lab Service",
    environment = env.EnvironmentName,
    health = "/health",
    swagger = env.IsProduction() ? null : "/swagger",
})).AllowAnonymous();

app.MapControllers();

app.Run();

public partial class Program;
