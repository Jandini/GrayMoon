using System.Collections.Concurrent;
using System.Text.Json;
using GrayMoon.App.Models;
using GrayMoon.App.Services.Connectors;
using Microsoft.Extensions.Options;

namespace GrayMoon.App.Services.Versions;

/// <summary>What one registry lookup says about a package version.</summary>
public enum PackageAvailability
{
    /// <summary>The registry lists the version.</summary>
    Available,

    /// <summary>The registry answered and does not list the version (<see cref="PackageVersionLookupKind.VersionNotFound"/>).</summary>
    Missing,

    /// <summary>Any other answer: authentication, timeout, server error, malformed answer. Never "missing".</summary>
    Unknown,
}

/// <summary>One (connector, package, version) to look up.</summary>
public sealed record PackageLookupRequest(Connector Connector, string PackageId, string Version)
{
    internal (int ConnectorId, string PackageId, string Version) Key
        => (Connector.ConnectorId, PackageId.Trim().ToLowerInvariant(), Version.Trim());
}

/// <summary>The outcome of one lookup, with the registry's original classification and HTTP status.</summary>
public sealed record PackageLookupOutcome(
    PackageAvailability Availability,
    PackageVersionLookupKind Kind,
    int? StatusCode,
    string? Error);

/// <summary>
/// Remembers <see cref="PackageAvailability.Available"/> answers for a short time, per (connector, package, version).
/// Nothing else is cached: a missing version or a failure is asked again every time. Singleton; bounded.
/// </summary>
public sealed class PackageAvailabilityCache(TimeProvider timeProvider)
{
    internal static readonly TimeSpan Ttl = TimeSpan.FromMinutes(2);
    private const int MaxEntries = 4096;
    private readonly ConcurrentDictionary<(int, string, string), DateTimeOffset> _availableUntil = new();

    public bool IsAvailable((int, string, string) key)
    {
        if (!_availableUntil.TryGetValue(key, out var until))
            return false;
        if (until > timeProvider.GetUtcNow())
            return true;
        _availableUntil.TryRemove(key, out _);
        return false;
    }

    public void Forget((int, string, string) key) => _availableUntil.TryRemove(key, out _);

    public void SetAvailable((int, string, string) key)
    {
        var now = timeProvider.GetUtcNow();
        if (_availableUntil.Count >= MaxEntries)
        {
            foreach (var expired in _availableUntil.Where(e => e.Value <= now).Select(e => e.Key).ToList())
                _availableUntil.TryRemove(expired, out _);
            if (_availableUntil.Count >= MaxEntries)
                _availableUntil.Clear();
        }

        _availableUntil[key] = now + Ttl;
    }
}

/// <summary>
/// Looks up required packages on their mapped connectors for effective versions (design 6.1, D3). Deduplicates per
/// (connector, package, version), bounds the parallel lookups by <see cref="WorkspaceOptions.MaxParallelOperations"/>, and
/// uses <see cref="PackageAvailabilityCache"/> unless <c>bypassCache</c> is set (the start of an operation always asks
/// the registry). Maps the registry's answer to available, missing or unknown; an exception is unknown, never missing.
/// </summary>
public sealed class PackageAvailabilityVerifier(
    NuGetService nuGetService,
    PackageAvailabilityCache cache,
    IOptions<WorkspaceOptions> workspaceOptions,
    ILogger<PackageAvailabilityVerifier> logger)
{
    public async Task<IReadOnlyDictionary<(int ConnectorId, string PackageId, string Version), PackageLookupOutcome>> VerifyAsync(
        IEnumerable<PackageLookupRequest> requests,
        bool bypassCache,
        CancellationToken cancellationToken)
    {
        var distinct = requests
            .GroupBy(r => r.Key)
            .Select(g => g.First())
            .ToList();
        var results = new ConcurrentDictionary<(int, string, string), PackageLookupOutcome>();
        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(1, workspaceOptions.Value.MaxParallelOperations),
            CancellationToken = cancellationToken,
        };

        await Parallel.ForEachAsync(distinct, options, async (request, ct) =>
        {
            var key = request.Key;
            if (!bypassCache && cache.IsAvailable(key))
            {
                results[key] = new PackageLookupOutcome(PackageAvailability.Available, PackageVersionLookupKind.Available, null, null);
                return;
            }

            var outcome = await LookupAsync(request, ct);
            // A fresh answer replaces whatever was cached: only "available" is remembered.
            if (outcome.Availability == PackageAvailability.Available)
                cache.SetAvailable(key);
            else
                cache.Forget(key);
            results[key] = outcome;
        });

        return results.ToDictionary(r => r.Key, r => r.Value);
    }

    private async Task<PackageLookupOutcome> LookupAsync(PackageLookupRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var lookup = await nuGetService.LookupPackageVersionAsync(request.Connector, request.PackageId, request.Version, cancellationToken);
            var availability = lookup.Kind switch
            {
                PackageVersionLookupKind.Available when lookup.Exists => PackageAvailability.Available,
                PackageVersionLookupKind.VersionNotFound => PackageAvailability.Missing,
                _ => PackageAvailability.Unknown,
            };
            return new PackageLookupOutcome(availability, lookup.Kind, lookup.StatusCode, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
        {
            logger.LogWarning("Package lookup timed out: {PackageId} {Version} on {Connector}", request.PackageId, request.Version, request.Connector.ConnectorName);
            return new PackageLookupOutcome(PackageAvailability.Unknown, PackageVersionLookupKind.RequestTimeout, null, ex.Message);
        }
        catch (JsonException ex)
        {
            logger.LogWarning("Package lookup gave a malformed answer: {PackageId} {Version} on {Connector}", request.PackageId, request.Version, request.Connector.ConnectorName);
            return new PackageLookupOutcome(PackageAvailability.Unknown, PackageVersionLookupKind.UnexpectedResponse, null, ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Package lookup failed: {PackageId} {Version} on {Connector}", request.PackageId, request.Version, request.Connector.ConnectorName);
            return new PackageLookupOutcome(PackageAvailability.Unknown, PackageVersionLookupKind.RegistryFailure, null, ex.Message);
        }
    }
}
