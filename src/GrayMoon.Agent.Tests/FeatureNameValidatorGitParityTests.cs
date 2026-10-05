using GrayMoon.Common.Features;

namespace GrayMoon.Agent.Tests;

/// <summary>
/// E1 Git parity: every name <see cref="FeatureNameValidator"/> accepts must also be accepted by real
/// <c>git check-ref-format --branch</c>. The validator may be stricter than Git, never looser.
/// </summary>
public sealed class FeatureNameValidatorGitParityTests
{
    public static IEnumerable<object[]> AllTableNames()
    {
        foreach (var name in FeatureNameValidatorTestNames.All)
            yield return [name];
    }

    [Theory]
    [MemberData(nameof(AllTableNames))]
    public void Accepted_names_also_pass_git_check_ref_format(string name)
    {
        var validationError = FeatureNameValidator.Validate(name);
        if (validationError is not null)
        {
            // Not accepted by our validator: parity only needs to hold for names it accepts.
            return;
        }

        using var fixture = new TempGitRepositoryFixture();
        var (exitCode, _, stderr) = fixture.RunGit("check-ref-format", "--branch", name);

        Assert.True(exitCode == 0, $"Expected git check-ref-format to accept '{name}' but it did not: {stderr}");
    }
}

/// <summary>Shared name table (same cases as <c>FeatureNameValidatorTests</c> in GrayMoon.Common.Tests).</summary>
internal static class FeatureNameValidatorTestNames
{
    public static readonly string[] All =
    [
        "feature/login",
        "fix-123",
        "user@host",
        "my-feature",
        "feature_123",
        "a",
        "feature/sub/deep",
        "release-1.2.3",
        "ABC",
        "CONFIG",
        "CONTACT",
        "NULLABLE",
        new string('a', 100),

        "",
        "   ",
        new string('a', 101),
        "has space",
        "has\ttab",
        "@",
        "feature@{1}",
        "feature..bad",
        "feature//bad",
        "-feature",
        "feature/",
        "feature.",
        "fea~ture",
        "fea^ture",
        "fea:ture",
        "fea?ture",
        "fea*ture",
        "fea[ture",
        "fea\\ture",
        "fea<ture",
        "fea>ture",
        "fea\"ture",
        "fea|ture",
        "fea&ture",
        "fea;ture",
        "fea%ture",
        "fea!ture",
        "fea$ture",
        "fea`ture",
        "feature/.hidden",
        "feature/sub.lock",
        "feature/sub ",
        "feature/sub.",
        "CON",
        "con",
        "NUL.txt",
        "COM1",
        "LPT9",
        "feature//LPT1",
    ];
}
