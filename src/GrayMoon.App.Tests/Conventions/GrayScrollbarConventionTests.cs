using System.Text.RegularExpressions;

namespace GrayMoon.App.Tests.Conventions;

/// <summary>
/// Scrollbars are gray and auto-width everywhere, never the browser's default white
/// (AGENTS.md, "Scrollbars are gray"). One inherited <c>scrollbar-color</c> on <c>html</c> in
/// <c>wwwroot/app.css</c> recolors the browser's own scrollbar in every scroll container, so a container added
/// later is gray without any CSS of its own. These tests keep that default in place and keep a stylesheet from
/// switching a container back to the default or to a light scrollbar.
/// </summary>
public sealed class GrayScrollbarConventionTests
{
    private static string AppCss =>
        WithoutComments(File.ReadAllText(Path.Combine(ConventionSource.AppDirectory, "wwwroot", "app.css")));

    private static string WithoutComments(string css) =>
        Regex.Replace(css, @"/\*.*?\*/", " ", RegexOptions.Singleline);

    private static IEnumerable<string> Stylesheets() =>
        ConventionSource.Files(Path.Combine(ConventionSource.AppDirectory, "Components"), ".razor.css")
            .Concat(ConventionSource.Files(Path.Combine(ConventionSource.AppDirectory, "wwwroot", "css"), ".css"))
            .Append(Path.Combine(ConventionSource.AppDirectory, "wwwroot", "app.css"));

    /// <summary>The declarations of the rule whose whole selector is <paramref name="selector"/>, or null.</summary>
    private static string? RuleBody(string css, string selector)
    {
        var match = Regex.Match(css, $@"(?:^|[}}\s]){Regex.Escape(selector)}\s*\{{(?<body>[^}}]*)\}}", RegexOptions.Multiline);
        return match.Success ? match.Groups["body"].Value : null;
    }

    [Fact]
    public void The_global_stylesheet_makes_every_scrollbar_gray_by_default()
    {
        var html = RuleBody(AppCss, "html");

        // Inherited, so every scroll container gets it; the thumb is the first color.
        Assert.NotNull(html);
        Assert.Matches(@"scrollbar-color\s*:\s*#5a5a5a\s", html);
    }

    [Fact]
    public void The_default_keeps_the_browsers_own_scrollbar_with_its_width_and_arrows()
    {
        var css = AppCss;

        // A bare ::-webkit-scrollbar rule would replace every scrollbar with a drawn one that has no arrow buttons.
        Assert.DoesNotMatch(@"(?:^|[}\s,])::-webkit-scrollbar", css);
        Assert.DoesNotMatch(@"scrollbar-width\s*:\s*(thin|none)", RuleBody(css, "html")!);
    }

    [Fact]
    public void No_stylesheet_gives_a_scrollbar_a_light_color()
    {
        var light = new Regex(@"(?:#fff(?:fff)?\b|#f[0-9a-f]f[0-9a-f]f[0-9a-f]\b|\bwhite\b)", RegexOptions.IgnoreCase);
        var offenders = new List<string>();

        foreach (var file in Stylesheets())
        {
            var css = WithoutComments(File.ReadAllText(file));

            foreach (Match declaration in Regex.Matches(css, @"scrollbar-color\s*:\s*(?<value>[^;}]+)"))
            {
                var value = declaration.Groups["value"].Value.Trim();
                var thumb = value.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
                if (light.IsMatch(thumb))
                    offenders.Add($"{ConventionSource.Relative(file)}: scrollbar-color: {value}");
            }

            foreach (Match rule in Regex.Matches(css, @"::-webkit-scrollbar-thumb[^{]*\{(?<body>[^}]*)\}"))
            {
                var background = Regex.Match(rule.Groups["body"].Value, @"background(?:-color)?\s*:\s*(?<value>[^;}]+)");
                if (background.Success && light.IsMatch(background.Groups["value"].Value))
                    offenders.Add($"{ConventionSource.Relative(file)}: scrollbar thumb {background.Groups["value"].Value.Trim()}");
            }
        }

        Assert.True(offenders.Count == 0,
            "Scrollbars are gray, never a light color (AGENTS.md, \"Scrollbars are gray\"):"
            + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void Only_a_container_that_draws_its_own_gray_scrollbar_may_reset_the_color_to_auto()
    {
        var offenders = new List<string>();

        foreach (var file in Stylesheets())
        {
            var css = WithoutComments(File.ReadAllText(file));

            foreach (Match rule in Regex.Matches(css, @"(?<selector>[^{}]+)\{(?<body>[^{}]*scrollbar-color\s*:\s*auto\b[^{}]*)\}"))
            {
                // "auto" is the browser default (white on Windows) unless the same container has its own thumb rule.
                var selectors = rule.Groups["selector"].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var drawsItsOwn = selectors.All(selector =>
                    Regex.IsMatch(css, $@"{Regex.Escape(selector)}::-webkit-scrollbar-thumb\s*\{{"));
                if (!drawsItsOwn)
                    offenders.Add($"{ConventionSource.Relative(file)}: {string.Join(", ", selectors)}");
            }
        }

        Assert.True(offenders.Count == 0,
            "scrollbar-color: auto brings back the default white scrollbar unless the container has its own "
            + "::-webkit-scrollbar-thumb rule (AGENTS.md, \"Scrollbars are gray\"):"
            + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }
}
