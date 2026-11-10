using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using GrayMoon.Worker.Jobs.Requests;

namespace GrayMoon.Worker.Tests.Conventions;

/// <summary>
/// Guardrail 6 (review 2026-11-10, finding 5): every path the App sends in a request DTO is checked by
/// <c>GitRepositoryPathValidator</c> in the command that reads it. The DTO properties come from reflection,
/// so a new request with a path property is covered the day it is added.
/// </summary>
public sealed class RequestPathValidationConventionTests
{
    private const string Validator = "GitRepositoryPathValidator";

    /// <summary>"RequestType.Property" that the reading command does not validate itself, with the reason.</summary>
    private static readonly Dictionary<string, string> Exceptions = new(StringComparer.Ordinal)
    {
        // Validated one call further down, by the service every caller goes through.
        ["DiscardGitChangesRequest.Paths"] = "Validated in GitCliRepositoryGitChangesService before any git or file call.",
        ["StageGitChangesRequest.Paths"] = "Validated in GitCliRepositoryGitChangesService before any git call.",
        ["UnstageGitChangesRequest.Paths"] = "Validated in GitCliRepositoryGitChangesService before any git call.",
        ["GetGitFileDiffRequest.Path"] = "Validated in GitCliRepositoryGitChangesService before any git or file call.",
        ["StageAndCommitRequest.PathsToStage"] = "Validated in GitService.StageAndCommitAsync before staging; the command only looks for a props file above each project.",

        // Absolute machine paths, not paths inside a repository, so the repository validator does not apply.
        ["CreateGitWorktreeRequest.MainRepositoryPath"] = "Absolute path of the main clone; GitWorktreeService checks it.",
        ["CreateGitWorktreeRequest.WorktreePath"] = "Absolute path of the worktree to create; GitWorktreeService checks it.",
        ["InspectWorktreeRequest.MainRepositoryPath"] = "Absolute path of the main clone; read only.",
        ["InspectWorktreeRequest.WorktreePath"] = "Absolute path of a worktree; read only.",
        ["ListGitWorktreesRequest.MainRepositoryPath"] = "Absolute path of the main clone; read only.",
        ["RemoveGitWorktreeRequest.MainRepositoryPath"] = "Absolute path of the main clone; GitWorktreeService checks it.",
        ["RemoveGitWorktreeRequest.WorktreePath"] = "Absolute path of the worktree; GitWorktreeService checks the residue path before deleting.",
        ["RemoveGitWorktreeRequest.FeatureRootPath"] = "Absolute Feature root; GitWorktreeService checks the residue path before deleting.",
        ["CleanupFeatureFolderRequest.FeatureRootPath"] = "Absolute Feature root; FeatureFolderCleaner has its own containment checks.",
        ["ValidatePathRequest.Path"] = "The command exists to test an arbitrary absolute path the user typed.",

        // Known open violations, recorded so they cannot spread. Remove the entry with the fix.
        ["ResolveGeneratedPackageReferencesItem.FilePath"] = "OPEN: ResolveGeneratedPackageReferencesCommand combines it with the repository path unvalidated (read only).",
        ["SyncRepositoryDependenciesProjectUpdate.ProjectPath"] = "OPEN: reaches CsProjFileService.UpdatePackageVersionsAsync unvalidated, and that method writes the file.",
    };

    [Fact]
    public void Every_request_path_is_validated_by_the_command_that_reads_it()
    {
        var commands = CommandSources();
        var properties = PathProperties(typeof(ValidatePathRequest).Assembly).ToList();
        Assert.True(properties.Count > 15, "The scan found almost no path properties; it is broken.");

        var found = properties
            .Select(p => (Key: $"{p.Type.Name}.{p.Property}", Problem: FindProblem(commands, RequestTypeNames(p.Type), p.Property)))
            .Where(p => p.Problem != null)
            .ToList();

        var violations = found.Where(p => !Exceptions.ContainsKey(p.Key)).Select(p => $"{p.Key}: {p.Problem}").ToList();
        Assert.True(violations.Count == 0,
            "A path from the App must go through GitRepositoryPathValidator in the command that reads it (GrayMoon/AGENTS.md, Worker: input, processes and git):"
            + Environment.NewLine + string.Join(Environment.NewLine, violations));

        var stale = Exceptions.Keys.Except(found.Select(p => p.Key)).ToList();
        Assert.True(stale.Count == 0, "Exceptions that no longer apply; remove them from the list: " + string.Join(", ", stale));
    }

    [Fact]
    public void Scan_reports_a_command_that_reads_a_path_without_validating_it()
    {
        const string unvalidated = """
            public sealed class ReadThingCommand
            {
                public Task<string> ExecuteAsync(ReadThingRequest request) =>
                    File.ReadAllTextAsync(Path.Combine(repoPath, request.FilePath!));
            }
            """;
        const string validated = """
            public sealed class ReadThingCommand
            {
                public Task<string> ExecuteAsync(ReadThingRequest request)
                {
                    var path = GitRepositoryPathValidator.Validate(repoPath, request.FilePath);
                    return File.ReadAllTextAsync(path.FullPath);
                }
            }
            """;
        string[] types = ["ReadThingRequest"];

        Assert.Equal("ReadThingCommand.cs reads it and never calls GitRepositoryPathValidator",
            FindProblem([("ReadThingCommand.cs", unvalidated)], types, "FilePath"));
        Assert.Null(FindProblem([("ReadThingCommand.cs", validated)], types, "FilePath"));
        // A path nobody reads in a command is reported too: it is validated nowhere the scan can see.
        Assert.Equal("no command reads it", FindProblem([("OtherCommand.cs", validated.Replace("ReadThingRequest", "OtherRequest"))], types, "FilePath"));
    }

    [Fact]
    public void Path_properties_are_found_on_requests_and_their_items()
    {
        var keys = PathProperties(typeof(ValidatePathRequest).Assembly).Select(p => $"{p.Type.Name}.{p.Property}").ToList();

        Assert.Contains("GetFileContentsRequest.FilePath", keys);
        Assert.Contains("DiscardGitChangesRequest.Paths", keys);
        Assert.Contains("CheckFileVersionsItem.FilePath", keys);
    }

    /// <summary>
    /// Null when every command that reads <paramref name="property"/> of one of <paramref name="requestTypes"/>
    /// also calls the validator; otherwise what is wrong.
    /// </summary>
    internal static string? FindProblem(IReadOnlyList<(string File, string Text)> commands, IReadOnlyList<string> requestTypes, string property)
    {
        var readers = commands
            .Where(c => requestTypes.Any(t => Regex.IsMatch(c.Text, $@"\b{Regex.Escape(t)}\b")))
            .Where(c => Regex.IsMatch(c.Text, $@"\.{Regex.Escape(property)}\b"))
            .ToList();
        if (readers.Count == 0)
            return "no command reads it";

        var unvalidated = readers.Where(c => !c.Text.Contains(Validator, StringComparison.Ordinal)).Select(c => c.File).ToList();
        return unvalidated.Count == 0
            ? null
            : $"{string.Join(", ", unvalidated)} reads it and never calls {Validator}";
    }

    /// <summary>String or string-list properties of the request DTOs whose name says they hold a path.</summary>
    private static IEnumerable<(Type Type, string Property)> PathProperties(Assembly worker) =>
        from type in worker.GetTypes()
        where type.Namespace == typeof(ValidatePathRequest).Namespace && type.IsClass
        from property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
        where Regex.IsMatch(property.Name, "Paths?($|[A-Z])") && IsStringOrStrings(property.PropertyType)
        orderby type.Name, property.Name
        select (type, property.Name);

    private static bool IsStringOrStrings(Type type) =>
        type == typeof(string)
        || (typeof(IEnumerable).IsAssignableFrom(type) && ElementType(type) == typeof(string));

    private static Type? ElementType(Type type) =>
        type.IsArray
            ? type.GetElementType()
            : type.GetInterfaces().Append(type)
                .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>))?
                .GetGenericArguments()[0];

    /// <summary>The DTO's own name plus the requests that carry it as an item, since a command names the request type.</summary>
    private static IReadOnlyList<string> RequestTypeNames(Type dto) =>
        dto.Assembly.GetTypes()
            .Where(t => t.Namespace == dto.Namespace)
            .Where(t => t.GetProperties().Any(p => p.PropertyType == dto || ElementType(p.PropertyType) == dto))
            .Select(t => t.Name)
            .Prepend(dto.Name)
            .ToList();

    private static IReadOnlyList<(string File, string Text)> CommandSources([CallerFilePath] string thisFile = "")
    {
        // The build output can live outside the repository (--artifacts-path), so fall back to this source file.
        foreach (var origin in new[] { AppContext.BaseDirectory, Path.GetDirectoryName(thisFile)! })
        {
            var dir = new DirectoryInfo(origin);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "GrayMoon.slnx")))
                dir = dir.Parent;
            if (dir == null)
                continue;

            var commands = Path.Combine(dir.FullName, "src", "GrayMoon.Worker", "Commands");
            Assert.True(Directory.Exists(commands), $"Expected Worker commands at '{commands}'.");
            return Directory.EnumerateFiles(commands, "*.cs", SearchOption.AllDirectories)
                .Select(f => (Path.GetFileName(f), File.ReadAllText(f)))
                .ToList();
        }

        throw new InvalidOperationException("GrayMoon.slnx was not found above the test output or the test source.");
    }
}
