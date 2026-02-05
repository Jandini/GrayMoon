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
            return Task.FromResult<CsProjFileInfo?>(result);
        }
        catch
        {
            return Task.FromResult<CsProjFileInfo?>(null);
        }
    }

    private static CsProjFileInfo? ParseCsProjDocument(XDocument doc, string csprojPath)
    {
        var root = doc.Root;
        if (root == null)
            return null;

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

        var projectType = ResolveProjectType(outputType, packageRefs);

        return new CsProjFileInfo
        {
            ProjectType = projectType,
            TargetFramework = framework,
            Name = name,
            PackageReferences = packageRefs
        };
    }

    private static ProjectType ResolveProjectType(string? outputType, IReadOnlyList<NuGetPackageReference> packageRefs)
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

        return ProjectType.Package;
    }
}
