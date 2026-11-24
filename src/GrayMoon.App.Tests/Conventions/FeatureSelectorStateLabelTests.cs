using System.Text.RegularExpressions;

namespace GrayMoon.App.Tests.Conventions;

/// <summary>
/// The Feature selector shows a state label ("Removing...", "Removal incomplete") behind a Feature's name. As inline
/// text in one ellipsis box the label was the first thing a long name pushed out of sight (logs of 2026-11-24: the
/// user could not see that a removal was running or had failed). The name must be the part that shrinks.
/// </summary>
public sealed class FeatureSelectorStateLabelTests
{
    private static string Component(string extension) =>
        File.ReadAllText(Path.Combine(
            ConventionSource.AppDirectory, "Components", "Features", "WorkspaceFeatureSelector" + extension));

    private static string RuleBody(string css, string selector)
    {
        var match = Regex.Match(css, $@"(?:^|[}}\s]){Regex.Escape(selector)}\s*\{{(?<body>[^}}]*)\}}", RegexOptions.Multiline);
        Assert.True(match.Success, $"WorkspaceFeatureSelector.razor.css has no rule '{selector}'.");
        return match.Groups["body"].Value;
    }

    [Fact]
    public void Every_state_label_is_an_element_of_its_own_next_to_a_name_element()
    {
        // The markup only: the code block also builds a tooltip from the label.
        var razor = Component(".razor");
        razor = razor[..razor.IndexOf("@code", StringComparison.Ordinal)];

        // Each place that renders a FriendlyLabel: the button holds a name element and a state element.
        var labels = Regex.Matches(razor, @"FeatureSelectorPresentation\.FriendlyLabel\(");
        Assert.NotEmpty(labels);
        foreach (Match label in labels)
        {
            var buttonStart = razor.LastIndexOf("<button", label.Index, StringComparison.Ordinal);
            var buttonEnd = razor.IndexOf("</button>", label.Index, StringComparison.Ordinal);
            Assert.True(buttonStart >= 0 && buttonEnd > buttonStart, "A state label is rendered outside a button.");
            var button = razor[buttonStart..buttonEnd];
            var line = ConventionSource.LineOf(razor, label.Index);

            Assert.True(button.Contains("workspace-feature-selector__feature-item", StringComparison.Ordinal),
                $"Line {line}: the button with a state label needs the class workspace-feature-selector__feature-item.");
            Assert.True(Regex.IsMatch(button, @"<span class=""workspace-feature-selector__feature-name"">"),
                $"Line {line}: the Feature name next to a state label must be in a workspace-feature-selector__feature-name element.");
            Assert.True(Regex.IsMatch(button, @"<span class=""workspace-feature-selector__feature-state[ ""]"),
                $"Line {line}: the state label must be in a workspace-feature-selector__feature-state element.");
        }
    }

    [Fact]
    public void The_name_shrinks_and_the_state_label_keeps_its_width()
    {
        var css = Regex.Replace(Component(".razor.css"), @"/\*.*?\*/", " ", RegexOptions.Singleline);

        Assert.Matches(@"display\s*:\s*flex", RuleBody(css, ".workspace-feature-selector__feature-item"));

        var name = RuleBody(css, ".workspace-feature-selector__feature-name");
        Assert.Matches(@"min-width\s*:\s*0", name);
        Assert.Matches(@"overflow\s*:\s*hidden", name);
        Assert.Matches(@"text-overflow\s*:\s*ellipsis", name);

        Assert.Matches(@"flex\s*:\s*0 0 auto", RuleBody(css, ".workspace-feature-selector__feature-state"));
    }
}
