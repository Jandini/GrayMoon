using System.Text;

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

    /// <summary>Protects a token for persistence using Base64 (Level 1). Idempotent for already-protected tokens when they are valid Base64.</summary>
    public static string? ProtectToken(string? plainToken)
    {
        if (string.IsNullOrWhiteSpace(plainToken))
            return null;

        var trimmed = plainToken.Trim();
        // If it already looks like Base64 and round-trips, assume it's protected.
        if (IsLikelyBase64(trimmed))
        {
            try
            {
                var bytes = Convert.FromBase64String(trimmed);
                _ = Encoding.UTF8.GetString(bytes);
                return trimmed;
            }
            catch (FormatException)
            {
                // Fall through and treat as plain text.
            }
        }

        return Convert.ToBase64String(Encoding.UTF8.GetBytes(trimmed));
    }

    /// <summary>Returns the plain-text token from a persisted value (Base64 or legacy plain text).</summary>
    public static string? UnprotectToken(string? storedToken)
    {
        if (string.IsNullOrWhiteSpace(storedToken))
            return null;

        var trimmed = storedToken.Trim();
        try
        {
            var bytes = Convert.FromBase64String(trimmed);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (FormatException)
        {
            // Not Base64; treat as legacy plain-text token.
            return trimmed;
        }
    }

    private static bool IsLikelyBase64(string value)
    {
        if (value.Length == 0 || value.Length % 4 != 0)
            return false;

        foreach (var c in value)
        {
            if (!(char.IsLetterOrDigit(c) || c == '+' || c == '/' || c == '='))
                return false;
        }

        return true;
    }
}
