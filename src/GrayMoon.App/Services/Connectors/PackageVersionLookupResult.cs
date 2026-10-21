namespace GrayMoon.App.Services.Connectors;

/// <summary>Why a package-version registry query did or did not treat the version as present. Informational; the boolean decision stays on <see cref="PackageVersionLookupResult.Exists"/>.</summary>
internal enum PackageVersionLookupKind
{
    Available,
    VersionNotFound,
    AuthenticationFailed,
    RegistryFailure,
    RequestTimeout,
    UnexpectedResponse,
    Inconclusive,
    Canceled
}

/// <summary>Result of one package-version lookup. <see cref="Exists"/> is the same decision <c>PackageVersionExistsAsync</c> has always returned.</summary>
internal readonly record struct PackageVersionLookupResult(bool Exists, PackageVersionLookupKind Kind, int? StatusCode)
{
    public static PackageVersionLookupResult Absent(PackageVersionLookupKind kind, int? statusCode = null)
        => new(false, kind, statusCode);
}
