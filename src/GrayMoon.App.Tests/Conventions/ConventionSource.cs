using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace GrayMoon.App.Tests.Conventions;

/// <summary>
/// Shared plumbing for the convention tests (review 2026-11-10, fix plan stream J): locating the source
/// tree and cutting source text into the pieces the scans look at. The scans are text based on purpose -
/// they need no compiler packages and they read .razor files as well as .cs files.
/// </summary>
internal static class ConventionSource
{
    /// <summary>The GrayMoon repository root: the folder that holds GrayMoon.slnx.</summary>
    public static string RepositoryRoot([CallerFilePath] string thisFile = "")
    {
        // The build output can live outside the repository (--artifacts-path), so fall back to this source file.
        foreach (var origin in new[] { AppContext.BaseDirectory, Path.GetDirectoryName(thisFile)! })
        {
            var dir = new DirectoryInfo(origin);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "GrayMoon.slnx")))
                dir = dir.Parent;
            if (dir != null)
                return dir.FullName;
        }

        throw new InvalidOperationException("GrayMoon.slnx was not found above the test output or the test source.");
    }

    public static string AppDirectory => Path.Combine(RepositoryRoot(), "src", "GrayMoon.App");

    /// <summary>Source files under <paramref name="directory"/> (recursive) with one of the extensions, build folders excluded.</summary>
    public static IEnumerable<string> Files(string directory, params string[] extensions)
    {
        Assert.True(Directory.Exists(directory), $"Expected source directory '{directory}'.");
        return Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Where(f => extensions.Any(e => f.EndsWith(e, StringComparison.OrdinalIgnoreCase)))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Path relative to the App project, with forward slashes, for messages and exception lists.</summary>
    public static string Relative(string file) => Path.GetRelativePath(AppDirectory, file).Replace('\\', '/');

    /// <summary>
    /// Blanks out <c>//</c> line comments (a URL such as <c>http://</c> is kept) so commented-out code is not scanned.
    /// Length and line breaks are preserved, so indexes and line numbers still match the original text.
    /// </summary>
    public static string WithoutLineComments(string source) =>
        Regex.Replace(source, @"(?<![:""'/\w])//[^\r\n]*", m => new string(' ', m.Length));

    public static int LineOf(string source, int index) => source.AsSpan(0, index).Count('\n') + 1;

    /// <summary>
    /// The text from the opening character at <paramref name="openIndex"/> to its matching closing character,
    /// both included. Returns the rest of the text when the block never closes.
    /// </summary>
    public static string Balanced(string source, int openIndex, char open, char close)
    {
        var depth = 0;
        for (var i = openIndex; i < source.Length; i++)
        {
            if (source[i] == open) depth++;
            else if (source[i] == close && --depth == 0)
                return source[openIndex..(i + 1)];
        }

        return source[openIndex..];
    }
}

/// <summary>A lambda passed to a call that runs it as a background job or inside a fresh DI scope.</summary>
internal sealed record JobLambda(string Call, int Line, string Body);

internal static class JobLambdaScanner
{
    /// <summary>
    /// Calls whose lambda argument runs off the dispatcher: the job service entry points, the page helpers
    /// that wrap them, and <c>ScopedExecutor.ExecuteAsync</c>.
    /// </summary>
    private static readonly Regex JobCall = new(
        @"\b(?<name>StartJob|StartWorkspaceJob|StartPushJob|ExecuteAsync)\s*(?:<[^;(){}]*?>)?\s*\(",
        RegexOptions.Compiled);

    private static readonly Regex DispatcherCall = new(@"\b(?:InvokeAsync|SafeInvoke|SafeInvokeAsync)\s*\(", RegexOptions.Compiled);

    /// <summary>Every lambda passed to a job call in <paramref name="source"/>. A call without a lambda (a declaration) is skipped.</summary>
    public static IReadOnlyList<JobLambda> Find(string source)
    {
        source = ConventionSource.WithoutLineComments(source);
        var result = new List<JobLambda>();
        foreach (Match call in JobCall.Matches(source))
        {
            var arguments = ConventionSource.Balanced(source, call.Index + call.Length - 1, '(', ')');
            var arrow = arguments.IndexOf("=>", StringComparison.Ordinal);
            if (arrow < 0)
                continue;
            result.Add(new JobLambda(call.Groups["name"].Value, ConventionSource.LineOf(source, call.Index), arguments[(arrow + 2)..]));
        }

        return result;
    }

    /// <summary>The lambda body with everything handed to the dispatcher (InvokeAsync / SafeInvoke) removed.</summary>
    public static string OffDispatcherPart(string lambdaBody)
    {
        while (true)
        {
            var call = DispatcherCall.Match(lambdaBody);
            if (!call.Success)
                return lambdaBody;
            var arguments = ConventionSource.Balanced(lambdaBody, call.Index + call.Length - 1, '(', ')');
            lambdaBody = string.Concat(lambdaBody.AsSpan(0, call.Index), "Dispatched", lambdaBody.AsSpan(call.Index + call.Length - 1 + arguments.Length));
        }
    }
}
