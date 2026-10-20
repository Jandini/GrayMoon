using System.Text.Json;
using GrayMoon.App.Services.Worker;
using GrayMoon.Common.Ai;

namespace GrayMoon.App.Tests;

public sealed class HostInfoCacheTests
{
    [Fact]
    public void TryRead_maps_prerequisite_versions_and_ai_clis_from_one_host_check()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "dotnetVersion": "10.0.100",
              "gitVersion": "2.47.0",
              "gitVersionToolVersion": "5.12.0",
              "codeGraphVersion": "0.4.0",
              "claudeVersion": "2.1.0",
              "codexVersion": "0.98.0",
              "cursorAgentVersion": null,
              "aiProviders": [
                { "providerId": "claude", "isInstalled": true, "version": "2.1.0", "hasNoTools": true },
                { "providerId": "codex", "isInstalled": false, "hasNoTools": true, "problem": "codex was not found on the Worker's PATH." },
                { "providerId": "cursor", "isInstalled": false, "hasNoTools": false, "problem": "agent was not found on the Worker's PATH." }
              ]
            }
            """);

        var read = HostInfoCache.TryRead(document.RootElement);

        Assert.NotNull(read);
        Assert.Equal("2.1.0", read!.Versions.ClaudeVersion);
        Assert.Equal("0.98.0", read.Versions.CodexVersion);
        Assert.Null(read.Versions.CursorAgentVersion);
        Assert.Equal("0.4.0", read.CodeGraphVersion);
        Assert.Equal([HostPrerequisiteIds.CursorAgent], HostPrerequisiteState.GetMissingIds(read.Versions));
        var claude = Assert.Single(read.AiProviders, p => p.ProviderId == AiProviderIds.Claude);
        Assert.True(claude.IsInstalled);
        Assert.True(claude.HasNoTools);
        Assert.False(read.AiProviders.Single(p => p.ProviderId == AiProviderIds.Codex).IsInstalled);
    }
}
