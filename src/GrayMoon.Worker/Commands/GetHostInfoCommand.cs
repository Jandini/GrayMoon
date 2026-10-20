using GrayMoon.Abstractions.Worker;
using GrayMoon.Worker.Abstractions;
using GrayMoon.Worker.Jobs.Requests;
using GrayMoon.Worker.Jobs.Response;
using GrayMoon.Worker.Services;
using GrayMoon.Common;
using GrayMoon.Common.Ai;
using GrayMoon.Worker.Services.Ai;

namespace GrayMoon.Worker.Commands;

public sealed class GetHostInfoCommand(ICommandLineService commandLine, CodeGraphCli codeGraph, AiCliRunner aiCli) : ICommandHandler<GetHostInfoRequest, GetHostInfoResponse>
{
    public async Task<GetHostInfoResponse> ExecuteAsync(GetHostInfoRequest request, CancellationToken cancellationToken = default)
    {
        string? dotnetVersion = null;
        string? gitVersion = null;
        string? gitVersionToolVersion = null;
        string? codeGraphVersion = null;
        var providers = new AiCliAvailability?[AiProviderIds.All.Count];

        var probes = new List<Func<CancellationToken, Task>>
        {
            async ct => dotnetVersion = await GetVersionAsync("dotnet", "--version", null, ct),
            async ct => gitVersion = await GetVersionAsync("git", "--version", null, ct),
            async ct => gitVersionToolVersion = await GetVersionAsync("dotnet", "gitversion version", null, ct),
            async ct => codeGraphVersion = await codeGraph.GetVersionAsync(ct),
        };
        for (var i = 0; i < AiProviderIds.All.Count; i++)
        {
            var index = i;
            var providerId = AiProviderIds.All[i];
            probes.Add(async ct => providers[index] = await aiCli.ProbeProviderAsync(providerId, refresh: true, ct));
        }

        await Parallel.ForEachAsync(
            probes,
            new ParallelOptions { MaxDegreeOfParallelism = AiCliRunner.MaxConcurrentProbes, CancellationToken = cancellationToken },
            async (probe, ct) => await probe(ct));
        var aiProviders = providers.OfType<AiCliAvailability>().ToList();
        string? InstalledVersion(string providerId) =>
            aiProviders.FirstOrDefault(p => p.ProviderId == providerId && p.IsInstalled)?.Version;

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(userProfile))
            userProfile = null;
        else
            userProfile = userProfile.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return new GetHostInfoResponse
        {
            DotnetVersion = dotnetVersion,
            GitVersion = gitVersion,
            GitVersionToolVersion = gitVersionToolVersion,
            CodeGraphVersion = codeGraphVersion,
            CursorAgentVersion = InstalledVersion(AiProviderIds.Cursor),
            ClaudeVersion = InstalledVersion(AiProviderIds.Claude),
            CodexVersion = InstalledVersion(AiProviderIds.Codex),
            AiProviders = aiProviders.Select(p => new AiProviderAvailabilityDto
            {
                ProviderId = p.ProviderId,
                IsInstalled = p.IsInstalled,
                Version = p.Version,
                HasNoTools = p.HasNoTools,
                Problem = p.Problem,
            }).ToList(),
            UserProfilePath = userProfile,
        };
    }

    private async Task<string?> GetVersionAsync(string fileName, string arguments, string? workingDirectory, CancellationToken cancellationToken)
    {
        try
        {
            var result = await commandLine.RunAsync(fileName, arguments, workingDirectory, null, cancellationToken);
            if (result.ExitCode != 0)
                return null;

            var line = result.Stdout?.Trim().Split('\n', '\r').FirstOrDefault()?.Trim();
            if (string.IsNullOrWhiteSpace(line))
                return null;

            if (fileName == "git" && line.StartsWith("git version ", StringComparison.OrdinalIgnoreCase))
                line = line["git version ".Length..].Trim();

            return line;
        }
        catch
        {
            return null;
        }
    }
}
