using System.Text.Json;
using GrayMoon.Abstractions.Worker;
using GrayMoon.App.Models;
using GrayMoon.App.Services.Orchestration;
using GrayMoon.App.Tests.Policy.Characterization;
using Microsoft.Extensions.DependencyInjection;

namespace GrayMoon.App.Tests;

/// <summary>
/// Update walks the dependency levels. What level 1 commits must reach level 2: its version file is checked against
/// the new version and the new commit of the level 1 repository, and updated with them. The file-version check used to
/// run after every repository refresh, each time with a Worker <c>GetHeadCommits</c> call; it now runs once per batch
/// and reads the stored commit.
/// </summary>
public sealed class UpdateLevelFileCheckTests
{
    private const string NewProducerVersion = "2.3.1-abc.4";
    private const string VersionToken = "@" + EffectiveVersionScenario.ProducerName;
    private const string CommitToken = "@" + EffectiveVersionScenario.ProducerName + ":commit";

    [Fact]
    public async Task Level_two_is_updated_with_the_version_and_the_commit_level_one_has_just_made()
    {
        await using var s = await EffectiveVersionScenario.CreateAsync(configureServices: PolicyHarness.AddFacades);
        await s.MutateStateAsync(s.ProducerLinkId, state => state.HeadCommit = EffectiveVersionScenario.BaseSha);
        await s.ResolveAsync();

        // Level 1: the producer stamps its own branch into a file, and that file is out of date.
        await AddVersionFileAsync(s, s.ProducerId, "self.props", $"BRANCH={{{VersionToken}:branch}}");
        await s.MutateStateAsync(s.ProducerLinkId, state => state.OutOfDateFileRepos = 1);
        // Level 2: the consumer stamps the producer's version and commit. Up to date until the producer commits.
        await AddVersionFileAsync(s, s.ConsumerId, "api.props", $"API={{{VersionToken}}}\nAPI_SHA={{{CommitToken}}}");
        var consumerFileContent = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [VersionToken] = EffectiveVersionScenario.ParentVersion,
            [CommitToken] = EffectiveVersionScenario.BaseSha,
        };

        var producerCommitted = false;
        var bridge = s.Ctx.WorkerBridge;
        bridge.Respond("RefreshRepositoryProjects", data: null);
        bridge.Respond(WorkerHubMethods.GetHeadCommits, _ => new WorkerCommandResponse(true, new
        {
            commits = new Dictionary<string, string>
            {
                [EffectiveVersionScenario.ProducerName] = producerCommitted ? EffectiveVersionScenario.MovedSha : EffectiveVersionScenario.BaseSha,
            },
        }, null));
        bridge.Respond("UpdateFileVersions", args =>
        {
            var request = JsonSerializer.SerializeToElement(args);
            if (request.GetProperty("filePath").GetString() == "api.props")
            {
                foreach (var value in request.GetProperty("tokenValues").EnumerateObject())
                    consumerFileContent[value.Name] = value.Value.GetString()!;
            }

            return new WorkerCommandResponse(true, new { updatedCount = 1 }, null);
        });
        bridge.Respond("StageAndCommit", args =>
        {
            if (JsonSerializer.SerializeToElement(args).GetProperty("repositoryName").GetString() == EffectiveVersionScenario.ProducerName)
                producerCommitted = true;
            return new WorkerCommandResponse(true, new { success = true, committed = true }, null);
        });
        bridge.Respond("RefreshRepositoryVersion", args =>
        {
            var isProducer = JsonSerializer.SerializeToElement(args).GetProperty("repositoryName").GetString() == EffectiveVersionScenario.ProducerName;
            return new WorkerCommandResponse(true, new
            {
                version = isProducer ? NewProducerVersion : "2.3.1-abc.8",
                branch = "feature/x",
                commit = isProducer ? EffectiveVersionScenario.MovedSha : "4444444444444444444444444444444444444444",
                outgoingCommits = 1,
                incomingCommits = 0,
            }, null);
        });
        // The Worker compares the consumer's file with what the App expects.
        bridge.Respond("CheckFileVersions", args =>
        {
            var files = new List<object>();
            foreach (var file in JsonSerializer.SerializeToElement(args).GetProperty("files").EnumerateArray())
            {
                var path = file.GetProperty("filePath").GetString();
                var outOfDate = path != "api.props"
                    ? []
                    : file.GetProperty("expectedValues").EnumerateObject()
                        .Where(expected => consumerFileContent.GetValueOrDefault(expected.Name) != expected.Value.GetString())
                        .Select(expected => (object)new
                        {
                            tokenName = expected.Name,
                            currentValue = consumerFileContent.GetValueOrDefault(expected.Name),
                            expectedValue = expected.Value.GetString(),
                        })
                        .ToList();
                files.Add(new
                {
                    repositoryName = file.GetProperty("repositoryName").GetString(),
                    filePath = path,
                    fileName = path,
                    fileMissing = false,
                    outOfDateLines = outOfDate,
                });
            }

            return new WorkerCommandResponse(true, new { files }, null);
        });
        bridge.Calls.Clear();

        await using var scope = s.Ctx.CreateScope();
        await scope.ServiceProvider.GetRequiredService<DependencyUpdateOrchestrator>().RunAsync(
            s.Ctx.WorkspaceId,
            s.Special,
            CancellationToken.None,
            progress: null,
            onRepoError: (_, _) => { },
            onLevelError: (_, _) => { });

        // Level 2's file got the producer's new version and new commit.
        Assert.Equal(NewProducerVersion, consumerFileContent[VersionToken]);
        Assert.Equal(EffectiveVersionScenario.MovedSha, consumerFileContent[CommitToken]);

        var calls = bridge.Calls.ToList();

        // The commit comes from the Worker only at the start of the run and for the write of the file that uses it.
        Assert.Equal(2, calls.Count(c => c.Command == WorkerHubMethods.GetHeadCommits));

        // The first check after the producer committed already expects its new version and commit, and no check
        // ran while the producer was being refreshed with the values from before the commit.
        var checksAfterCommit = calls
            .SkipWhile(c => c.Command != "StageAndCommit")
            .Where(c => c.Command == "CheckFileVersions")
            .Select(c => JsonSerializer.Serialize(c.Args))
            .ToList();
        Assert.NotEmpty(checksAfterCommit);
        Assert.Contains(NewProducerVersion, checksAfterCommit[0]);
        Assert.Contains(EffectiveVersionScenario.MovedSha, checksAfterCommit[0]);

        // Four checks for two levels: after the level 1 refresh, at the start of level 2, after the level 2 refresh,
        // and in the recompute at the end. Before, every refreshed repository ran its own and the end ran two.
        Assert.Equal(4, calls.Count(c => c.Command == "CheckFileVersions"));
    }

    private static async Task<int> AddVersionFileAsync(EffectiveVersionScenario s, int repositoryId, string path, string pattern)
    {
        var fileId = 0;
        await s.WithDbAsync(async db =>
        {
            var file = new WorkspaceFile { WorkspaceId = s.Ctx.WorkspaceId, RepositoryId = repositoryId, FilePath = path, FileName = path };
            db.WorkspaceFiles.Add(file);
            await db.SaveChangesAsync();
            db.WorkspaceFileVersionConfigs.Add(new WorkspaceFileVersionConfig { FileId = file.FileId, VersionPattern = pattern });
            fileId = file.FileId;
        });
        return fileId;
    }
}
