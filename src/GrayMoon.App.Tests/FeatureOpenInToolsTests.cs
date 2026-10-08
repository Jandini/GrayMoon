using GrayMoon.App.Components.Features;
using GrayMoon.App.Services.Ui;
using GrayMoon.App.Services.WorkspaceManifest;
using GrayMoon.Application.WorkspaceManifest;
using Microsoft.Extensions.Logging.Abstractions;

namespace GrayMoon.App.Tests;

public sealed class FeatureOpenInToolsTests
{
    [Fact]
    public void Visible_buttons_are_empty_until_a_tool_is_used()
    {
        var buttons = FeatureOpenInTools.VisibleButtons([], cursor: true, claudeCli: true, vsCode: true, visualStudio: true);

        Assert.Empty(buttons);
    }

    [Fact]
    public void Record_use_puts_the_latest_tool_first_including_terminal_and_explorer()
    {
        var recent = FeatureOpenInTools.RecordUse([], FeatureOpenInTools.Cursor);
        recent = FeatureOpenInTools.RecordUse(recent, FeatureOpenInTools.Terminal);
        recent = FeatureOpenInTools.RecordUse(recent, FeatureOpenInTools.Explorer);
        recent = FeatureOpenInTools.RecordUse(recent, FeatureOpenInTools.Cursor);

        Assert.Equal(
            [FeatureOpenInTools.Cursor, FeatureOpenInTools.Explorer, FeatureOpenInTools.Terminal],
            recent);
    }

    [Fact]
    public void Visible_buttons_hide_unavailable_ides_and_keep_recent_order()
    {
        var recent = new[]
        {
            FeatureOpenInTools.Explorer,
            FeatureOpenInTools.VsCode,
            FeatureOpenInTools.Cursor,
            FeatureOpenInTools.Terminal,
        };
        var buttons = FeatureOpenInTools.VisibleButtons(recent, cursor: false, claudeCli: true, vsCode: true, visualStudio: false);

        Assert.Equal(
            [FeatureOpenInTools.Explorer, FeatureOpenInTools.VsCode, FeatureOpenInTools.Terminal],
            buttons);
    }
}

public sealed class WorkspaceManifestRecentToolsTests
{
    [Fact]
    public void Apply_keeps_the_definition_and_appends_recent_tools()
    {
        var definition = WorkspaceManifestSerializer.Serialize(new WorkspaceManifest(
            1,
            new WorkspaceManifestWorkspace("AVR", new WorkspaceManifestProfile("basic", "none", "none")),
            [],
            []));

        var updated = WorkspaceManifestRecentTools.Apply(definition, [FeatureOpenInTools.Cursor, "not-a-tool", FeatureOpenInTools.Cursor]);

        Assert.NotNull(updated);
        Assert.Equal([FeatureOpenInTools.Cursor], WorkspaceManifestRecentTools.Read(updated));
        Assert.True(WorkspaceManifestSerializer.TryParse(updated!, out var parsed, out var error), error);
        Assert.Equal("AVR", parsed!.Workspace.Name);
        Assert.Equal(definition, WorkspaceManifestSerializer.Serialize(parsed));
    }

    [Fact]
    public void Apply_returns_null_for_content_that_is_not_a_definition()
    {
        Assert.Null(WorkspaceManifestRecentTools.Apply("not json", [FeatureOpenInTools.Cursor]));
        Assert.Null(WorkspaceManifestRecentTools.Apply(null, [FeatureOpenInTools.Cursor]));
    }

    [Fact]
    public void Preserve_copies_recent_tools_onto_a_rewritten_definition()
    {
        var definition = WorkspaceManifestSerializer.Serialize(new WorkspaceManifest(
            1,
            new WorkspaceManifestWorkspace("AVR", new WorkspaceManifestProfile("basic", "none", "none")),
            [],
            []));
        var existing = WorkspaceManifestRecentTools.Apply(definition, [FeatureOpenInTools.VisualStudio]);

        var preserved = WorkspaceManifestRecentTools.Preserve(definition, existing);

        Assert.Equal([FeatureOpenInTools.VisualStudio], WorkspaceManifestRecentTools.Read(preserved));
        Assert.Equal(definition, WorkspaceManifestRecentTools.Preserve(definition, definition));
    }
}

public sealed class WorkspaceOpenInRecentToolsTests
{
    [Fact]
    public async Task Record_patches_existing_manifest_and_does_not_create_one()
    {
        await using var fixture = await ManifestTestFixture.CreateAsync();
        var workspace = await fixture.SeedWorkspaceWithRootRepositoryAsync();
        var manifest = fixture.CreateService();
        var definition = manifest.Serialize(await manifest.BuildFromDatabaseAsync(workspace.WorkspaceId));
        fixture.Bridge.RespondWithFile(definition);
        var recent = CreateRecent(fixture);

        var tools = await recent.RecordAsync(workspace.WorkspaceId, FeatureOpenInTools.Cursor);

        Assert.Equal([FeatureOpenInTools.Cursor], tools);
        var write = Assert.Single(fixture.Bridge.Sent, sent => sent.Command == "WriteRepositoryFile");
        var content = ManifestTestFixture.ToJson(write.Args).GetProperty("content").GetString();
        Assert.Equal([FeatureOpenInTools.Cursor], WorkspaceManifestRecentTools.Read(content));
        Assert.True(WorkspaceManifestSerializer.TryParse(content!, out _, out var error), error);
    }

    [Fact]
    public async Task Record_without_workspace_repository_stays_in_memory()
    {
        await using var fixture = await ManifestTestFixture.CreateAsync();
        var workspace = await fixture.AddWorkspaceAsync("plain");
        var recent = CreateRecent(fixture);

        var tools = await recent.RecordAsync(workspace.WorkspaceId, FeatureOpenInTools.ClaudeCli);

        Assert.Equal([FeatureOpenInTools.ClaudeCli], tools);
        Assert.DoesNotContain(fixture.Bridge.Sent, sent => sent.Command == "WriteRepositoryFile");
        Assert.Equal([FeatureOpenInTools.ClaudeCli], await recent.GetAsync(workspace.WorkspaceId));
    }

    [Fact]
    public async Task Record_does_not_create_a_missing_manifest()
    {
        await using var fixture = await ManifestTestFixture.CreateAsync();
        var workspace = await fixture.SeedWorkspaceWithRootRepositoryAsync();
        fixture.Bridge.Handler = (command, _) => command == "GetFileContents"
            ? new GrayMoon.Abstractions.Worker.WorkerCommandResponse(true, new { errorMessage = "File not found: .graymoon.json" }, null)
            : new GrayMoon.Abstractions.Worker.WorkerCommandResponse(true, new { success = true }, null);
        var recent = CreateRecent(fixture);

        var tools = await recent.RecordAsync(workspace.WorkspaceId, FeatureOpenInTools.VsCode);

        Assert.Equal([FeatureOpenInTools.VsCode], tools);
        Assert.DoesNotContain(fixture.Bridge.Sent, sent => sent.Command == "WriteRepositoryFile");
    }

    private static WorkspaceOpenInRecentTools CreateRecent(ManifestTestFixture fixture) => new(
        fixture.Bridge,
        new FakeWorkspacePathResolver(fixture.Factory),
        new FakeFeatureContextResolver(),
        NullLogger<WorkspaceOpenInRecentTools>.Instance);
}
