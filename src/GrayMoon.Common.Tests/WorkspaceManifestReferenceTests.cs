using GrayMoon.Common.Manifest;

namespace GrayMoon.Common.Tests;

/// <summary>
/// Version 2 of the Workspace definition: <c>workspace.reference</c>, <c>repositories[].reference</c> and the
/// <c>branch</c> + <c>commit</c> of a read-only repository. A definition without any of them stays version 1.
/// </summary>
public sealed class WorkspaceManifestReferenceTests
{
    private const string Sha = "3f2a9c0d5e7b1a4c6d8e0f1a2b3c4d5e6f7a8b9c";

    private static readonly IWorkspaceManifestFile ManifestFile = new WorkspaceManifestFile();

    private static WorkspaceManifest Sample(bool? referenceDefault, params WorkspaceManifestRepository[] repositories) => new(
        1,
        new WorkspaceManifestWorkspace(new WorkspaceManifestProfile("basic", "none", "none"), referenceDefault),
        [new WorkspaceManifestConnector("github", "https://github.com")],
        repositories);

    private static WorkspaceManifestRepository Repository(string name, bool? reference = null, string? tag = null, string? branch = null, string? commit = null) =>
        new(name, $"https://github.com/acme/{name}.git", "https://github.com", tag, commit, reference, branch);

    private static string Json(string version, string workspace, string repository) =>
        $$"""
        {
          {{version}}
          "workspace": { {{workspace}} },
          "repositories": [
            { "name": "Billing", "repositoryUrl": "https://github.com/acme/Billing.git", "connectorUrl": "https://github.com"{{repository}} }
          ]
        }
        """;

    [Fact]
    public void The_mixed_sample_round_trips_as_version_2()
    {
        var content = ManifestFile.Serialize(Sample(
            true,
            Repository("Billing", reference: true, branch: "main", commit: Sha),
            Repository("Platform", reference: false)));

        Assert.Contains("\"version\": 2", content);
        Assert.True(ManifestFile.TryParse(content, out var parsed, out var error), error);
        Assert.Equal(2, parsed!.SchemaVersion);
        Assert.True(parsed.Workspace.Reference);
        var billing = parsed.Repositories.Single(repository => repository.Name == "Billing");
        Assert.True(billing.Reference);
        Assert.Equal("main", billing.Branch);
        Assert.Equal(Sha, billing.Commit);
        Assert.Null(billing.Tag);
        var platform = parsed.Repositories.Single(repository => repository.Name == "Platform");
        Assert.False(platform.Reference);
        Assert.Null(platform.Branch);
        Assert.Null(platform.Commit);
        Assert.Equal(content, ManifestFile.Serialize(parsed));
    }

    [Fact]
    public void A_repository_entry_lists_reference_then_branch_then_commit()
    {
        var content = ManifestFile.Serialize(Sample(false, Repository("Billing", reference: true, branch: "main", commit: Sha)));

        var connectorUrl = content.IndexOf("\"connectorUrl\"", content.IndexOf("\"repositories\"", StringComparison.Ordinal), StringComparison.Ordinal);
        var reference = content.IndexOf("\"reference\": true", connectorUrl, StringComparison.Ordinal);
        var branch = content.IndexOf("\"branch\": \"main\"", StringComparison.Ordinal);
        var commit = content.IndexOf("\"commit\"", StringComparison.Ordinal);
        Assert.True(connectorUrl < reference && reference < branch && branch < commit, content);
    }

    [Fact]
    public void A_default_of_true_with_one_writable_repository_round_trips()
    {
        var content = ManifestFile.Serialize(Sample(true, Repository("Billing", reference: true), Repository("Platform", reference: false)));

        Assert.True(ManifestFile.TryParse(content, out var parsed, out var error), error);
        Assert.True(parsed!.Workspace.Reference);
        Assert.Equal([true, false], parsed.Repositories.Select(repository => repository.Reference).ToArray());
    }

    [Fact]
    public void A_definition_without_reference_data_stays_version_1_whatever_version_it_is_given()
    {
        var content = ManifestFile.Serialize(Sample(null, Repository("Billing"), Repository("Platform", tag: "v1.0.0", commit: Sha)) with { SchemaVersion = 2 });

        Assert.Contains("\"version\": 1", content);
        Assert.DoesNotContain("reference", content);
        Assert.DoesNotContain("branch", content);
    }

    [Fact]
    public void Reference_data_makes_the_file_version_2_whatever_version_it_is_given()
    {
        Assert.Contains("\"version\": 2", ManifestFile.Serialize(Sample(null, Repository("Billing", reference: false))));
        Assert.Contains("\"version\": 2", ManifestFile.Serialize(Sample(false, Repository("Billing"))));
    }

    [Fact]
    public void The_newest_version_this_GrayMoon_reads_is_2()
    {
        Assert.Equal(2, ManifestFile.CurrentSchemaVersion);
        Assert.False(ManifestFile.IsNewerSchema(Json("\"version\": 2,", "\"reference\": true", "")));
        Assert.True(ManifestFile.IsNewerSchema(Json("\"version\": 3,", "", "")));
    }

    [Theory]
    [InlineData("\"reference\": \"yes\"")]
    [InlineData("\"reference\": 1")]
    [InlineData("\"reference\": \"\"")]
    public void A_reference_that_is_not_true_or_false_is_rejected_by_name(string reference)
    {
        Assert.False(ManifestFile.TryParse(Json("\"version\": 2,", "", ", " + reference), out var manifest, out var error));
        Assert.Null(manifest);
        Assert.Contains("reference must be true or false", error);

        Assert.False(ManifestFile.TryParse(Json("\"version\": 2,", reference, ""), out _, out var workspaceError));
        Assert.Contains("reference must be true or false", workspaceError);
    }

    [Fact]
    public void A_reference_written_as_text_is_read()
    {
        Assert.True(ManifestFile.TryParse(Json("\"version\": 2,", "\"reference\": \"false\"", ", \"reference\": \"true\""), out var parsed, out var error), error);
        Assert.False(parsed!.Workspace.Reference);
        Assert.True(Assert.Single(parsed.Repositories).Reference);
    }

    [Fact]
    public void A_codegraph_value_that_is_not_true_or_false_keeps_its_own_message()
    {
        const string json = """{ "workspace": { }, "codegraph": "maybe" }""";

        Assert.False(ManifestFile.TryParse(json, out _, out var error));
        Assert.Contains("codegraph must be true or false", error);
    }

    [Theory]
    [InlineData("\"version\": 1,", "\"reference\": true", "")]
    [InlineData("\"version\": 1,", "", ", \"reference\": false")]
    [InlineData("", "", ", \"reference\": true")]
    [InlineData("\"version\": 1,", "", ", \"branch\": \"main\", \"commit\": \"" + Sha + "\"")]
    public void Reference_data_in_a_version_1_file_is_rejected(string version, string workspace, string repository)
    {
        Assert.False(ManifestFile.TryParse(Json(version, workspace, repository), out var manifest, out var error));
        Assert.Null(manifest);
        Assert.Equal("Workspace definition uses reference or branch, which needs \"version\": 2", error);
    }

    [Fact]
    public void A_branch_on_a_writable_repository_is_rejected()
    {
        Assert.False(ManifestFile.TryParse(
            Json("\"version\": 2,", "", $", \"reference\": false, \"branch\": \"main\", \"commit\": \"{Sha}\""), out _, out var error));
        Assert.Contains("Repository 'Billing'", error);
        Assert.Contains("branch", error);
        Assert.Contains("not read only", error);
    }

    [Fact]
    public void A_repository_without_its_own_reference_takes_the_workspace_value_for_its_branch()
    {
        Assert.True(ManifestFile.TryParse(
            Json("\"version\": 2,", "\"reference\": true", $", \"branch\": \"main\", \"commit\": \"{Sha}\""), out var parsed, out var error), error);
        var billing = Assert.Single(parsed!.Repositories);
        Assert.Null(billing.Reference);
        Assert.Equal("main", billing.Branch);
    }

    [Theory]
    [InlineData(", \"reference\": true, \"branch\": \"main\"", "missing the full commit hash")]
    [InlineData(", \"reference\": true, \"branch\": \"main\", \"commit\": \"3f2a9c0\"", "full commit hash")]
    [InlineData(", \"reference\": true, \"branch\": \"main\", \"tag\": \"v1.0.0\", \"commit\": \"" + Sha + "\"", "both tag and branch")]
    [InlineData(", \"reference\": true, \"branch\": \"-x\", \"commit\": \"" + Sha + "\"", "not a valid branch name")]
    public void An_incomplete_or_contradictory_revision_is_rejected(string repository, string expected)
    {
        Assert.False(ManifestFile.TryParse(Json("\"version\": 2,", "", repository), out _, out var error));
        Assert.Contains("Repository 'Billing'", error);
        Assert.Contains(expected, error);
    }

    [Fact]
    public void A_commit_with_neither_tag_nor_branch_is_still_rejected()
    {
        Assert.False(ManifestFile.TryParse(Json("\"version\": 2,", "", $", \"reference\": true, \"commit\": \"{Sha}\""), out _, out var error));
        Assert.Contains("Repository 'Billing'", error);
    }

    [Fact]
    public void A_read_only_repository_on_a_tag_keeps_its_tag_pin()
    {
        Assert.True(ManifestFile.TryParse(
            Json("\"version\": 2,", "", $", \"reference\": true, \"tag\": \"v1.0.0\", \"commit\": \"{Sha}\""), out var parsed, out var error), error);
        var billing = Assert.Single(parsed!.Repositories);
        Assert.Equal("v1.0.0", billing.Tag);
        Assert.Null(billing.Branch);
    }

    [Fact]
    public void The_pinned_revision_of_a_read_only_repository_is_its_commit_and_branch()
    {
        var (commit, branch, error) = ManifestFile.ReadPinnedRevision(
            Json("\"version\": 2,", "", $", \"reference\": true, \"branch\": \"release/1.x\", \"commit\": \"{Sha.ToUpperInvariant()}\""),
            "https://github.com/acme/Billing");

        Assert.Null(error);
        Assert.Equal(Sha, commit);
        Assert.Equal("release/1.x", branch);
    }

    [Fact]
    public void The_pinned_revision_of_a_tag_pin_has_no_branch()
    {
        var (commit, branch, error) = ManifestFile.ReadPinnedRevision(
            Json("", "", $", \"tag\": \"v1.0.0\", \"commit\": \"{Sha}\""), "https://github.com/acme/Billing.git");

        Assert.Null(error);
        Assert.Equal(Sha, commit);
        Assert.Null(branch);
    }

    [Fact]
    public void The_pinned_revision_takes_the_workspace_value_when_the_entry_has_no_reference()
    {
        var (commit, branch, error) = ManifestFile.ReadPinnedRevision(
            Json("\"version\": 2,", "\"reference\": true", $", \"branch\": \"main\", \"commit\": \"{Sha}\""), "https://github.com/acme/Billing.git");

        Assert.Null(error);
        Assert.Equal(Sha, commit);
        Assert.Equal("main", branch);
    }

    [Theory]
    [InlineData(", \"branch\": \"main\", \"commit\": \"" + Sha + "\"", "not read only")]
    [InlineData(", \"reference\": \"yes\", \"branch\": \"main\", \"commit\": \"" + Sha + "\"", "reference must be true or false")]
    [InlineData(", \"reference\": true, \"branch\": 7, \"commit\": \"" + Sha + "\"", "branch")]
    public void An_invalid_entry_gives_no_pinned_revision(string repository, string expected)
    {
        var (commit, branch, error) = ManifestFile.ReadPinnedRevision(Json("\"version\": 2,", "", repository), "https://github.com/acme/Billing.git");

        Assert.Null(commit);
        Assert.Null(branch);
        Assert.Contains(expected, error);
    }
}
