using GrayMoon.App.Api.Endpoints;

namespace GrayMoon.App.Api;

public static class ApiEndpointRegistration
{
    public static IEndpointRouteBuilder MapApiEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapAboutEndpoints();
        routes.MapAgentEndpoints();
        routes.MapSyncEndpoints();
        routes.MapCommitSyncEndpoints();
        routes.MapConnectorEndpoints();
        routes.MapBranchEndpoints();
        routes.MapWorkspaceEndpoints();
        return routes;
    }
}
