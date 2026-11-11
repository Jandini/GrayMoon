using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace GrayMoon.App.Tests;

/// <summary>
/// A string component parameter given <c>Message="_field"</c> (no <c>@</c>) renders the literal text "_field" in the
/// dialog. Text-like parameters must use <c>@</c>.
/// </summary>
public sealed partial class RazorStringParameterLiteralTests
{
    [GeneratedRegex(@"(?<![\w@:-])(?<name>[A-Z]\w*(Message|Name|Title|Text|Label|Caption|Note|Heading|Url))=""(?<value>_[A-Za-z]\w*(\.\w+)*)""")]
    private static partial Regex UnprefixedField();

    [Fact]
    public void Text_like_component_parameters_do_not_pass_a_field_name_as_a_literal()
    {
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(ComponentsDirectory(), "*.razor", SearchOption.AllDirectories))
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                foreach (Match m in UnprefixedField().Matches(lines[i]))
                    offenders.Add($"{Path.GetFileName(file)}:{i + 1} {m.Value}");
            }
        }

        Assert.True(offenders.Count == 0, "Missing '@' before a field name:" + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    private static string ComponentsDirectory([CallerFilePath] string thisFile = "")
    {
        foreach (var origin in new[] { AppContext.BaseDirectory, Path.GetDirectoryName(thisFile)! })
        {
            var dir = new DirectoryInfo(origin);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "GrayMoon.slnx")))
                dir = dir.Parent;
            if (dir != null)
                return Path.Combine(dir.FullName, "src", "GrayMoon.App", "Components");
        }
        throw new InvalidOperationException("GrayMoon.slnx was not found above the test output or the test source.");
    }
}
