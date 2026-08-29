using GrayMoon.Agent.Models;

namespace GrayMoon.Agent.Abstractions;

/// <summary>Parses SDK-style .csproj files.</summary>
public interface ICsProjFileParser
{
    /// <summary>Parses an SDK-style .csproj file and returns project type, target framework, name, and package references; returns null if the file is missing or invalid.</summary>
    Task<CsProjFileInfo?> ParseAsync(string csprojPath, CancellationToken cancellationToken = default);

    /// <summary>Updates only the Version of PackageReference elements whose Include matches a key in <paramref name="packageIdToNewVersion"/>. Does not change any other content in the .csproj file.</summary>
    /// <returns>True if the file was modified.</returns>
    Task<bool> UpdateAsync(string csprojPath, IReadOnlyDictionary<string, string> packageIdToNewVersion, CancellationToken cancellationToken = default);

    /// <summary>
    /// Finds the PackageReference Include name whose element text contains a match for the given version-pattern
    /// prefix/suffix (e.g. prefix="Version=\"" suffix="\" />"), scanning whole PackageReference elements so
    /// multiline attributes, attribute ordering, and whitespace variations are handled the same way as <see cref="UpdateAsync"/>.
    /// Returns null if the file is missing/invalid or no PackageReference element matches.
    /// </summary>
    Task<string?> FindPackageReferenceForVersionPatternAsync(string csprojPath, string prefix, string suffix, CancellationToken cancellationToken = default);
}
