using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using GrayMoon.Agent.Abstractions;
using GrayMoon.Agent.Models;

namespace GrayMoon.Agent.Services;

public sealed class CsProjFileParser : ICsProjFileParser
{
    // Matches a complete PackageReference element — self-closing (<PackageReference ... />) or with child
    // elements (<PackageReference ...>...</PackageReference>). Singleline so . spans newlines, which lets
    // a single match cover attributes spread across multiple lines.
    private static readonly Regex PackageReferenceElementRegex = new(
        @"<PackageReference\b.*?(?:/>|</PackageReference\s*>)",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex IncludeAttributeRegex = new(
        @"\bInclude\s*=\s*""([^""]*)""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Captures the opening-quote prefix and closing quote so only the value between them is swapped.
    private static readonly Regex VersionAttributeRegex = new(
        @"(\bVersion\s*=\s*"")[^""]*("")",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex VersionElementRegex = new(
        @"(<Version\s*>)[^<]*(</Version\s*>)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public Task<CsProjFileInfo?> ParseAsync(string csprojPath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(csprojPath) || !File.Exists(csprojPath))
            return Task.FromResult<CsProjFileInfo?>(null);

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var doc = XDocument.Load(csprojPath);
            var result = ParseCsProjDocument(doc, csprojPath);
            return Task.FromResult(result);
        }
        catch
        {
            return Task.FromResult<CsProjFileInfo?>(null);
        }
    }

    public async Task<bool> UpdateAsync(string csprojPath, IReadOnlyDictionary<string, string> packageIdToNewVersion, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(csprojPath) || !File.Exists(csprojPath) || packageIdToNewVersion == null || packageIdToNewVersion.Count == 0)
            return false;

        cancellationToken.ThrowIfCancellationRequested();

        var lookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in packageIdToNewVersion)
            lookup[kv.Key] = kv.Value;

        // Read as raw text so we can write back with every byte preserved except the version values.
        Encoding fileEncoding;
        string content;
        try
        {
            using var reader = new StreamReader(csprojPath, detectEncodingFromByteOrderMarks: true);
            content = await reader.ReadToEndAsync(cancellationToken);
            fileEncoding = reader.CurrentEncoding;
        }
        catch
        {
            return false;
        }

        var modified = false;

        var newContent = PackageReferenceElementRegex.Replace(content, match =>
        {
            var elementText = match.Value;

            var includeMatch = IncludeAttributeRegex.Match(elementText);
            if (!includeMatch.Success) return elementText;

            var includeName = includeMatch.Groups[1].Value.Trim();
            if (!lookup.TryGetValue(includeName, out var newVersion)) return elementText;

            // Prefer Version attribute; fall back to <Version> child element.
            if (VersionAttributeRegex.IsMatch(elementText))
            {
                var updated = VersionAttributeRegex.Replace(elementText,
                    m => m.Groups[1].Value + newVersion + m.Groups[2].Value);
                if (updated != elementText) modified = true;
                return updated;
            }

            if (VersionElementRegex.IsMatch(elementText))
            {
                var updated = VersionElementRegex.Replace(elementText,
                    m => m.Groups[1].Value + newVersion + m.Groups[2].Value);
                if (updated != elementText) modified = true;
                return updated;
            }

            return elementText;
        });

        if (modified)
            await File.WriteAllTextAsync(csprojPath, newContent, fileEncoding, cancellationToken);

        return modified;
    }

    private static CsProjFileInfo? ParseCsProjDocument(XDocument doc, string csprojPath)
    {
        var root = doc.Root;
        if (root == null)
            return null;

        var sdk = root.Attribute("Sdk")?.Value?.Trim();
        var allElements = root.Descendants().ToList();

        string? GetFirstPropertyValue(string localName)
        {
            var el = allElements.FirstOrDefault(e => string.Equals(e.Name.LocalName, localName, StringComparison.OrdinalIgnoreCase));
            return el?.Value?.Trim();
        }

        var outputType = GetFirstPropertyValue("OutputType");
        var targetFramework = GetFirstPropertyValue("TargetFramework");
        var targetFrameworks = GetFirstPropertyValue("TargetFrameworks");
        var packageId = GetFirstPropertyValue("PackageId");
        var assemblyName = GetFirstPropertyValue("AssemblyName");
        var isPackable = GetFirstPropertyValue("IsPackable");

        var framework = !string.IsNullOrWhiteSpace(targetFramework)
            ? targetFramework
            : targetFrameworks ?? "";

        var name = !string.IsNullOrWhiteSpace(packageId)
            ? packageId
            : !string.IsNullOrWhiteSpace(assemblyName)
                ? assemblyName
                : Path.GetFileNameWithoutExtension(csprojPath) ?? "";

        var packageRefs = allElements
            .Where(e => string.Equals(e.Name.LocalName, "PackageReference", StringComparison.OrdinalIgnoreCase))
            .Select(pr =>
            {
                var include = pr.Attribute("Include")?.Value?.Trim();
                var version = pr.Attribute("Version")?.Value?.Trim()
                    ?? pr.Elements().FirstOrDefault(c => string.Equals(c.Name.LocalName, "Version", StringComparison.OrdinalIgnoreCase))?.Value?.Trim() ?? "";
                return (Include: include, Version: version);
            })
            .Where(t => !string.IsNullOrWhiteSpace(t.Include))
            .Select(t => new NuGetPackageReference { Name = t.Include!, Version = t.Version })
            .ToList();

        var projectType = ResolveProjectType(sdk, outputType, packageRefs, packageId, isPackable);
        // For Package type: use explicit PackageId from csproj if present, otherwise use project name
        var resultPackageId = projectType == ProjectType.Package
            ? (!string.IsNullOrWhiteSpace(packageId) ? packageId : name)
            : null;

        return new CsProjFileInfo
        {
            ProjectType = projectType,
            TargetFramework = framework,
            Name = name,
            PackageId = resultPackageId,
            PackageReferences = packageRefs
        };
    }

    private static ProjectType ResolveProjectType(
        string? sdk,
        string? outputType,
        IReadOnlyList<NuGetPackageReference> packageRefs,
        string? packageId,
        string? isPackable)
    {
        if (string.Equals(outputType, "Exe", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(outputType, "WinExe", StringComparison.OrdinalIgnoreCase))
            return ProjectType.Executable;

        var hasTestSdk = packageRefs.Any(pr =>
            string.Equals(pr.Name, "Microsoft.NET.Test.Sdk", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(pr.Name, "xunit", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(pr.Name, "NUnit", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(pr.Name, "MSTest.TestFramework", StringComparison.OrdinalIgnoreCase));

        if (hasTestSdk)
            return ProjectType.Test;

        if (!string.IsNullOrWhiteSpace(sdk) && sdk.StartsWith("Microsoft.NET.Sdk.Web", StringComparison.OrdinalIgnoreCase))
            return ProjectType.Service;

        // Library: if packable or has PackageId then it's a Package, else Library
        var isPackage = IsPackagedAsNuGet(packageId, isPackable);
        return isPackage ? ProjectType.Package : ProjectType.Library;
    }

    /// <summary>True when the project is packable or has PackageId (NuGet package intent).</summary>
    private static bool IsPackagedAsNuGet(string? packageId, string? isPackable)
    {
        if (!string.IsNullOrWhiteSpace(packageId))
            return true;
        if (string.Equals(isPackable, "true", StringComparison.OrdinalIgnoreCase))
            return true;
        return false;
    }
}
