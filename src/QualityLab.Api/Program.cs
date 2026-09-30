using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using System.Text.Json;
using System.Text.Json.Serialization;
using Prometheus;
using QualityLab.Api.Application.Panels;
using QualityLab.Api.Application.Sensory;
using QualityLab.Api.Application.Specs;
using QualityLab.Api.Application.Determinations;
using QualityLab.Api.Health;
using QualityLab.Api.Infrastructure.Auth;
using QualityLab.Api.Infrastructure.Kafka;
using QualityLab.Api.Infrastructure.Sync;
using QualityLab.Api.Infrastructure.Persistence;
using QualityLab.Api.Observability;

var builder = WebApplication.CreateBuilder(args);

// ── Controllers ──────────────────────────────────────────────────────────────
builder.Services
    .AddControllers()
    .AddJsonOptions(options => options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

// ── Persistence — MySQL (EF Core 10 + Microting fork) ────────────────────────
var connectionString = builder.Configuration.GetConnectionString("QualityLabDb");

if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException(
        "ConnectionStrings:QualityLabDb is not configured. "
        + "Set it in .env (Docker), user secrets (dotnet run) or App Service settings (Azure).");
}

var serverVersion = new MySqlServerVersion(new Version(8, 0, 21));
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

// ── Kafka consumer (ProcessingCompleted → work queue) ──────────────────────
builder.Services.AddHostedService<ProcessingCompletedConsumer>();

// ── Processing DB sync (direct polling — works without Kafka) ───────────────
builder.Services.AddHostedService<ProcessingDbSyncService>();

// ── Observability (SCRUM-111) ───────────────────────────────────────────────
// Passive listener on Processing's stage events: records correlation IDs across the
// Kafka hop. Own consumer group, never commits offsets, so it takes no messages from
// ProcessingCompletedConsumer. Disabled with Kafka:StageEventListener:Enabled=false.
builder.Services.AddHostedService<StageEventListener>();
QualityLabMetrics.Initialise();

// ── Health + Swagger ────────────────────────────────────────────────────────
// mysql failing = Unhealthy (deploy fails); Kafka failing = Degraded (deploy still passes).
builder.Services.AddProblemDetails();
builder.Services.AddHealthChecks()
    .AddMySql(connectionString, name: "mysql")
    .AddCheck<KafkaHealthCheck>("kafka", failureStatus: HealthStatus.Degraded);

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

// ── Observability first: every later log line carries the correlation ID, and every
// request is measured, including ones rejected by authentication (SCRUM-111).
app.UseCorrelationId();
app.UseRequestMetrics();

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

// Health is anonymous (container runtime probes and the post-deploy check).
// Shape { status, checks: [ { name, status, description } ] } is what
// scripts/verify-health.sh and HealthEndpointTests read — keep it.
app.MapHealthChecks("/health", new HealthCheckOptions
{
    ResponseWriter = async (context, report) =>
    {
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(JsonSerializer.Serialize(new
        {
            status = report.Status.ToString(),
            checks = report.Entries.Select(e => new
            {
                name = e.Key,
                status = e.Value.Status.ToString(),
                description = e.Value.Description
            })
        }));
    }
}).AllowAnonymous();

// Prometheus scrape endpoint — anonymous so Prometheus needs no token (SCRUM-111)
app.MapMetrics().AllowAnonymous();

// Deployed commit — the pipeline waits for this to show the new SHA (SCRUM-109)
app.MapGet("/version", () => Results.Ok(new
{
    sha = Environment.GetEnvironmentVariable("GIT_SHA") ?? "local"
})).AllowAnonymous();

// Root descriptor
app.MapGet("/", (IWebHostEnvironment env) => Results.Ok(new
{
    service = "Wonrich Quality Lab Service",
    environment = env.EnvironmentName,
    health = "/health",
    version = "/version",
    metrics = "/metrics",
    swagger = env.IsProduction() ? null : "/swagger",
})).AllowAnonymous();

app.MapControllers();

app.Run();

public partial class Program;