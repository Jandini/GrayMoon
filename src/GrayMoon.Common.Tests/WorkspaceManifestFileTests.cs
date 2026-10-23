using GrayMoon.Common.Manifest;

namespace GrayMoon.Common.Tests;

public sealed class WorkspaceManifestFileTests
{
    private static readonly IWorkspaceManifestFile ManifestFile = new WorkspaceManifestFile();

    private static WorkspaceManifest Sample() => new(
        1,
        new WorkspaceManifestWorkspace(new WorkspaceManifestProfile("basic", "none", "none")),
        [
            new WorkspaceManifestConnector("github", "https://github.com")
        ],
        [
            new WorkspaceManifestRepository("Avr.Api", "https://github.com/example/Avr.Api.git", "https://github.com"),
            new WorkspaceManifestRepository("Avr.Web", "https://github.com/example/Avr.Web.git", "https://github.com")
        ]);

    [Fact]
    public void Round_trip_is_byte_identical()
    {
        var first = ManifestFile.Serialize(Sample());

        Assert.True(ManifestFile.TryParse(first, out var parsed, out var error), error);
        var second = ManifestFile.Serialize(parsed!);

        Assert.Equal(first, second);
    }

    [Fact]
    public void Version_files_round_trip_sorted_and_are_omitted_when_empty()
    {
        var manifest = Sample() with
        {
            VersionFiles =
            [
                new WorkspaceManifestVersionFile("Avr.Web", "src/b.props", ["B={avr.api}"]),
                new WorkspaceManifestVersionFile("Avr.Api", "src/a.props", ["A={avr.web}", "C={avr.web:commit}"])
            ]
        };

        var text = ManifestFile.Serialize(manifest);

        Assert.Contains("\"versionFiles\"", text);
        Assert.True(text.IndexOf("src/a.props", StringComparison.Ordinal) < text.IndexOf("src/b.props", StringComparison.Ordinal));
        Assert.True(ManifestFile.TryParse(text, out var parsed, out var error), error);
        Assert.Equal(text, ManifestFile.Serialize(parsed!));
        Assert.Equal("A={avr.web}\nC={avr.web:commit}", parsed!.VersionFiles!.Single(f => f.Repository == "Avr.Api").PatternText());

        Assert.DoesNotContain("versionFiles", ManifestFile.Serialize(Sample() with { VersionFiles = [] }));
    }

    [Fact]
    public void Parse_normalizes_version_file_paths_and_pattern_lines()
    {
        const string content = """
            {
              "version": 1,
              "workspace": { "name": "AVR" },
              "versionFiles": [
                { "repository": "Avr.Api", "path": "\\src\\a.props", "pattern": ["  A={avr.web}", "", "B={avr.web}\nC={avr.web}"] }
              ]
            }
            """;

        Assert.True(ManifestFile.TryParse(content, out var parsed, out var error), error);

        var file = Assert.Single(parsed!.VersionFiles!);
        Assert.Equal("src/a.props", file.Path);
        Assert.Equal(["A={avr.web}", "B={avr.web}", "C={avr.web}"], file.Pattern.ToArray());
    }

    [Fact]
    public void Parse_rejects_version_file_without_path()
    {
        const string content = """
            { "version": 1, "workspace": { "name": "AVR" }, "versionFiles": [ { "repository": "Avr.Api", "pattern": ["A={avr.web}"] } ] }
            """;

        Assert.False(ManifestFile.TryParse(content, out _, out var error));
        Assert.Contains("no path", error);
    }

    [Fact]
    public void Serialize_sorts_connectors_and_repositories()
    {
        var manifest = Sample() with
        {
            Connectors =
            [
                new WorkspaceManifestConnector("github", "https://zzz.example.com"),
                new WorkspaceManifestConnector("github", "https://AAA.example.com")
            ],
            Repositories =
            [
                new WorkspaceManifestRepository("beta", "https://github.com/x/beta.git", "https://github.com"),
                new WorkspaceManifestRepository("Alpha", "https://github.com/x/Alpha.git", "https://github.com"),
                new WorkspaceManifestRepository("charlie", "https://github.com/x/charlie.git", "https://github.com")
            ]
        };

        var text = ManifestFile.Serialize(manifest);

        Assert.True(text.IndexOf("AAA.example.com", StringComparison.Ordinal) < text.IndexOf("zzz.example.com", StringComparison.Ordinal));
        var alpha = text.IndexOf("\"Alpha\"", StringComparison.Ordinal);
        var beta = text.IndexOf("\"beta\"", StringComparison.Ordinal);
        var charlie = text.IndexOf("\"charlie\"", StringComparison.Ordinal);
        Assert.True(alpha < beta && beta < charlie);
    }

    [Fact]
    public void Serialize_uses_lf_and_trailing_newline()
    {
        var text = ManifestFile.Serialize(Sample());

        Assert.DoesNotContain('\r', text);
        Assert.EndsWith("\n", text);
    }

    [Fact]
    public void Serialize_never_contains_id_fields()
    {
        var text = ManifestFile.Serialize(Sample());

        Assert.DoesNotContain("\"workspaceId\"", text);
        Assert.DoesNotContain("\"repositoryId\"", text);
        Assert.DoesNotContain("\"connectorId\"", text);
        Assert.DoesNotContain("\"gitHubRepositoryId\"", text);
        Assert.DoesNotContain("\"nodeId\"", text);
    }

    [Fact]
    public void Parse_rejects_schema_version_2()
    {
        const string json = """{ "version": 2, "workspace": { "name": "AVR" } }""";

        Assert.False(ManifestFile.TryParse(json, out var manifest, out var error));
        Assert.Null(manifest);
        Assert.Equal("Workspace definition schema 2 is newer than this GrayMoon", error);
    }

    [Fact]
    public void Parse_defaults_missing_schema_version_to_1()
    {
        const string json = """{ "workspace": { "name": "AVR" } }""";

        Assert.True(ManifestFile.TryParse(json, out var manifest, out var error), error);
        Assert.Equal(1, manifest!.SchemaVersion);
        Assert.Empty(manifest.Connectors);
        Assert.Empty(manifest.Repositories);
    }

    [Fact]
    public void Parse_ignores_unknown_properties()
    {
        const string json = """
            { "Version": 1, "future": { "a": 1 },
              "workspace": { "name": "AVR", "extra": true, "profile": { "type": "basic", "versioning": "none", "ci": "none", "more": 1 } },
              "connectors": null,
              "repositories": [ { "name": "Avr.Api", "repositoryUrl": "u", "connectorUrl": "c", "other": 5 } ] }
            """;

        Assert.True(ManifestFile.TryParse(json, out var manifest, out var error), error);
        Assert.Equal("basic", manifest!.Workspace.Profile.Type);
        Assert.Empty(manifest.Connectors);
        Assert.Single(manifest.Repositories);
        Assert.False(JsonDocumentHasWorkspaceName(ManifestFile.Serialize(manifest)));
    }

    [Fact]
    public void Serialize_omits_tag_and_commit_when_unset()
    {
        var text = ManifestFile.Serialize(Sample());

        Assert.DoesNotContain("\"tag\"", text);
        Assert.DoesNotContain("\"commit\"", text);
    }

    [Fact]
    public void Tag_pin_round_trips_and_stores_the_full_commit_hash()
    {
        const string sha = "0123456789abcdef0123456789abcdef01234567";
        var manifest = Sample() with
        {
            Repositories =
            [
                new WorkspaceManifestRepository(
                    "Avr.Api",
                    "https://github.com/example/Avr.Api.git",
                    "https://github.com",
                    "v1.0.0",
                    sha)
            ]
        };

        var text = ManifestFile.Serialize(manifest);

        Assert.Contains("\"tag\": \"v1.0.0\"", text);
        Assert.Contains($"\"commit\": \"{sha}\"", text);
        Assert.True(ManifestFile.TryParse(text, out var parsed, out var error), error);
        var repository = Assert.Single(parsed!.Repositories);
        Assert.Equal("v1.0.0", repository.Tag);
        Assert.Equal(sha, repository.Commit);
        Assert.Equal(text, ManifestFile.Serialize(parsed));
    }

    [Fact]
    public void Parse_lowercases_the_commit_hash()
    {
        const string json = """
            { "version": 1, "workspace": { "name": "AVR" },
              "repositories": [ { "name": "Avr.Api", "repositoryUrl": "u", "connectorUrl": "c", "tag": "v1", "commit": "0123456789ABCDEF0123456789abcdef01234567" } ] }
            """;

        Assert.True(ManifestFile.TryParse(json, out var parsed, out var error), error);
        Assert.Equal("0123456789abcdef0123456789abcdef01234567", parsed!.Repositories[0].Commit);
    }

    [Fact]
    public void Parse_rejects_a_tag_without_a_commit()
    {
        const string json = """
            { "version": 1, "workspace": { "name": "AVR" },
              "repositories": [ { "name": "Avr.Api", "repositoryUrl": "u", "connectorUrl": "c", "tag": "v1" } ] }
            """;

        Assert.False(ManifestFile.TryParse(json, out var manifest, out var error));
        Assert.Null(manifest);
        Assert.Equal("Repository 'Avr.Api' is on a tag but is missing the tag name or the full commit hash.", error);
    }

    [Fact]
    public void Parse_rejects_a_short_commit_hash()
    {
        const string json = """
            { "version": 1, "workspace": { "name": "AVR" },
              "repositories": [ { "name": "Avr.Api", "repositoryUrl": "u", "connectorUrl": "c", "tag": "v1", "commit": "abc123" } ] }
            """;

        Assert.False(ManifestFile.TryParse(json, out var manifest, out var error));
        Assert.Null(manifest);
        Assert.Equal("Repository 'Avr.Api' commit must be the full commit hash.", error);
    }

    [Fact]
    public void Parse_accepts_a_workspace_without_a_name_and_serialize_omits_it()
    {
        const string json = """{ "version": 1, "workspace": { "profile": { "type": "basic", "versioning": "none", "ci": "none" } } }""";

        Assert.True(ManifestFile.TryParse(json, out var manifest, out var error), error);
        Assert.Equal("basic", manifest!.Workspace.Profile.Type);
        Assert.False(JsonDocumentHasWorkspaceName(ManifestFile.Serialize(manifest)));
    }

    [Fact]
    public void Parse_ignores_a_leftover_workspace_name_and_serialize_drops_it()
    {
        const string json = """{ "version": 1, "workspace": { "name": "OtherMachine", "profile": { "type": "basic", "versioning": "none", "ci": "none" } } }""";

        Assert.True(ManifestFile.TryParse(json, out var manifest, out var error), error);
        Assert.False(JsonDocumentHasWorkspaceName(ManifestFile.Serialize(manifest!)));
    }

    [Fact]
    public void Parse_fails_when_workspace_is_missing()
    {
        const string json = """{ "version": 1 }""";

        Assert.False(ManifestFile.TryParse(json, out var manifest, out var error));
        Assert.Null(manifest);
        Assert.Equal("Workspace definition is missing workspace", error);
    }

    private static bool JsonDocumentHasWorkspaceName(string json)
    {
        using var document = System.Text.Json.JsonDocument.Parse(json);
        return document.RootElement.GetProperty("workspace").TryGetProperty("name", out _);
    }

    [Fact]
    public void File_name_is_graymoon_json()
        => Assert.Equal("graymoon.json", ManifestFile.FileName);

    [Fact]
    public void Repository_folder_names_are_read_from_every_listed_repository()
    {
        var names = ManifestFile.ReadRepositoryFolderNames("""
            {
              "version": 1,
              "repositories": [
                { "name": "GrayMoon", "repositoryUrl": "https://github.com/acme/GrayMoon.git" },
                { "Name": " GrayMoon.Desktop " }
              ]
            }
            """);

        Assert.Equal(["GrayMoon", "GrayMoon.Desktop"], names);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{ not json")]
    [InlineData("[]")]
    [InlineData("""{ "version": 1 }""")]
    [InlineData("""{ "repositories": {} }""")]
    public void Repository_folder_names_of_missing_or_unreadable_content_are_empty(string? json)
        => Assert.Empty(ManifestFile.ReadRepositoryFolderNames(json));

    [Fact]
    public void Repository_folder_names_skip_entries_that_cannot_be_one_folder_name()
    {
        var names = ManifestFile.ReadRepositoryFolderNames("""
            { "repositories": [ "Api", { "name": 5 }, { "name": "" }, { "name": ".." }, { "name": "a/b" }, { "name": "a\\b" }, { "name": "Api" }, { "name": "api" } ] }
            """);

        Assert.Equal(["Api"], names);
    }

    [Fact]
    public async Task Definition_is_read_from_the_root_of_a_directory()
    {
        var folder = Directory.CreateTempSubdirectory("graymoon-definition-").FullName;
        try
        {
            Assert.Null(await ManifestFile.ReadFromDirectoryAsync(folder));
            Assert.Empty(ManifestFile.ReadRepositoryFolderNamesFromDirectory(folder));

            const string content = """{ "repositories": [ { "name": "Api" } ] }""";
            File.WriteAllText(Path.Combine(folder, ManifestFile.FileName), content);

            Assert.Equal(content, await ManifestFile.ReadFromDirectoryAsync(folder));
            Assert.Equal(["Api"], ManifestFile.ReadRepositoryFolderNamesFromDirectory(folder));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }
}
