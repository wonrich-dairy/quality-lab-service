using System.Security.Claims;

namespace QualityLab.Api.Shared.Authorization;

/// <summary>
/// Reads the Wonrich claims off an authenticated principal.
/// EXACT COPY from Auth shared service.
/// </summary>
public static class WonrichPrincipalExtensions
{
    public static string? UserId(this ClaimsPrincipal principal) =>
        principal.FindFirstValue(ClaimTypes.NameIdentifier);

    public static string? UserName(this ClaimsPrincipal principal) =>
        principal.FindFirstValue(ClaimTypes.Name);

    public static string? Role(this ClaimsPrincipal principal) =>
        principal.FindFirstValue(ClaimTypes.Role);
}
