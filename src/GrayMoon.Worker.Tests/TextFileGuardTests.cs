using System.Text;
using GrayMoon.Worker.Commands;
using GrayMoon.Worker.Jobs.Requests;
using GrayMoon.Worker.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace GrayMoon.Worker.Tests;

public sealed class TextFileGuardTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("graymoon-tfg-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* best-effort */ }
    }

    private static readonly byte[] BinaryBytes = [0x4D, 0x5A, 0x90, 0x00, 0x03, 0x00, 0x56, 0x45, 0x52, 0x3D];

    [Fact]
    public void Utf8_text_is_accepted()
    {
        var path = Path.Combine(_root, "versions.env");
        File.WriteAllText(path, "VERSION=1.2.3\r\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        Assert.Null(TextFileGuard.GetRejectionReason(path));
    }

    [Fact]
    public void Utf16_text_with_bom_is_accepted()
    {
        var path = Path.Combine(_root, "versions.txt");
        File.WriteAllText(path, "VERSION=1.2.3\r\n", Encoding.Unicode);
        Assert.Null(TextFileGuard.GetRejectionReason(path));
    }

    [Fact]
    public void Empty_file_is_accepted()
    {
        var path = Path.Combine(_root, "empty.txt");
        File.WriteAllBytes(path, []);
        Assert.Null(TextFileGuard.GetRejectionReason(path));
    }

    [Fact]
    public void File_with_nul_byte_is_rejected()
    {
        var path = Path.Combine(_root, "tool.dll");
        File.WriteAllBytes(path, BinaryBytes);
        Assert.NotNull(TextFileGuard.GetRejectionReason(path));
    }

    [Fact]
    public void File_over_the_size_cap_is_rejected()
    {
        var path = Path.Combine(_root, "huge.txt");
        using (var stream = File.Create(path))
            stream.SetLength(TextFileGuard.MaxTextFileBytes + 1);
        Assert.NotNull(TextFileGuard.GetRejectionReason(path));
    }

    [Fact]
    public async Task UpdateFileVersions_leaves_a_binary_file_untouched()
    {
        var repoPath = Path.Combine(_root, "ws", "Repo");
        Directory.CreateDirectory(repoPath);
        var fullPath = Path.Combine(repoPath, "tool.dll");
        await File.WriteAllBytesAsync(fullPath, BinaryBytes);

        var response = await new UpdateFileVersionsCommand().ExecuteAsync(new UpdateFileVersionsRequest
        {
            WorkspaceRoot = _root,
            WorkspaceName = "ws",
            RepositoryName = "Repo",
            FilePath = "tool.dll",
            VersionPattern = "VER={@Repo}",
            TokenValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["@Repo"] = "9.9.9" }
        });

        Assert.Equal(0, response.UpdatedCount);
        Assert.NotNull(response.ErrorMessage);
        Assert.Equal(BinaryBytes, await File.ReadAllBytesAsync(fullPath));
    }

    [Fact]
    public async Task CheckFileVersions_reports_every_token_of_a_binary_file_as_not_found()
    {
        var repoPath = Path.Combine(_root, "ws", "Repo");
        Directory.CreateDirectory(repoPath);
        await File.WriteAllBytesAsync(Path.Combine(repoPath, "tool.dll"), BinaryBytes);

        var response = await new CheckFileVersionsCommand(NullLogger<CheckFileVersionsCommand>.Instance).ExecuteAsync(new CheckFileVersionsRequest
        {
            WorkspaceRoot = _root,
            WorkspaceName = "ws",
            Files =
            [
                new CheckFileVersionsItem
                {
                    RepositoryName = "Repo",
                    FilePath = "tool.dll",
                    Pattern = "VER={@Repo}",
                    ExpectedValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["@Repo"] = "9.9.9" }
                }
            ]
        });

        var file = Assert.Single(response.Files!);
        Assert.False(file.FileMissing);
        Assert.Equal(0, file.TotalMatchedLines);
        Assert.Empty(file.OutOfDateLines!);
        Assert.Equal(["@Repo"], file.NotMatchedTokens!);
    }
}
