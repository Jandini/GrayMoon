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
        var dotnetVersion = await GetVersionAsync("dotnet", "--version", null, cancellationToken);
        var gitVersion = await GetVersionAsync("git", "--version", null, cancellationToken);
        var gitVersionToolVersion = await GetVersionAsync("dotnet", "gitversion version", null, cancellationToken);
        var codeGraphVersion = await codeGraph.GetVersionAsync(cancellationToken);
        var aiProviders = await aiCli.ProbeAsync(refresh: true, cancellationToken);
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
