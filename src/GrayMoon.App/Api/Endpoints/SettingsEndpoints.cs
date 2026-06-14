using GrayMoon.App.Repositories;

namespace GrayMoon.App.Api.Endpoints;

public static class SettingsEndpoints
{
    public static IEndpointRouteBuilder MapSettingsEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapPost("/api/settings/sidebar-collapsed", SetSidebarCollapsed);
        return routes;
    }

    private static async Task<IResult> SetSidebarCollapsed(
        SidebarCollapsedRequest request,
        AppSettingRepository settings)
    {
        await settings.SetBoolAsync(AppSettingRepository.SidebarCollapsedKey, request.Value);
        return Results.NoContent();
    }

    private sealed record SidebarCollapsedRequest(bool Value);
}
