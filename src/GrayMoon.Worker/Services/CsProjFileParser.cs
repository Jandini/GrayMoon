using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using GrayMoon.Worker.Abstractions;
using GrayMoon.Worker.Models;

namespace GrayMoon.Worker.Services;

public sealed class CsProjFileParser : ICsProjFileParser
{
    // Matches a complete PackageReference element - self-closing (<PackageReference ... />) or with child
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

    public Task<CsProjFileInfo?> ParseAsync(string csprojPath, CancellationToken cancellationToken = default, string? repositoryRoot = null)
    {
        if (string.IsNullOrWhiteSpace(csprojPath) || !File.Exists(csprojPath))
            return Task.FromResult<CsProjFileInfo?>(null);

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var doc = XDocument.Load(csprojPath);
            var root = ResolveRepositoryRoot(csprojPath, repositoryRoot);
            var packaging = ProjectPackagingResolver.Resolve(csprojPath, root);
            var result = ParseCsProjDocument(doc, csprojPath, packaging);
            return Task.FromResult(result);
        }
        catch
        {
            return Task.FromResult<CsProjFileInfo?>(null);
        }
    }

    /// <summary>The directory props and targets must not leave. An explicit root wins. Otherwise the nearest parent that contains a .git entry, or the project directory.</summary>
    private static string ResolveRepositoryRoot(string csprojPath, string? repositoryRoot)
    {
        if (!string.IsNullOrWhiteSpace(repositoryRoot))
            return Path.GetFullPath(repositoryRoot);

        var dir = Path.GetDirectoryName(Path.GetFullPath(csprojPath));
        var current = dir;
        while (!string.IsNullOrEmpty(current))
        {
            var git = Path.Combine(current, ".git");
            if (Directory.Exists(git) || File.Exists(git))
                return current;

            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || parent == current)
                break;
            current = parent;
        }

        return dir ?? Path.GetFullPath(csprojPath);
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

    public async Task<PackageReferenceMatch?> FindPackageReferenceForVersionPatternAsync(string csprojPath, string prefix, string suffix, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(csprojPath) || !File.Exists(csprojPath) || string.IsNullOrEmpty(prefix))
            return null;

        cancellationToken.ThrowIfCancellationRequested();

        string content;
        try
        {
            content = await File.ReadAllTextAsync(csprojPath, cancellationToken);
        }
        catch
        {
            return null;
        }

        var patternRegex = new Regex(Regex.Escape(prefix) + "(.*?)" + Regex.Escape(suffix ?? ""), RegexOptions.Singleline);

        foreach (Match elementMatch in PackageReferenceElementRegex.Matches(content))
        {
            var elementText = elementMatch.Value;
            var versionMatch = patternRegex.Match(elementText);
            if (!versionMatch.Success)
                continue;

            var includeMatch = IncludeAttributeRegex.Match(elementText);
            if (includeMatch.Success)
                return new PackageReferenceMatch(includeMatch.Groups[1].Value.Trim(), versionMatch.Groups[1].Value.Trim());
        }

        return null;
    }

    private static CsProjFileInfo? ParseCsProjDocument(XDocument doc, string csprojPath, ProjectPackagingResolver.PackagingProperties packaging)
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
        // Name stays tied to the project file itself so a cleared package id does not rename the row.
        var localPackageId = GetFirstPropertyValue("PackageId");
        var assemblyName = GetFirstPropertyValue("AssemblyName");

        var framework = !string.IsNullOrWhiteSpace(targetFramework)
            ? targetFramework
            : targetFrameworks ?? "";

        var name = !string.IsNullOrWhiteSpace(localPackageId)
            ? localPackageId
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

        var projectType = ResolveProjectType(sdk, outputType, packageRefs, packaging.PackageId, packaging.IsPackable);
        // For Package type: use the effective PackageId when present, otherwise the project name.
        var resultPackageId = projectType == ProjectType.Package
            ? (!string.IsNullOrWhiteSpace(packaging.PackageId) ? packaging.PackageId : name)
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
        bool? isPackable)
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

        var isPackage = IsPackagedAsNuGet(packageId, isPackable);
        return isPackage ? ProjectType.Package : ProjectType.Library;
    }

    /// <summary>Explicit IsPackable false is a library. Otherwise true, or an unset value with a PackageId, is a package.</summary>
    private static bool IsPackagedAsNuGet(string? packageId, bool? isPackable)
    {
        if (isPackable == false)
            return false;
        if (isPackable == true)
            return true;
        return !string.IsNullOrWhiteSpace(packageId);
    }
}
