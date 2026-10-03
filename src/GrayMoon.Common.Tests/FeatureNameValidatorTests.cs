using GrayMoon.Common.Features;

namespace GrayMoon.Common.Tests;

public class FeatureNameValidatorTests
{
    public static IEnumerable<object[]> ValidNames()
    {
        yield return ["feature/login"];
        yield return ["fix-123"];
        yield return ["user@host"];
        yield return ["my-feature"];
        yield return ["feature_123"];
        yield return ["a"];
        yield return ["feature/sub/deep"];
        yield return ["release-1.2.3"];
        yield return ["ABC"];
        yield return ["CONFIG"];
        yield return ["CONTACT"];
        yield return ["NULLABLE"];
        yield return [new string('a', 100)];
    }

    public static IEnumerable<object[]> InvalidNames()
    {
        yield return ["", "empty"];
        yield return ["   ", "whitespace only"];
        yield return [new string('a', 101), "too long"];
        yield return ["has space", "space"];
        yield return ["has\ttab", "control character"];
        yield return ["@", "bare @"];
        yield return ["feature@{1}", "at-brace"];
        yield return ["feature..bad", "double dot"];
        yield return ["feature//bad", "double slash"];
        yield return ["-feature", "leading hyphen"];
        yield return ["feature/", "trailing slash"];
        yield return ["feature.", "trailing dot"];
        yield return ["fea~ture", "tilde"];
        yield return ["fea^ture", "caret"];
        yield return ["fea:ture", "colon"];
        yield return ["fea?ture", "question mark"];
        yield return ["fea*ture", "asterisk"];
        yield return ["fea[ture", "bracket"];
        yield return ["fea\\ture", "backslash"];
        yield return ["fea<ture", "less than"];
        yield return ["fea>ture", "greater than"];
        yield return ["fea\"ture", "double quote"];
        yield return ["fea|ture", "pipe"];
        yield return ["fea&ture", "ampersand"];
        yield return ["fea;ture", "semicolon"];
        yield return ["fea%ture", "percent"];
        yield return ["fea!ture", "bang"];
        yield return ["fea$ture", "dollar"];
        yield return ["fea`ture", "backtick"];
        yield return ["feature/.hidden", "segment starts with dot"];
        yield return ["feature/sub.lock", "segment ends with .lock"];
        yield return ["feature/sub ", "segment ends with space"];
        yield return ["feature/sub.", "segment ends with dot"];
        yield return ["CON", "reserved Windows name CON"];
        yield return ["con", "reserved Windows name con (case-insensitive)"];
        yield return ["NUL.txt", "reserved Windows name NUL with extension"];
        yield return ["COM1", "reserved Windows name COM1"];
        yield return ["LPT9", "reserved Windows name LPT9"];
        yield return ["feature//LPT1", "reserved name in later segment via empty check"];
    }

    [Theory]
    [MemberData(nameof(ValidNames))]
    public void Accepts_valid_names(string name)
    {
        Assert.Null(FeatureNameValidator.Validate(name));
    }

    [Theory]
    [MemberData(nameof(InvalidNames))]
    public void Rejects_invalid_names(string name, string reason)
    {
        var result = FeatureNameValidator.Validate(name);
        Assert.True(result is not null, $"Expected '{name}' to be rejected ({reason}) but it was accepted.");
    }

    [Fact]
    public void Rejection_reason_is_human_readable()
    {
        var result = FeatureNameValidator.Validate("has space");
        Assert.Contains("space", result, StringComparison.OrdinalIgnoreCase);
    }
}
