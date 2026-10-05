using GrayMoon.App.Services.Features;

namespace GrayMoon.App.Tests;

/// <summary>
/// Pure tests for the Windows-vs-POSIX path join/inspect helper used everywhere a Feature's
/// Agent/Worker-host path is built. Style is inferred from the path text itself (no OS flag),
/// so these cover both shapes plus the mixed-separator inputs the Agent/git can hand back.
/// </summary>
public sealed class AgentPathTests
{
    [Theory]
    [InlineData("/home/dev", true)]
    [InlineData("/home/dev/.graymoon", true)]
    [InlineData(@"C:\Users\dev", false)]
    [InlineData(@"C:\Users\dev\.graymoon", false)]
    [InlineData("relative", false)]
    [InlineData("", false)]
    public void IsPosix_InfersFromLeadingSlash(string path, bool expected)
    {
        Assert.Equal(expected, AgentPath.IsPosix(path));
    }

    [Theory]
    [InlineData(@"C:\a\b\", @"C:\a\b")]
    [InlineData("C:/a/b/", @"C:\a\b")]
    [InlineData(@"C:\a/b\c", @"C:\a\b\c")]
    [InlineData("/home/dev/", "/home/dev")]
    [InlineData("/home/dev\\sub", "/home/dev/sub")]
    [InlineData("/", "/")]
    public void Normalize_KeepsOriginalStyle(string input, string expected)
    {
        Assert.Equal(expected, AgentPath.Normalize(input));
    }

    [Fact]
    public void Combine_WindowsRoot_JoinsWithBackslash()
    {
        var result = AgentPath.Combine(@"C:\Users\test\.graymoon", "test-ws", "features", "feat-1");
        Assert.Equal(@"C:\Users\test\.graymoon\test-ws\features\feat-1", result);
    }

    [Fact]
    public void Combine_PosixRoot_JoinsWithForwardSlash()
    {
        var result = AgentPath.Combine("/home/dev/.graymoon", "test-ws", "features", "feat-1");
        Assert.Equal("/home/dev/.graymoon/test-ws/features/feat-1", result);
    }

    [Fact]
    public void Combine_NormalizesMixedSeparatorsInSegments()
    {
        var result = AgentPath.Combine("C:/Users/test/.graymoon", "feat/1");
        Assert.Equal(@"C:\Users\test\.graymoon\feat\1", result);
    }

    [Fact]
    public void Combine_SkipsEmptyOrWhitespaceSegments()
    {
        var result = AgentPath.Combine(@"C:\root", "", "  ", "leaf");
        Assert.Equal(@"C:\root\leaf", result);
    }

    [Fact]
    public void Combine_NoNonEmptyParts_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, AgentPath.Combine("", null!, "   "));
    }

    [Theory]
    [InlineData(@"C:\a\b\c", "c")]
    [InlineData("C:/a/b/c/", "c")]
    [InlineData("/home/dev/repo", "repo")]
    [InlineData("/home/dev/repo/", "repo")]
    public void GetFileName_ReturnsLastSegment(string path, string expected)
    {
        Assert.Equal(expected, AgentPath.GetFileName(path));
    }

    [Theory]
    [InlineData(@"C:\a\b\c", @"C:\a\b")]
    [InlineData("/home/dev/repo", "/home/dev")]
    public void GetDirectoryName_ReturnsParent(string path, string expected)
    {
        Assert.Equal(expected, AgentPath.GetDirectoryName(path));
    }

    [Fact]
    public void GetDirectoryName_NoSeparator_ReturnsNull()
    {
        Assert.Null(AgentPath.GetDirectoryName("justaname"));
    }

    [Theory]
    [InlineData(@"C:\.graymoon", true)]
    [InlineData(@"C:\.graymoon\test-ws\features", true)]
    [InlineData("C:/.graymoon/test-ws", true)]
    [InlineData(@"C:\Users\test\.graymoon", false)]
    [InlineData(@"C:\.graymoonextra", false)]
    [InlineData("/home/dev/.graymoon", false)]
    [InlineData("", false)]
    public void IsLegacyWindowsDriveRootGraymoonPath_DetectsOnlyTheDriveRootBug(string path, bool expected)
    {
        Assert.Equal(expected, AgentPath.IsLegacyWindowsDriveRootGraymoonPath(path));
    }
}
