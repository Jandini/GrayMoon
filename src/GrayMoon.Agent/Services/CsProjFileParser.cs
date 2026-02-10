using System.Xml.Linq;
using GrayMoon.Agent.Abstractions;
using GrayMoon.Agent.Models;

namespace GrayMoon.Agent.Services;

public sealed class CsProjFileParser : ICsProjFileParser
{
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

        XDocument doc;
        try
        {
            doc = XDocument.Load(csprojPath, LoadOptions.PreserveWhitespace);
        }
        catch
        {
            return false;
        }

        // Never emit an XML declaration when writing .csproj files; keep them lean and diff-friendly.
        // This also prevents adding '<?xml version="1.0" encoding="utf-8\"?>' to projects that didn't have it.
        doc.Declaration = null;

        var root = doc.Root;
        if (root == null)
            return false;

        var modified = false;
        foreach (var pr in root.Descendants().Where(e => string.Equals(e.Name.LocalName, "PackageReference", StringComparison.OrdinalIgnoreCase)))
        {
            var include = pr.Attribute("Include")?.Value?.Trim();
            if (string.IsNullOrEmpty(include) || !lookup.TryGetValue(include, out var newVersion) || newVersion == null)
                continue;

            var versionAttr = pr.Attribute("Version");
            var versionEl = pr.Elements().FirstOrDefault(e => string.Equals(e.Name.LocalName, "Version", StringComparison.OrdinalIgnoreCase));

            if (versionAttr != null)
            {
                if (versionAttr.Value != newVersion)
                {
                    versionAttr.Value = newVersion;
                    modified = true;
                }
            }
            else if (versionEl != null)
            {
                if (versionEl.Value != newVersion)
                {
                    versionEl.Value = newVersion;
                    modified = true;
                }
            }
            else
            {
                pr.Add(new XAttribute("Version", newVersion));
                modified = true;
            }
        }

        if (modified)
        {
            doc.Save(csprojPath, SaveOptions.None);
        }

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
