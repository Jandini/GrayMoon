using System.Text.RegularExpressions;

namespace GrayMoon.App.Tests.Conventions;

/// <summary>
/// A REST route is another way into the same operations as a page. It gets the same policy (read only and
/// tag-pinned repositories, Feature branch rules), the same Workspace lock and the same result type only when it
/// calls the operation facades (<c>IWorkspace*Operations</c>). A route that calls a handler, an orchestrator, the
/// git service or the Worker itself decides those things a second time, or not at all. This test reads the
/// endpoint sources and fails for a route that reaches past the facades.
/// </summary>
public sealed class ApiEndpointFacadeConventionTests
{
    /// <summary>What an endpoint must not use, and what it is.</summary>
    private static readonly (string Pattern, string What)[] Forbidden =
    [
        (@"\bWorkspaceGitService\b", "the git service behind the facades"),
        (@"\bWorkspace\w+Handler\b", "an orchestration handler behind the facades"),
        (@"\b\w+Orchestrator\b", "an orchestrator behind the facades"),
        (@"\bWorkspacePushService\b", "the push service behind the facades"),
        (@"\bIGitChangesWorkerClient\b", "the Git Changes Worker client"),
        (@"\bWorkspaceRepositoryStateWriter\b", "the per-context state writer"),
        (@"\.SendCommandAsync\s*\(", "a Worker command"),
    ];

    /// <summary>"file: identifier" -> why that endpoint may use it.</summary>
    private static readonly Dictionary<string, string> Allowed = new(StringComparer.Ordinal);

    [Fact]
    public void Endpoints_reach_operations_only_through_the_operation_facades()
    {
        var violations = new List<string>();
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in EndpointFiles())
        {
            var source = ConventionSource.WithoutLineComments(File.ReadAllText(file));
            foreach (var (pattern, what) in Forbidden)
            {
                foreach (Match match in Regex.Matches(source, pattern))
                {
                    var key = $"{ConventionSource.Relative(file)}: {match.Value.Trim('.', '(', ' ')}";
                    if (Allowed.ContainsKey(key))
                        used.Add(key);
                    else
                        violations.Add($"{ConventionSource.Relative(file)}({ConventionSource.LineOf(source, match.Index)}): {match.Value} is {what}. Call an IWorkspace*Operations facade instead.");
                }
            }
        }

        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
        Assert.Empty(Allowed.Keys.Except(used));
    }

    [Fact]
    public void Every_route_that_changes_a_Workspace_runs_under_the_Workspace_lock_and_a_facade()
    {
        // The Workspace command routes: each handler is one RunExclusiveAsync around one facade call.
        var file = EndpointFiles().Single(f => f.EndsWith("WorkspaceOperationsEndpoints.cs", StringComparison.Ordinal));
        var source = ConventionSource.WithoutLineComments(File.ReadAllText(file));
        var posts = Regex.Matches(source, @"group\.MapPost\(""(?<route>[^""]+)"",\s*(?<handler>\w+)\)").Select(m => (Route: m.Groups["route"].Value, Handler: m.Groups["handler"].Value)).ToList();
        Assert.NotEmpty(posts);

        foreach (var (route, handler) in posts)
        {
            var declaration = Regex.Match(source, @"(?:private|internal) static (?:async )?Task<IResult> " + handler + @"\s*\(");
            Assert.True(declaration.Success, $"Handler {handler} of {route} was not found.");
            var next = Regex.Match(source[(declaration.Index + 1)..], @"(?:private|internal) static ");
            var body = next.Success ? source[declaration.Index..(declaration.Index + 1 + next.Index)] : source[declaration.Index..];

            Assert.True(
                Regex.IsMatch(body, @"\bIWorkspace\w+Operations\b"),
                $"{route} ({handler}) takes no IWorkspace*Operations facade.");
            Assert.True(
                body.Contains("WorkspaceCommandHttp.Run", StringComparison.Ordinal),
                $"{route} ({handler}) does not run through WorkspaceCommandHttp, which takes the Workspace lock and maps the result.");
        }
    }

    private static List<string> EndpointFiles()
        => ConventionSource.Files(Path.Combine(ConventionSource.AppDirectory, "Api", "Endpoints"), ".cs").ToList();
}
