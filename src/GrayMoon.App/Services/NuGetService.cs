using System.Net.Http.Headers;
using System.Text.Json;
using GrayMoon.App.Models;

namespace GrayMoon.App.Services;

public class NuGetService : IConnectorService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<NuGetService> _logger;

    public ConnectorType ConnectorType => ConnectorType.NuGet;

    public NuGetService(HttpClient httpClient, ILogger<NuGetService> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Checks whether a package with the given id exists in the registry.</summary>
    public async Task<bool> PackageExistsAsync(Connector connector, string packageId, CancellationToken cancellationToken = default)
    {
        if (connector.ConnectorType != ConnectorType.NuGet)
            throw new InvalidOperationException($"Connector type {connector.ConnectorType} is not supported by NuGetService.");
        if (string.IsNullOrWhiteSpace(packageId))
            return false;

        var baseAddress = await GetPackageBaseAddressAsync(connector, cancellationToken);
        if (string.IsNullOrEmpty(baseAddress))
        {
            _logger.LogTrace("PackageExists: connector {ConnectorName} has no PackageBaseAddress.", connector.ConnectorName);
            return false;
        }

        var lowerId = packageId.Trim().ToLowerInvariant();
        var url = $"{baseAddress.TrimEnd('/')}/{lowerId}/index.json";
        _logger.LogTrace("PackageExists: GET {Url} (connector {ConnectorName}).", url, connector.ConnectorName);
        using var request = CreateRequest(HttpMethod.Get, url, connector);
        var response = await _httpClient.SendAsync(request, cancellationToken);
        _logger.LogTrace("PackageExists: {Url} -> StatusCode={StatusCode}.", url, (int)response.StatusCode);
        if (response.StatusCode == System.Net.HttpStatusCode.Forbidden && ConnectorHelpers.IsGitHubPackages(connector.ApiBaseUrl))
            _logger.LogTrace("PackageExists: 403 from GitHub Packages. Ensure the connector uses a classic PAT (not fine-grained) with read:packages scope, and if the org uses SSO, authorize the token for SSO.");
        return response.IsSuccessStatusCode;
    }

    /// <summary>Checks whether a package with the given id and version exists in the registry.</summary>
    public async Task<bool> PackageVersionExistsAsync(Connector connector, string packageId, string version, CancellationToken cancellationToken = default)
    {
        if (connector.ConnectorType != ConnectorType.NuGet)
            throw new InvalidOperationException($"Connector type {connector.ConnectorType} is not supported by NuGetService.");
        if (string.IsNullOrWhiteSpace(packageId) || string.IsNullOrWhiteSpace(version))
            return false;

        var baseAddress = await GetPackageBaseAddressAsync(connector, cancellationToken);
        if (string.IsNullOrEmpty(baseAddress))
            return false;

        var lowerId = packageId.Trim().ToLowerInvariant();
        var url = $"{baseAddress.TrimEnd('/')}/{lowerId}/index.json";
        using var request = CreateRequest(HttpMethod.Get, url, connector);
        var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            return false;

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (!doc.RootElement.TryGetProperty("versions", out var versions) || versions.ValueKind != JsonValueKind.Array)
            return false;

        var versionNorm = version.Trim();
        foreach (var v in versions.EnumerateArray())
        {
            var s = v.GetString();
            if (string.IsNullOrEmpty(s)) continue;
            if (string.Equals(s, versionNorm, StringComparison.OrdinalIgnoreCase))
                return true;
            if (NormalizeVersion(s) == NormalizeVersion(versionNorm))
                return true;
        }
        return false;
    }

    /// <summary>Gets the PackageBaseAddress URL from the NuGet service index (e.g. https://api.nuget.org/v3-flatcontainer/).</summary>
    public async Task<string?> GetPackageBaseAddressAsync(Connector connector, CancellationToken cancellationToken = default)
    {
        if (connector.ConnectorType != ConnectorType.NuGet)
            throw new InvalidOperationException($"Connector type {connector.ConnectorType} is not supported by NuGetService.");

        var indexUrl = connector.ApiBaseUrl?.TrimEnd('/') ?? "";
        if (!indexUrl.EndsWith("/index.json", StringComparison.OrdinalIgnoreCase))
        {
            // GitHub Packages uses .../index.json (no v3 in path); NuGet.org and others use .../v3/index.json
            if (ConnectorHelpers.IsGitHubPackages(connector.ApiBaseUrl))
                indexUrl = indexUrl + "/index.json";
            else
                indexUrl = indexUrl.EndsWith("/v3", StringComparison.OrdinalIgnoreCase) ? indexUrl + "/index.json" : indexUrl + "/v3/index.json";
        }

        _logger.LogTrace("GetPackageBaseAddress: GET {IndexUrl} (connector {ConnectorName}).", indexUrl, connector.ConnectorName);
        using var request = CreateRequest(HttpMethod.Get, indexUrl, connector);
        var response = await _httpClient.SendAsync(request, cancellationToken);
        _logger.LogTrace("GetPackageBaseAddress: index {IndexUrl} -> StatusCode={StatusCode}.", indexUrl, (int)response.StatusCode);
        if (!response.IsSuccessStatusCode)
            return null;

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (!doc.RootElement.TryGetProperty("resources", out var resources) || resources.ValueKind != JsonValueKind.Array)
        {
            _logger.LogTrace("GetPackageBaseAddress: no resources array in index for {ConnectorName}.", connector.ConnectorName);
            return null;
        }

        foreach (var r in resources.EnumerateArray())
        {
            if (r.TryGetProperty("@type", out var type))
            {
                var typeStr = type.GetString() ?? "";
                if (typeStr.Contains("PackageBaseAddress", StringComparison.OrdinalIgnoreCase))
                {
                    if (r.TryGetProperty("@id", out var id))
                    {
                        var baseAddress = id.GetString();
                        _logger.LogTrace("GetPackageBaseAddress: {ConnectorName} -> {BaseAddress}.", connector.ConnectorName, baseAddress);
                        return baseAddress;
                    }
                    break;
                }
            }
        }
        _logger.LogTrace("GetPackageBaseAddress: no PackageBaseAddress resource for {ConnectorName}.", connector.ConnectorName);
        return null;
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string url, Connector connector)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        if (ConnectorHelpers.IsGitHubPackages(connector.ApiBaseUrl))
        {
            // GitHub Packages NuGet requires Basic auth with username and token
            var token = connector.UserToken?.Trim();
            var username = connector.UserName?.Trim();
            if (!string.IsNullOrEmpty(token))
            {
                var user = string.IsNullOrEmpty(username) ? "USER" : username;
                var credentials = Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes($"{user}:{token}"));
                request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
                _logger.LogTrace("CreateRequest: using Basic auth for connector {ConnectorName} (user: {UserName}).", connector.ConnectorName, user);
            }
            else
                _logger.LogTrace("CreateRequest: GitHub Packages connector {ConnectorName} has no UserToken; request will be unauthenticated.", connector.ConnectorName);
        }
        else if (ConnectorHelpers.IsProGet(connector.ApiBaseUrl) && !string.IsNullOrWhiteSpace(connector.UserToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", connector.UserToken);
        }

        return request;
    }

    private static string NormalizeVersion(string v)
    {
        var parts = v.Trim().Split('.');
        if (parts.Length >= 4) return v.Trim();
        if (parts.Length == 3) return v.Trim();
        if (parts.Length == 2) return v.Trim() + ".0";
        if (parts.Length == 1) return v.Trim() + ".0.0";
        return v.Trim();
    }

    public async Task<bool> TestConnectionAsync(Connector connector)
    {
        if (connector.ConnectorType != ConnectorType.NuGet)
        {
            throw new InvalidOperationException($"Connector type {connector.ConnectorType} is not supported by NuGetService.");
        }

        // NuGet.org doesn't require authentication
        if (ConnectorHelpers.IsNuGetOrg(connector.ApiBaseUrl))
        {
            return await TestNuGetOrgConnectionAsync(connector);
        }

        // Other registries require token
        if (string.IsNullOrWhiteSpace(connector.UserToken))
        {
            throw new InvalidOperationException("Connector token is required for this NuGet registry.");
        }

        return await TestAuthenticatedNuGetConnectionAsync(connector);
    }

    private async Task<bool> TestNuGetOrgConnectionAsync(Connector connector)
    {
        try
        {
            var url = connector.ApiBaseUrl.TrimEnd('/');
            if (!url.EndsWith("/v3/index.json", StringComparison.OrdinalIgnoreCase))
            {
                url = url.EndsWith("/v3", StringComparison.OrdinalIgnoreCase)
                    ? url + "/index.json"
                    : url + "/v3/index.json";
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            var response = await _httpClient.SendAsync(request);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to test NuGet.org connection.");
            return false;
        }
    }

    private async Task<bool> TestAuthenticatedNuGetConnectionAsync(Connector connector)
    {
        try
        {
            var url = connector.ApiBaseUrl.TrimEnd('/');
            if (!url.EndsWith("/index.json", StringComparison.OrdinalIgnoreCase))
            {
                url = url + "/index.json";
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            if (!string.IsNullOrWhiteSpace(connector.UserToken))
            {
                if (ConnectorHelpers.IsGitHubPackages(connector.ApiBaseUrl))
                {
                    // GitHub Packages uses Basic auth with username:token
                    var username = connector.UserName ?? "USER";
                    var credentials = Convert.ToBase64String(
                        System.Text.Encoding.ASCII.GetBytes($"{username}:{connector.UserToken}"));
                    request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
                }
                else if (ConnectorHelpers.IsProGet(connector.ApiBaseUrl))
                {
                    // ProGet uses API key in header or Basic auth
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", connector.UserToken);
                }
            }

            var response = await _httpClient.SendAsync(request);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to test authenticated NuGet connection.");
            return false;
        }
    }
}
