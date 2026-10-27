using System.Text.RegularExpressions;

namespace GrayMoon.App.Tests;

/// <summary>
/// Combined Update and Push must have one application-level entry point; no UI member may reconstruct it by calling the
/// update and the push itself. This scans the UI source (Components/**) member by member and compares the files that
/// still do so with <see cref="KnownViolations"/>. A file may host a standalone Update Only and a standalone Push Only as
/// long as no single member chains them. The list is a ratchet for the Push Updated unification: it only shrinks. A file
/// that is not listed and starts chaining fails the test; a listed file that was migrated also fails it, so the list
/// cannot go stale. The end state is an empty list (see docs/todo/push-updated-unified-entry-living-notes.md).
/// </summary>
public sealed class UpdateAndPushEntryPointArchitectureTests
{
    // Files under src/GrayMoon.App/Components, slash separated.
    private static readonly string[] KnownViolations = [];

    private static readonly string[] UpdateCalls = [".UpdateAsync(", "PrepareAsync("];
    private static readonly string[] PushCalls = ["PushAsync(", "ExecutePushCoreAsync(", "ExecutePushAsync("];

    // A class member starts at the 4-space indent in both Razor @code blocks and file-scoped partial classes.
    private static readonly Regex MemberStart = new(@"^    (?:private|public|protected|internal)\b", RegexOptions.Multiline);

    [Fact]
    public void Ui_members_that_chain_update_and_push_match_the_known_list()
    {
        var componentsDir = FindComponentsDirectory();
        var found = new List<string>();

        foreach (var file in Directory.EnumerateFiles(componentsDir, "*.*", SearchOption.AllDirectories)
                     .Where(f => f.EndsWith(".razor", StringComparison.Ordinal) || f.EndsWith(".cs", StringComparison.Ordinal)))
        {
            if (SplitMembers(File.ReadAllText(file)).Any(Chains))
                found.Add("Components/" + Path.GetRelativePath(componentsDir, file).Replace(Path.DirectorySeparatorChar, '/'));
        }

        Assert.Equal(
            KnownViolations.OrderBy(f => f, StringComparer.Ordinal),
            found.OrderBy(f => f, StringComparer.Ordinal));
    }

    [Fact]
    public void Chain_detection_flags_a_member_that_updates_then_pushes_and_not_two_standalone_members()
    {
        const string chained = "    private async Task A()\n    {\n        await ops.UpdateAsync(x);\n        await pushOps.PushAsync(y);\n    }\n";
        const string standalone = "    private async Task A()\n    {\n        await ops.UpdateAsync(x);\n    }\n\n    private async Task B()\n    {\n        await pushOps.PushAsync(y);\n    }\n";
        const string direct = "    private async Task A()\n    {\n        await ops.UpdateAndPushAsync(x);\n    }\n";

        Assert.Contains(SplitMembers(chained), Chains);
        Assert.DoesNotContain(SplitMembers(standalone), Chains);
        Assert.Contains(SplitMembers(direct), Chains);
    }

    private static bool Chains(string member)
        => member.Contains(".UpdateAndPushAsync(", StringComparison.Ordinal)
            || (UpdateCalls.Any(c => member.Contains(c, StringComparison.Ordinal))
                && PushCalls.Any(c => member.Contains(c, StringComparison.Ordinal)));

    private static IEnumerable<string> SplitMembers(string text)
    {
        text = text.Replace("\r\n", "\n");
        var starts = MemberStart.Matches(text).Select(m => m.Index).ToList();
        if (starts.Count == 0)
        {
            yield return text;
            yield break;
        }

        for (var i = 0; i < starts.Count; i++)
            yield return text.Substring(starts[i], (i + 1 < starts.Count ? starts[i + 1] : text.Length) - starts[i]);
    }

    private static string FindComponentsDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GrayMoon.slnx")))
            dir = dir.Parent;

        if (dir is null)
            throw new InvalidOperationException("Could not locate GrayMoon.slnx above the test output directory.");

        var componentsDir = Path.Combine(dir.FullName, "src", "GrayMoon.App", "Components");
        if (!Directory.Exists(componentsDir))
            throw new InvalidOperationException($"Expected Components source directory at '{componentsDir}'.");

        return componentsDir;
    }
}
