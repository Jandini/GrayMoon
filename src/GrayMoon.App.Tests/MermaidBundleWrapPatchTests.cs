using System.Text.RegularExpressions;

namespace GrayMoon.App.Tests;

/// <summary>
/// The vendored Mermaid bundle (<c>wwwroot/mermaid/mermaid.min.js</c>) carries one local patch. Mermaid
/// limits an HTML label to the wrapping width (200px) with <c>white-space: nowrap</c>, measures it, and
/// switches the label to its wrapping layout only when the measured width equals that limit. Upstream
/// compares with <c>===</c>; on a display with fractional scaling (for example 175%) Chromium reports
/// 200.00003, the switch never happens, and every long label is cut off at the limit instead of wrapping
/// and growing its box. The patch compares with a tolerance. Re-apply it when the bundle is replaced.
/// </summary>
public sealed class MermaidBundleWrapPatchTests
{
    private static string ReadBundle() =>
        File.ReadAllText(Path.Combine(
            Conventions.ConventionSource.AppDirectory, "wwwroot", "mermaid", "mermaid.min.js"));

    [Fact]
    public void Label_wrap_check_tolerates_fractional_display_scaling()
    {
        var bundle = ReadBundle();

        // The exact comparison that fails on fractional scaling must be gone.
        Assert.DoesNotMatch(
            new Regex(@"\.width===\w+&&\(\w+\.style\(""display"",""table""\)"),
            bundle);

        // The wrapping branch must still be there, guarded by the tolerant comparison. If a new Mermaid
        // version restructures this code, check long labels at 175% scaling and update this test.
        Assert.Matches(
            new Regex(@"Math\.abs\(\w+\.width-\w+\)<\.5&&\(\w+\.style\(""display"",""table""\),\w+\.style\(""white-space"",""break-spaces""\)"),
            bundle);
    }
}
