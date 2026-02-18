using GrayMoon.App.Api.Endpoints;
using Microsoft.AspNetCore.Routing;

namespace GrayMoon.App.Api;

public static class ApiEndpointRegistration
{
    public static IEndpointRouteBuilder MapApiEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapAboutEndpoints();
        routes.MapAgentEndpoints();
        routes.MapSyncEndpoints();
        routes.MapCommitSyncEndpoints();
        routes.MapBranchEndpoints();
        routes.MapWorkspaceEndpoints();
        return routes;
    }
}
