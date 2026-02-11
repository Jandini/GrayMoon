namespace GrayMoon.App.Models;

public static class ConnectorHelpers
{
    public static bool RequiresToken(ConnectorType connectorType, string apiBaseUrl)
    {
        if (string.IsNullOrWhiteSpace(apiBaseUrl))
        {
            return connectorType == ConnectorType.GitHub; // GitHub always requires token
        }

        return connectorType switch
        {
            ConnectorType.GitHub => true, // Always requires token
            ConnectorType.NuGet => !IsNuGetOrg(apiBaseUrl),
            _ => true
        };
    }

    public static bool IsNuGetOrg(string apiBaseUrl)
    {
        if (string.IsNullOrWhiteSpace(apiBaseUrl))
            return false;

        return apiBaseUrl.Contains("nuget.org", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsGitHubPackages(string apiBaseUrl)
    {
        if (string.IsNullOrWhiteSpace(apiBaseUrl))
            return false;

        return apiBaseUrl.Contains("pkg.github.com", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsProGet(string apiBaseUrl)
    {
        if (string.IsNullOrWhiteSpace(apiBaseUrl))
            return false;

        return apiBaseUrl.Contains("proget", StringComparison.OrdinalIgnoreCase);
    }

    public static string GetDefaultUrl(ConnectorType connectorType, string? registryHint = null)
    {
        return connectorType switch
        {
            ConnectorType.GitHub => "https://api.github.com/",
            ConnectorType.NuGet => registryHint switch
            {
                "github" => "https://nuget.pkg.github.com/",
                "nugetorg" => "https://api.nuget.org/v3/index.json",
                _ => "https://api.nuget.org/v3/index.json" // Default to NuGet.org
            },
            _ => throw new NotSupportedException($"Connector type {connectorType} is not supported.")
        };
    }

    public static bool ShouldShowUserNameField(ConnectorType connectorType, string apiBaseUrl)
    {
        if (connectorType == ConnectorType.GitHub)
            return true; // Optional but shown

        if (IsNuGetOrg(apiBaseUrl))
            return false; // Not needed for public NuGet.org

        if (IsProGet(apiBaseUrl) && connectorType == ConnectorType.NuGet)
            return true; // ProGet NuGet may use Basic auth

        if (IsGitHubPackages(apiBaseUrl))
            return true; // GitHub Packages uses Basic auth with username:token

        return false;
    }

    public static bool ShouldShowTokenField(ConnectorType connectorType, string apiBaseUrl)
    {
        if (IsNuGetOrg(apiBaseUrl))
            return false; // Never show for NuGet.org

        return true; // Show for GitHub, NuGet (non-NuGet.org)
    }
}
