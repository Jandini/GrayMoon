using GrayMoon.Common.Manifest;
using GrayMoon.Worker.Services;
using Microsoft.Extensions.Logging;

namespace GrayMoon.Worker.Tests;

public sealed class CsProjFileServiceNameCollisionTests : IDisposable
{
    private readonly TempGitRepositoryFixture _repo = new();
    private readonly CapturingLogger<CsProjFileService> _logger = new();
    private readonly CsProjFileService _service;

    public CsProjFileServiceNameCollisionTests()
    {
        _service = new CsProjFileService(new CsProjFileParser(), new LibGit2SharpGitIgnoreService(), new WorkspaceManifestFile(), _logger);
        _repo.CommitInitial();
    }

    public void Dispose() => _repo.Dispose();

    private static string Csproj(string assemblyName, string? packageId = null, bool? packable = null) =>
        $"""
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            <AssemblyName>{assemblyName}</AssemblyName>
            {(packageId is null ? "" : $"<PackageId>{packageId}</PackageId>")}
            {(packable is null ? "" : $"<IsPackable>{packable.ToString()!.ToLowerInvariant()}</IsPackable>")}
          </PropertyGroup>
        </Project>
        """;

    private IReadOnlyList<(LogLevel Level, string Message)> Warnings =>
        _logger.Entries.Where(e => e.Level == LogLevel.Warning).ToList();

    [Fact]
    public async Task Ordinary_collision_logs_one_warning_and_returns_every_project()
    {
        _repo.WriteFile("Shell.csproj", Csproj("Project", packable: false));
        _repo.WriteFile("packages/migrations/Migrations.csproj", Csproj("Project", packable: true));

        var found = await _service.FindAsync(_repo.RepositoryPath);

        Assert.Equal(2, found.Count);
        var warning = Assert.Single(Warnings);
        Assert.Contains("Project name collision", warning.Message);
        Assert.DoesNotContain("Ambiguous", warning.Message);
        Assert.Contains("ProjectName=Project", warning.Message);
        Assert.Contains("Candidates=2", warning.Message);
        Assert.Contains("PackageIdentities=1", warning.Message);
        Assert.Contains("PackageId=null", warning.Message);
        Assert.Contains("packages/migrations/Migrations.csproj", warning.Message);
        Assert.Contains("Shell.csproj", warning.Message);
    }

    [Fact]
    public async Task Ambiguous_package_collision_logs_one_stronger_warning_with_ordered_candidates()
    {
        // ProjectName follows a local PackageId, so two projects declaring the same one collide as producers.
        _repo.WriteFile("src/b/B.csproj", Csproj("Ignored", packageId: "Project"));
        _repo.WriteFile("src/a/A.csproj", Csproj("Ignored2", packageId: "Project"));

        var found = await _service.FindAsync(_repo.RepositoryPath);

        Assert.Equal(2, found.Count);
        var warning = Assert.Single(Warnings);
        Assert.Contains("Ambiguous package-producer", warning.Message);
        Assert.Contains("PackageIdentities=2", warning.Message);
        Assert.True(warning.Message.IndexOf("src/a/A.csproj", StringComparison.Ordinal) < warning.Message.IndexOf("src/b/B.csproj", StringComparison.Ordinal));
        Assert.Contains("PackageId=Project", warning.Message);
    }

    [Fact]
    public async Task No_collision_logs_no_warning()
    {
        _repo.WriteFile("A/A.csproj", Csproj("A"));
        _repo.WriteFile("B/B.csproj", Csproj("B", packable: true));

        var found = await _service.FindAsync(_repo.RepositoryPath);

        Assert.Equal(2, found.Count);
        Assert.Empty(Warnings);
    }
}
