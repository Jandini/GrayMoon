using GrayMoon.App.Services.WorkspaceManifest;
using GrayMoon.Application.WorkspaceManifest;

namespace GrayMoon.App.Tests;

public sealed class WorkspaceManifestSerializerTests
{
    private static WorkspaceManifest Sample() => new(
        1,
        new WorkspaceManifestWorkspace("AVR", new WorkspaceManifestProfile("basic", "none", "none")),
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
        var first = WorkspaceManifestSerializer.Serialize(Sample());

        Assert.True(WorkspaceManifestSerializer.TryParse(first, out var parsed, out var error), error);
        var second = WorkspaceManifestSerializer.Serialize(parsed!);

        Assert.Equal(first, second);
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

        var text = WorkspaceManifestSerializer.Serialize(manifest);

        Assert.True(text.IndexOf("AAA.example.com", StringComparison.Ordinal) < text.IndexOf("zzz.example.com", StringComparison.Ordinal));
        var alpha = text.IndexOf("\"Alpha\"", StringComparison.Ordinal);
        var beta = text.IndexOf("\"beta\"", StringComparison.Ordinal);
        var charlie = text.IndexOf("\"charlie\"", StringComparison.Ordinal);
        Assert.True(alpha < beta && beta < charlie);
    }

    [Fact]
    public void Serialize_uses_lf_and_trailing_newline()
    {
        var text = WorkspaceManifestSerializer.Serialize(Sample());

        Assert.DoesNotContain('\r', text);
        Assert.EndsWith("\n", text);
    }

    [Fact]
    public void Serialize_never_contains_id_fields()
    {
        var text = WorkspaceManifestSerializer.Serialize(Sample());

        Assert.DoesNotContain("\"workspaceId\"", text);
        Assert.DoesNotContain("\"repositoryId\"", text);
        Assert.DoesNotContain("\"connectorId\"", text);
        Assert.DoesNotContain("\"gitHubRepositoryId\"", text);
        Assert.DoesNotContain("\"nodeId\"", text);
    }

    [Fact]
    public void Parse_rejects_schema_version_2()
    {
        const string json = """{ "schemaVersion": 2, "workspace": { "name": "AVR" } }""";

        Assert.False(WorkspaceManifestSerializer.TryParse(json, out var manifest, out var error));
        Assert.Null(manifest);
        Assert.Equal("Workspace definition schema 2 is newer than this GrayMoon", error);
    }

    [Fact]
    public void Parse_defaults_missing_schema_version_to_1()
    {
        const string json = """{ "workspace": { "name": "AVR" } }""";

        Assert.True(WorkspaceManifestSerializer.TryParse(json, out var manifest, out var error), error);
        Assert.Equal(1, manifest!.SchemaVersion);
        Assert.Empty(manifest.Connectors);
        Assert.Empty(manifest.Repositories);
    }

    [Fact]
    public void Parse_ignores_unknown_properties()
    {
        const string json = """
            { "SchemaVersion": 1, "future": { "a": 1 },
              "workspace": { "name": "AVR", "extra": true, "profile": { "type": "basic", "versioning": "none", "ci": "none", "more": 1 } },
              "connectors": null,
              "repositories": [ { "name": "Avr.Api", "repositoryUrl": "u", "connectorUrl": "c", "other": 5 } ] }
            """;

        Assert.True(WorkspaceManifestSerializer.TryParse(json, out var manifest, out var error), error);
        Assert.Equal("AVR", manifest!.Workspace.Name);
        Assert.Empty(manifest.Connectors);
        Assert.Single(manifest.Repositories);
    }

    [Fact]
    public void Parse_fails_on_missing_workspace_name()
    {
        const string json = """{ "schemaVersion": 1, "workspace": { "profile": { "type": "basic", "versioning": "none", "ci": "none" } } }""";

        Assert.False(WorkspaceManifestSerializer.TryParse(json, out var manifest, out var error));
        Assert.Null(manifest);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }
}
