using System.Text.Json;
using GrayMoon.Abstractions.Worker;
using GrayMoon.App.Services.Features;
using GrayMoon.App.Services.WorkspaceManifest;
using GrayMoon.Application.Features;
using Microsoft.Extensions.Logging.Abstractions;

namespace GrayMoon.App.Tests;

public sealed class CodeGraphWorkspaceTests
{
    [Fact]
    public void Config_file_lists_every_source_repository_folder()
    {
        var content = CodeGraphConfigFile.Apply(null, ["Web", "Api", "api"]);

        Assert.Equal("{\n  \"include\": [\n    \"Api/\",\n    \"Web/\"\n  ]\n}\n", content);
    }

    [Fact]
    public void Config_file_keeps_other_properties_and_the_include_position()
    {
        const string existing = "{\n  \"exclude\": [\"**/bin/**\"],\n  \"Include\": [\"Old/\"],\n  \"x\": 1\n}\n";

        var content = CodeGraphConfigFile.Apply(existing, ["Api"]);

        using var doc = JsonDocument.Parse(content!);
        Assert.Equal(["exclude", "Include", "x"], doc.RootElement.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal(["Api/"], doc.RootElement.GetProperty("Include").EnumerateArray().Select(e => e.GetString()!).ToArray());
        Assert.Equal(1, doc.RootElement.GetProperty("x").GetInt32());
    }

    [Fact]
    public void Config_file_that_is_not_json_is_never_overwritten()
    {
        Assert.Null(CodeGraphConfigFile.Apply("{ not json", ["Api"]));
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData("\"true\"", true)]
    [InlineData("\"False\"", false)]
    public void Definition_reads_codegraph(string value, bool expected)
    {
        var json = "{\"version\":1,\"workspace\":{\"name\":\"ws\"},\"codegraph\":" + value + "}";

        Assert.True(WorkspaceManifestSerializer.TryParse(json, out var manifest, out var error), error);
        Assert.Equal(expected, manifest!.CodeGraph);
    }

    [Fact]
    public void Definition_without_codegraph_does_not_write_it()
    {
        Assert.True(WorkspaceManifestSerializer.TryParse("{\"version\":1,\"workspace\":{\"name\":\"ws\"}}", out var manifest, out _));

        Assert.Null(manifest!.CodeGraph);
        Assert.DoesNotContain("codegraph", WorkspaceManifestSerializer.Serialize(manifest), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"codegraph\": true", WorkspaceManifestSerializer.Serialize(manifest with { CodeGraph = true }), StringComparison.Ordinal);
    }

    [Fact]
    public void Definition_with_an_invalid_codegraph_value_is_rejected()
    {
        Assert.False(WorkspaceManifestSerializer.TryParse("{\"version\":1,\"workspace\":{\"name\":\"ws\"},\"codegraph\":\"yes\"}", out _, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public async Task Write_manifest_keeps_codegraph_from_the_file()
    {
        await using var fixture = await ManifestTestFixture.CreateAsync();
        var workspace = await fixture.SeedWorkspaceWithRootRepositoryAsync();
        var service = fixture.CreateService();
        var definition = service.Serialize(await service.BuildFromDatabaseAsync(workspace.WorkspaceId) with { CodeGraph = true });
        fixture.Bridge.RespondWithFile(definition);

        var result = await service.WriteAuthoritativeManifestAsync(workspace.WorkspaceId);

        Assert.True(result.Success, result.Error);
        var write = Assert.Single(fixture.Bridge.Sent, sent => sent.Command == WorkerHubMethods.WriteRepositoryFile);
        Assert.Equal(definition, ManifestTestFixture.ToJson(write.Args).GetProperty("content").GetString());
    }

    [Fact]
    public async Task Drift_ignores_codegraph()
    {
        await using var fixture = await ManifestTestFixture.CreateAsync();
        var workspace = await fixture.SeedWorkspaceWithRootRepositoryAsync();
        var service = fixture.CreateService();
        fixture.Bridge.RespondWithFile(service.Serialize(await service.BuildFromDatabaseAsync(workspace.WorkspaceId) with { CodeGraph = true }));

        var drift = await service.DetectDriftAsync(workspace.WorkspaceId);

        Assert.False(drift.HasDrift);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(null, false)]
    public async Task Codegraph_is_enabled_only_by_the_definition(bool? codeGraph, bool expected)
    {
        await using var fixture = await ManifestTestFixture.CreateAsync();
        var workspace = await fixture.SeedWorkspaceWithRootRepositoryAsync();
        var service = fixture.CreateService();
        fixture.Bridge.RespondWithFile(service.Serialize(await service.BuildFromDatabaseAsync(workspace.WorkspaceId) with { CodeGraph = codeGraph }));

        Assert.Equal(expected, await service.IsCodeGraphEnabledAsync(workspace.WorkspaceId));
    }

    [Fact]
    public async Task Write_codegraph_config_creates_the_file_in_the_workspace_repository()
    {
        await using var fixture = await ManifestTestFixture.CreateAsync();
        var workspace = await fixture.SeedWorkspaceWithRootRepositoryAsync();
        var service = fixture.CreateService();
        fixture.Bridge.Handler = (command, _) => command == "GetFileContents"
            ? new WorkerCommandResponse(true, new { errorMessage = "File not found: codegraph.json" }, null)
            : new WorkerCommandResponse(true, new { success = true, written = true }, null);

        var result = await service.WriteCodeGraphConfigAsync(workspace.WorkspaceId);

        Assert.True(result.Success, result.Error);
        var args = ManifestTestFixture.ToJson(Assert.Single(fixture.Bridge.Sent, s => s.Command == WorkerHubMethods.WriteRepositoryFile).Args);
        Assert.Equal(CodeGraphConfigFile.FilePath, args.GetProperty("filePath").GetString());
        Assert.Equal("ws-root", args.GetProperty("repositoryName").GetString());
        Assert.Equal(CodeGraphConfigFile.Apply(null, ["Api", "Web"]), args.GetProperty("content").GetString());
    }

    [Theory]
    [InlineData(true, new[] { "gitignore", "manifest", "codegraph" })]
    [InlineData(false, new[] { "gitignore", "manifest" })]
    public async Task Sync_definition_writes_codegraph_config_only_when_enabled(bool enabled, string[] expected)
    {
        var manifest = new RecordingManifestService { CodeGraphEnabled = enabled };

        var error = await manifest.SyncDefinitionToDiskAsync(7);

        Assert.Null(error);
        Assert.Equal(expected, manifest.Calls);
    }

    [Fact]
    public async Task Feature_init_does_nothing_when_codegraph_is_off()
    {
        var bridge = new ScriptedWorkerBridge();
        var service = CreateFeatureService(new RecordingManifestService { CodeGraphEnabled = false }, bridge);

        Assert.Equal(FeatureCodeGraphService.Disabled, await service.InitializeAsync(7, new WorkspaceFeatureContextId(2)));
        Assert.Equal(FeatureCodeGraphService.Disabled, await service.UninitializeAsync(7, new WorkspaceFeatureContextId(2)));
        Assert.Empty(bridge.Sent);
    }

    [Fact]
    public async Task Feature_init_writes_both_configs_then_indexes_the_feature_root()
    {
        var manifest = new RecordingManifestService { CodeGraphEnabled = true };
        var bridge = new ScriptedWorkerBridge { Handler = (_, _) => new WorkerCommandResponse(true, new { outcome = "Started" }, null) };
        var service = CreateFeatureService(manifest, bridge);

        var outcome = await service.InitializeAsync(7, new WorkspaceFeatureContextId(2));

        Assert.Equal("Started", outcome);
        Assert.Equal(["codegraph", "codegraph"], manifest.Calls);
        var sent = Assert.Single(bridge.Sent);
        Assert.Equal(WorkerHubMethods.InitCodeGraph, sent.Command);
        var args = ManifestTestFixture.ToJson(sent.Args);
        Assert.Equal(@"C:\work\ws", args.GetProperty("sourceRoot").GetString());
        Assert.Equal(@"C:\features\ws\feat", args.GetProperty("targetRoot").GetString());
    }

    [Fact]
    public async Task Feature_uninit_removes_the_feature_root_index()
    {
        var bridge = new ScriptedWorkerBridge { Handler = (_, _) => new WorkerCommandResponse(true, new { outcome = "Removed" }, null) };
        var service = CreateFeatureService(new RecordingManifestService { CodeGraphEnabled = true }, bridge);

        var outcome = await service.UninitializeAsync(7, new WorkspaceFeatureContextId(2));

        Assert.Equal("Removed", outcome);
        var sent = Assert.Single(bridge.Sent);
        Assert.Equal(WorkerHubMethods.UninitCodeGraph, sent.Command);
        Assert.Equal(@"C:\features\ws\feat", ManifestTestFixture.ToJson(sent.Args).GetProperty("root").GetString());
    }

    [Fact]
    public async Task Feature_uninit_reports_a_worker_failure()
    {
        var bridge = new ScriptedWorkerBridge { Handler = (_, _) => new WorkerCommandResponse(false, null, "Worker not connected.") };
        var service = CreateFeatureService(new RecordingManifestService { CodeGraphEnabled = true }, bridge);

        Assert.Equal(FeatureCodeGraphService.Failed, await service.UninitializeAsync(7, new WorkspaceFeatureContextId(2)));
    }

    private static FeatureCodeGraphService CreateFeatureService(RecordingManifestService manifest, ScriptedWorkerBridge bridge) => new(
        manifest,
        bridge,
        new RootPathResolver(),
        new FakeFeatureContextResolver(),
        NullLogger<FeatureCodeGraphService>.Instance);

    /// <summary>Context 1 is the special Workspace; any other context is the Feature <c>feat</c>.</summary>
    private sealed class RootPathResolver : IWorkspaceContextPathResolver
    {
        public Task<string> GetContextRootAsync(WorkspaceFeatureContextId contextId, CancellationToken cancellationToken = default) =>
            Task.FromResult(contextId.Value == 1 ? @"C:\work\ws" : @"C:\features\ws\feat");

        public Task<string> GetRepositoryPathAsync(WorkspaceFeatureContextId contextId, int workspaceRepositoryId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<WorkerWorkspaceArgs> GetWorkerArgsAsync(WorkspaceFeatureContextId contextId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
