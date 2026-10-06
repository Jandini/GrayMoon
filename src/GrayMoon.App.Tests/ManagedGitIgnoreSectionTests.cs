using GrayMoon.App.Services.WorkspaceManifest;

namespace GrayMoon.App.Tests;

public sealed class ManagedGitIgnoreSectionTests
{
    private const string Section = "# <graymoon-repositories>\n/Avr.Api/\n/Avr.Web/\n# </graymoon-repositories>\n";

    [Fact]
    public void Appends_section_to_empty_file()
    {
        var result = ManagedGitIgnoreSection.Apply(null, ["Avr.Web", "Avr.Api"]);

        Assert.Equal(Section, result);
        Assert.Equal(Section, ManagedGitIgnoreSection.Apply(string.Empty, ["Avr.Web", "Avr.Api"]));
    }

    [Fact]
    public void Appends_section_after_blank_line_to_non_empty_file()
    {
        Assert.Equal("bin/\n\n" + Section, ManagedGitIgnoreSection.Apply("bin/\n", ["Avr.Api", "Avr.Web"]));
        Assert.Equal("bin/\n\n" + Section, ManagedGitIgnoreSection.Apply("bin/", ["Avr.Api", "Avr.Web"]));
        Assert.Equal("bin/\n\n" + Section, ManagedGitIgnoreSection.Apply("bin/\n\n", ["Avr.Api", "Avr.Web"]));
    }

    [Fact]
    public void Replaces_existing_section_and_preserves_outside_lines()
    {
        var existing = "bin/\r\nobj/\r\n\r\n# <graymoon-repositories>\n/Old/\n# </graymoon-repositories>\r\n\r\n*.user\r\n";

        var result = ManagedGitIgnoreSection.Apply(existing, ["Avr.Api", "Avr.Web"]);

        Assert.Equal(
            "bin/\r\nobj/\r\n\r\n# <graymoon-repositories>\n/Avr.Api/\n/Avr.Web/\n# </graymoon-repositories>\r\n\r\n*.user\r\n",
            result);
    }

    [Fact]
    public void Sorts_names_case_insensitively()
    {
        var result = ManagedGitIgnoreSection.Apply(null, ["zeta", "Beta", "alpha", "Gamma"]);

        Assert.Equal(
            "# <graymoon-repositories>\n/alpha/\n/Beta/\n/Gamma/\n/zeta/\n# </graymoon-repositories>\n",
            result);
    }

    [Fact]
    public void Output_uses_lf()
    {
        Assert.DoesNotContain('\r', ManagedGitIgnoreSection.Apply(null, ["Avr.Api"]));
        Assert.DoesNotContain('\r', ManagedGitIgnoreSection.Apply("bin/\n", ["Avr.Api"]));
    }

    [Fact]
    public void Idempotent_second_apply_is_identical()
    {
        var first = ManagedGitIgnoreSection.Apply("bin/\nobj/\n", ["Avr.Web", "Avr.Api"]);
        var second = ManagedGitIgnoreSection.Apply(first, ["Avr.Web", "Avr.Api"]);

        Assert.Equal(first, second);
    }
}
