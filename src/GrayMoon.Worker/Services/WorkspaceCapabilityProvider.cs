using System.Net;
using System.Net.Http.Json;
using GrayMoon.Abstractions.Worker;
using GrayMoon.Abstractions.Workspaces;
using GrayMoon.Worker.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GrayMoon.Worker.Services;

/// <inheritdoc cref="IWorkspaceCapabilityProvider" />
/// <remarks>
/// Three tiers, in order: the cache every app-initiated command warms, then
/// <c>GET /workspaces/{id}/capabilities</c> behind the worker secret, then full enrichment. Only the first
/// two are cached - a fallback is a guess, and caching it would make one unreachable moment permanent for
/// the rest of the process's life.
/// </remarks>
internal sealed class WorkspaceCapabilityProvider(
    IOptions<WorkerOptions> options,
    IWorkerSecretProvider secretProvider,
    ILogger<WorkspaceCapabilityProvider> logger) : IWorkspaceCapabilityProvider
{
    private readonly WorkerOptions _options = options.Value;
    private readonly object _lock = new();
    private readonly Dictionary<int, RepositoryOperationCapabilities> _byWorkspaceId = new();

    public async Task<RepositoryOperationCapabilities> GetAsync(int workspaceId, CancellationToken cancellationToken = default)
    {
        if (workspaceId <= 0)
            return RepositoryOperationCapabilities.LegacyFullEnrichment;

        lock (_lock)
        {
            if (_byWorkspaceId.TryGetValue(workspaceId, out var cached))
                return cached;
        }

        var fetched = await FetchAsync(workspaceId, cancellationToken);
        if (fetched == null)
        {
            logger.LogInformation(
                "WorkspaceCapabilityProvider: could not resolve capabilities for workspace {WorkspaceId}; running full enrichment for this operation.",
                workspaceId);
            return RepositoryOperationCapabilities.LegacyFullEnrichment;
        }

        lock (_lock)
        {
            _byWorkspaceId[workspaceId] = fetched;
        }

        return fetched;
    }

    public void Remember(int workspaceId, RepositoryOperationCapabilities? capabilities)
    {
        if (workspaceId <= 0 || capabilities == null)
            return;

        lock (_lock)
        {
            _byWorkspaceId[workspaceId] = capabilities;
        }
    }

    public void Invalidate(int workspaceId)
    {
        if (workspaceId <= 0)
            return;

        lock (_lock)
        {
            _byWorkspaceId.Remove(workspaceId);
        }
    }

    private async Task<RepositoryOperationCapabilities?> FetchAsync(int workspaceId, CancellationToken cancellationToken)
    {
        var baseUrl = _options.AppApiBaseUrl;
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            logger.LogDebug("WorkspaceCapabilityProvider: AppApiBaseUrl is not configured; cannot obtain capabilities for workspace {WorkspaceId}.", workspaceId);
            return null;
        }

        var path = $"/workspaces/{workspaceId}/capabilities";
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(baseUrl, UriKind.Absolute) };
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            var secret = secretProvider.GetSecret();
            if (!string.IsNullOrEmpty(secret))
                request.Headers.TryAddWithoutValidation(WorkerSecretHeader.Name, secret);

            using var response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                if (response.StatusCode == HttpStatusCode.Unauthorized)
                    logger.LogWarning("WorkspaceCapabilityProvider: the App rejected this Worker's secret for workspace {WorkspaceId}. Reinstall the Worker from GrayMoon > Worker.", workspaceId);
                else
                    logger.LogWarning("WorkspaceCapabilityProvider: GET {Path} failed for workspace {WorkspaceId} with status {StatusCode}.", path, workspaceId, response.StatusCode);
                return null;
            }

            var payload = await response.Content.ReadFromJsonAsync<RepositoryOperationCapabilities>(cancellationToken: cancellationToken);
            if (payload == null)
            {
                logger.LogWarning("WorkspaceCapabilityProvider: empty payload for workspace {WorkspaceId}.", workspaceId);
                return null;
            }

            logger.LogDebug(
                "WorkspaceCapabilityProvider: workspace {WorkspaceId} calculateRepositoryVersion={CalculateVersion}, discoverDotNetProjects={DiscoverProjects}.",
                workspaceId, payload.ShouldCalculateVersion, payload.ShouldDiscoverProjects);
            return payload;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "WorkspaceCapabilityProvider: error obtaining capabilities for workspace {WorkspaceId}.", workspaceId);
            return null;
        }
    }
}
