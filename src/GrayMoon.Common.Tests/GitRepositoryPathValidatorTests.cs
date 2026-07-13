using GrayMoon.Common.Git;

namespace GrayMoon.Common.Tests;

public class GitRepositoryPathValidatorTests
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "graymoon-path-validator-tests", "repo");

    [Fact]
    public void Valid_relative_path_is_accepted()
    {
        var result = GitRepositoryPathValidator.Validate(_root, "src/File.cs");

        Assert.True(result.IsValid);
        Assert.Equal("src/File.cs", result.NormalizedRelativePath);
        Assert.NotNull(result.FullPath);
    }

    [Fact]
    public void Backslash_separators_are_normalized()
    {
        var result = GitRepositoryPathValidator.Validate(_root, "src\\Nested\\File.cs");

        Assert.True(result.IsValid);
        Assert.Equal("src/Nested/File.cs", result.NormalizedRelativePath);
    }

    [Fact]
    public void Null_or_empty_path_is_rejected()
    {
        Assert.False(GitRepositoryPathValidator.Validate(_root, null).IsValid);
        Assert.False(GitRepositoryPathValidator.Validate(_root, "").IsValid);
        Assert.False(GitRepositoryPathValidator.Validate(_root, "   ").IsValid);
    }

    [Fact]
    public void Unix_absolute_path_is_rejected()
    {
        var result = GitRepositoryPathValidator.Validate(_root, "/etc/passwd");

        Assert.False(result.IsValid);
    }

    [Fact]
    public void Windows_absolute_path_is_rejected()
    {
        var result = GitRepositoryPathValidator.Validate(_root, "C:\\Windows\\System32\\config");

        Assert.False(result.IsValid);
    }

    [Fact]
    public void Parent_traversal_is_rejected()
    {
        var result = GitRepositoryPathValidator.Validate(_root, "../outside.txt");

        Assert.False(result.IsValid);
    }

    [Fact]
    public void Nested_parent_traversal_is_rejected()
    {
        var result = GitRepositoryPathValidator.Validate(_root, "src/../../outside.txt");

        Assert.False(result.IsValid);
    }

    [Fact]
    public void Current_directory_segment_is_rejected()
    {
        var result = GitRepositoryPathValidator.Validate(_root, "./src/File.cs");

        Assert.False(result.IsValid);
    }

    [Fact]
    public void Sibling_directory_sharing_root_as_a_name_prefix_is_not_treated_as_inside_root()
    {
        // Guards against a naive `candidate.StartsWith(root)` check (without a trailing separator)
        // that would incorrectly treat "C:\repo-evil\..." as inside "C:\repo".
        var siblingRoot = _root + "-evil";

        var result = GitRepositoryPathValidator.Validate(siblingRoot, "file.txt");

        Assert.True(result.IsValid);
        Assert.StartsWith(
            Path.GetFullPath(siblingRoot) + Path.DirectorySeparatorChar,
            result.FullPath,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Path_with_spaces_and_unicode_is_valid()
    {
        var result = GitRepositoryPathValidator.Validate(_root, "src/my file 文件.txt");

        Assert.True(result.IsValid);
        Assert.Equal("src/my file 文件.txt", result.NormalizedRelativePath);
    }
}
