using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace GrayMoon.Worker.Services;

/// <summary>
/// NuGet central package management: a project's <c>PackageReference</c> carries no version, and the version lives in
/// a <c>PackageVersion</c> item of the nearest <c>Directory.Packages.props</c> inside the repository.
/// </summary>
/// <remarks>
/// Only the nearest file is used (no parent import chain), and only an unconditional <c>PackageVersion</c> with a literal
/// <c>Version</c> counts. A value that contains <c>$(...)</c> is read as-is and never rewritten.
/// </remarks>
public static class CentralPackageVersions
{
    public const string FileName = "Directory.Packages.props";

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static readonly Regex PackageVersionElementRegex = new(
        @"<PackageVersion\b.*?(?:/>|</PackageVersion\s*>)",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex IncludeAttributeRegex = new(
        @"\b(?:Include|Update)\s*=\s*""([^""]*)""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex VersionAttributeRegex = new(
        @"(\bVersion\s*=\s*"")[^""]*("")",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Nearest <c>Directory.Packages.props</c> at or above the project directory, never above the repository root.</summary>
    public static string? FindPropsFile(string projectPath, string repositoryRoot)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryRoot));
        var dir = Path.GetDirectoryName(Path.GetFullPath(projectPath));
        while (!string.IsNullOrEmpty(dir) && IsInside(dir, root))
        {
            var candidate = Path.Combine(dir, FileName);
            if (File.Exists(candidate))
                return candidate;

            var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(dir));
            if (string.IsNullOrEmpty(parent) || parent.Equals(dir, PathComparison))
                break;
            dir = parent;
        }

        return null;
    }

    /// <summary>Package id to version for every unconditional <c>PackageVersion</c> in the file. Empty when the file is missing or unreadable.</summary>
    public static IReadOnlyDictionary<string, string> Read(string? propsPath)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(propsPath) || !File.Exists(propsPath))
            return result;

        XDocument doc;
        try
        {
            doc = XDocument.Load(propsPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            return result;
        }

        foreach (var group in doc.Root?.Elements() ?? [])
        {
            if (!group.Name.LocalName.Equals("ItemGroup", StringComparison.OrdinalIgnoreCase) || HasCondition(group))
                continue;
            foreach (var item in group.Elements())
            {
                if (!item.Name.LocalName.Equals("PackageVersion", StringComparison.OrdinalIgnoreCase) || HasCondition(item))
                    continue;
                var id = (item.Attribute("Include") ?? item.Attribute("Update"))?.Value?.Trim();
                var version = item.Attribute("Version")?.Value?.Trim()
                    ?? item.Elements().FirstOrDefault(c => c.Name.LocalName.Equals("Version", StringComparison.OrdinalIgnoreCase))?.Value?.Trim();
                if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(version))
                    result[id] = version;
            }
        }

        return result;
    }

    /// <summary>
    /// Rewrites the <c>Version</c> of each listed <c>PackageVersion</c> that already exists in the file, preserving every other byte.
    /// Returns true when the file changed.
    /// </summary>
    public static async Task<bool> UpdateAsync(string propsPath, IReadOnlyDictionary<string, string> packageIdToNewVersion, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(propsPath) || !File.Exists(propsPath) || packageIdToNewVersion.Count == 0)
            return false;

        var lookup = new Dictionary<string, string>(packageIdToNewVersion, StringComparer.OrdinalIgnoreCase);

        Encoding encoding;
        string content;
        try
        {
            using var reader = new StreamReader(propsPath, detectEncodingFromByteOrderMarks: true);
            content = await reader.ReadToEndAsync(cancellationToken);
            encoding = reader.CurrentEncoding;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        var modified = false;
        var newContent = PackageVersionElementRegex.Replace(content, match =>
        {
            var element = match.Value;
            var include = IncludeAttributeRegex.Match(element);
            if (!include.Success || !lookup.TryGetValue(include.Groups[1].Value.Trim(), out var newVersion))
                return element;
            if (!VersionAttributeRegex.IsMatch(element))
                return element;

            var updated = VersionAttributeRegex.Replace(element, m => m.Groups[1].Value + newVersion + m.Groups[2].Value);
            if (updated != element)
                modified = true;
            return updated;
        });

        if (modified)
            await File.WriteAllTextAsync(propsPath, newContent, encoding, cancellationToken);

        return modified;
    }

    private static bool HasCondition(XElement element) =>
        element.Attributes().Any(a => a.Name.LocalName.Equals("Condition", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(a.Value));

    private static bool IsInside(string fullPath, string root)
    {
        var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(fullPath));
        return path.Equals(root, PathComparison) || path.StartsWith(root + Path.DirectorySeparatorChar, PathComparison);
    }
}
