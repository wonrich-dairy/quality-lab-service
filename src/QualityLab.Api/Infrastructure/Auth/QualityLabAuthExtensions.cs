using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using System.Security.Claims;
using System.Text;
using QualityLab.Api.Shared.Authorization;

namespace QualityLab.Api.Infrastructure.Auth;

/// <summary>
/// Authentication and authorization wiring matching the shared Auth Service (SCRUM-34).
/// Tokens issued by Auth Service, validated here independently.
/// </summary>
public static class QualityLabAuthExtensions
{
    public static IServiceCollection AddQualityLabAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        var issuer = configuration["Auth:Issuer"] ?? "wonrich-auth";
        var audience = configuration["Auth:Audience"] ?? "wonrich-services";
        var signingKey = configuration["Auth:SigningKey"]
            ?? throw new InvalidOperationException("Auth:SigningKey is required. Set it in appsettings, user-secrets or environment.");

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = issuer,
                    ValidateAudience = true,
                    ValidAudience = audience,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)),
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.Zero,
                    RoleClaimType = ClaimTypes.Role,
                    NameClaimType = ClaimTypes.NameIdentifier
                };
            });

        return services;
    }

    public static IServiceCollection AddQualityLabAuthorization(this IServiceCollection services)
    {
        services.AddAuthorization(options =>
        {
            // Lab technician (QualityAnalyst role) — can record panels, sensory, determinations
            options.AddPolicy("LabTechnician", policy =>
                policy.RequireRole(WonrichRoles.QualityAnalyst, WonrichRoles.SystemAdministrator));

            // Production Manager — can edit spec thresholds
            options.AddPolicy("ProductionManager", policy =>
                policy.RequireRole(WonrichRoles.ProductionManager, WonrichRoles.SystemAdministrator));

            // Read-only access for wider roles
            options.AddPolicy("LabReader", policy =>
                policy.RequireRole(
                    WonrichRoles.QualityAnalyst,
                    WonrichRoles.ProductionManager,
                    WonrichRoles.SystemAdministrator,
                    WonrichRoles.ProcessingTechnician));
        });
        return services;
    }

    public static IServiceCollection AddQualityLabCors(this IServiceCollection services, IConfiguration configuration)
    {
        var origins = configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
                      ?? ["http://localhost:5173", "http://127.0.0.1:5173"];

        services.AddCors(options =>
        {
            options.AddPolicy("QualityLabCors", policy =>
            {
                policy.WithOrigins(origins)
                      .AllowAnyHeader()
                      .AllowAnyMethod()
                      .AllowCredentials();
            });
        });

        return services;
    }

    public static IApplicationBuilder UseQualityLabCors(this IApplicationBuilder app)
    {
        return app.UseCors("QualityLabCors");
    }
}
