using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace GrayMoon.Worker.Services;

/// <summary>
/// Resolves effective <c>IsPackable</c> and <c>PackageId</c> for one project.
/// Layers, later replacing earlier: the nearest <c>Directory.Build.props</c> inside the repository
/// (and a parent file only when that nearest file imports it), then the project in document order,
/// then the nearest <c>Directory.Build.targets</c>.
/// </summary>
/// <remarks>
/// Not evaluated in this pass, and intentionally skipped:
/// conditional imports, wildcard imports, MSBuild property functions other than
/// <c>$([MSBuild]::GetPathOfFileAbove('Directory.Build.props', '$(MSBuildThisFileDirectory)../'))</c>
/// and the same form for <c>Directory.Build.targets</c>, <c>Choose</c>/<c>When</c>/<c>Otherwise</c>,
/// props and targets imported by the SDK or a NuGet package, a <c>Directory.Build.props</c> that lives
/// outside the repository root, and disabling the automatic import through an environment variable or a
/// global MSBuild property rather than the project file.
/// A property group or property that carries a Condition is skipped. A value that still contains <c>$(...)</c> does not change the effective value.
/// </remarks>
public static class ProjectPackagingResolver
{
    public const int MaxImportDepth = 16;

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    // The parent chain people write at the top of Directory.Build.props / Directory.Build.targets.
    private static readonly Regex ParentChainImport = new(
        @"^\$\(\s*\[MSBuild\]::GetPathOfFileAbove\(\s*'(?<file>Directory\.Build\.(?:props|targets))'\s*,\s*'\$\(MSBuildThisFileDirectory\)\.\./'\s*\)\s*\)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public readonly record struct PackagingProperties(string? PackageId, bool? IsPackable);

    public static PackagingProperties Resolve(string projectPath, string repositoryRoot)
    {
        var projectFull = Path.GetFullPath(projectPath);
        var rootFull = Path.GetFullPath(repositoryRoot);
        if (!IsInsideRepository(projectFull, rootFull))
            rootFull = Path.GetDirectoryName(projectFull) ?? rootFull;

        var (importProps, importTargets) = ReadAutomaticImportFlags(projectFull);
        var state = new PropertyState();
        var stack = new HashSet<string>(PathComparer);
        var projectDirectory = Path.GetDirectoryName(projectFull) ?? rootFull;

        if (importProps)
        {
            var props = FindNearest(projectDirectory, "Directory.Build.props", rootFull);
            if (props != null)
                ApplyFile(props, rootFull, state, stack, depth: 0);
        }

        ApplyFile(projectFull, rootFull, state, stack, depth: 0);

        if (importTargets)
        {
            var targets = FindNearest(projectDirectory, "Directory.Build.targets", rootFull);
            if (targets != null)
                ApplyFile(targets, rootFull, state, stack, depth: 0);
        }

        return new PackagingProperties(state.PackageId, state.IsPackable);
    }

    private static (bool ImportProps, bool ImportTargets) ReadAutomaticImportFlags(string projectPath)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Load(projectPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            return (true, true);
        }

        bool? importProps = null;
        bool? importTargets = null;
        if (doc.Root != null)
        {
            foreach (var group in doc.Root.Elements())
            {
                if (!NameIs(group, "PropertyGroup") || HasCondition(group))
                    continue;
                foreach (var child in group.Elements())
                {
                    if (HasCondition(child))
                        continue;
                    if (NameIs(child, "ImportDirectoryBuildProps") && TryParseBool(child.Value, out var propsFlag))
                        importProps = propsFlag;
                    else if (NameIs(child, "ImportDirectoryBuildTargets") && TryParseBool(child.Value, out var targetsFlag))
                        importTargets = targetsFlag;
                }
            }
        }

        // An explicit false in the project file skips that automatic file. Unset means import it.
        return (importProps != false, importTargets != false);
    }

    private static void ApplyFile(string path, string repositoryRoot, PropertyState state, HashSet<string> stack, int depth)
    {
        if (depth > MaxImportDepth)
            return;

        var full = Path.GetFullPath(path);
        if (!IsInsideRepository(full, repositoryRoot) || !File.Exists(full) || !stack.Add(full))
            return;

        try
        {
            XDocument doc;
            try
            {
                doc = XDocument.Load(full);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
            {
                return;
            }

            if (doc.Root == null)
                return;

            var directory = Path.GetDirectoryName(full) ?? repositoryRoot;
            foreach (var child in doc.Root.Elements())
            {
                if (NameIs(child, "Import"))
                    ApplyImport(child, directory, repositoryRoot, state, stack, depth);
                else if (NameIs(child, "PropertyGroup") && !HasCondition(child))
                    ApplyPropertyGroup(child, state);
            }
        }
        finally
        {
            stack.Remove(full);
        }
    }

    private static void ApplyImport(
        XElement import,
        string importingDirectory,
        string repositoryRoot,
        PropertyState state,
        HashSet<string> stack,
        int depth)
    {
        var project = AttributeValue(import, "Project")?.Trim();
        if (string.IsNullOrEmpty(project))
            return;

        var parentChain = ParentChainImport.Match(project);
        if (parentChain.Success)
        {
            // Search starts at the parent of this file: GetPathOfFileAbove is given $(MSBuildThisFileDirectory)../
            var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(importingDirectory)));
            var found = FindNearest(parent, parentChain.Groups["file"].Value, repositoryRoot);
            if (found != null)
                ApplyFile(found, repositoryRoot, state, stack, depth + 1);
            return;
        }

        // Literal relative imports only. Conditions, wildcards, properties, and rooted paths are left unimplemented.
        if (HasCondition(import) || project.Contains('$') || project.Contains('*') || project.Contains('?') || Path.IsPathRooted(project))
            return;

        var combined = Path.GetFullPath(Path.Combine(importingDirectory, project));
        if (!IsInsideRepository(combined, repositoryRoot) || !File.Exists(combined))
            return;

        ApplyFile(combined, repositoryRoot, state, stack, depth + 1);
    }

    private static void ApplyPropertyGroup(XElement group, PropertyState state)
    {
        foreach (var child in group.Elements())
        {
            if (HasCondition(child))
                continue;

            var raw = child.Value?.Trim();
            if (string.IsNullOrEmpty(raw) || raw.Contains("$("))
                continue;

            if (NameIs(child, "PackageId"))
                state.PackageId = raw;
            else if (NameIs(child, "IsPackable") && TryParseBool(raw, out var packable))
                state.IsPackable = packable;
        }
    }

    private static string? FindNearest(string? startDirectory, string fileName, string repositoryRoot)
    {
        if (string.IsNullOrEmpty(startDirectory))
            return null;

        var dir = Path.GetFullPath(startDirectory);
        while (IsInsideRepository(dir, repositoryRoot))
        {
            var candidate = Path.Combine(dir, fileName);
            if (File.Exists(candidate))
                return Path.GetFullPath(candidate);

            var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(dir));
            if (string.IsNullOrEmpty(parent) || parent.Equals(dir, PathComparison))
                break;
            dir = parent;
        }

        return null;
    }

    private static bool IsInsideRepository(string fullPath, string repositoryRoot)
    {
        var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(fullPath));
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryRoot));
        if (path.Equals(root, PathComparison))
            return true;
        return path.StartsWith(root + Path.DirectorySeparatorChar, PathComparison);
    }

    private static bool HasCondition(XElement element) =>
        element.Attributes().Any(a => a.Name.LocalName.Equals("Condition", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(a.Value));

    private static string? AttributeValue(XElement element, string localName) =>
        element.Attributes().FirstOrDefault(a => a.Name.LocalName.Equals(localName, StringComparison.OrdinalIgnoreCase))?.Value;

    private static bool NameIs(XElement element, string localName) =>
        element.Name.LocalName.Equals(localName, StringComparison.OrdinalIgnoreCase);

    private static bool TryParseBool(string? value, out bool result)
    {
        if (string.Equals(value?.Trim(), "true", StringComparison.OrdinalIgnoreCase))
        {
            result = true;
            return true;
        }

        if (string.Equals(value?.Trim(), "false", StringComparison.OrdinalIgnoreCase))
        {
            result = false;
            return true;
        }

        result = false;
        return false;
    }

    private sealed class PropertyState
    {
        public string? PackageId { get; set; }
        public bool? IsPackable { get; set; }
    }
}
