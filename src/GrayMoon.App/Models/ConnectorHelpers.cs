using System.Text;
using GrayMoon.App.Services.Security;

namespace GrayMoon.App.Models;

public static class ConnectorHelpers
{
    private static ITokenProtector? _tokenProtector;

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

    /// <summary>Initializes the token protector used by ProtectToken/UnprotectToken. Called once at app startup.</summary>
    public static void InitializeTokenProtector(ITokenProtector tokenProtector)
    {
        _tokenProtector = tokenProtector ?? throw new ArgumentNullException(nameof(tokenProtector));
    }

    /// <summary>Protects a token for persistence. Uses AES-GCM via ITokenProtector when available, otherwise falls back to Base64 (Level 1).</summary>
    public static string? ProtectToken(string? plainToken)
    {
        if (string.IsNullOrWhiteSpace(plainToken))
            return null;

        var trimmed = plainToken.Trim();

        if (_tokenProtector != null)
            return _tokenProtector.Protect(trimmed);

        // Legacy fallback: Base64 obfuscation when no protector has been initialized.
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(trimmed));
    }

    /// <summary>Returns the plain-text token from a persisted value. Uses AES-GCM via ITokenProtector when available, otherwise supports Base64 or legacy plain text.</summary>
    public static string? UnprotectToken(string? storedToken)
    {
        if (string.IsNullOrWhiteSpace(storedToken))
            return null;

        var trimmed = storedToken.Trim();

        if (_tokenProtector != null)
            return _tokenProtector.Unprotect(trimmed);

        // Legacy fallback: try Base64, otherwise treat as plain text.
        try
        {
            var bytes = Convert.FromBase64String(trimmed);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (FormatException)
        {
            return trimmed;
        }
    }
}
