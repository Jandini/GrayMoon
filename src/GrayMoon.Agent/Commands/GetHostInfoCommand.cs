using GrayMoon.Agent.Abstractions;
using GrayMoon.Agent.Jobs.Requests;
using GrayMoon.Agent.Jobs.Response;
using GrayMoon.Common;

namespace GrayMoon.Agent.Commands;

public sealed class GetHostInfoCommand(ICommandLineService commandLine) : ICommandHandler<GetHostInfoRequest, GetHostInfoResponse>
{
    public async Task<GetHostInfoResponse> ExecuteAsync(GetHostInfoRequest request, CancellationToken cancellationToken = default)
    {
        var dotnetVersion = await GetVersionAsync("dotnet", "--version", null, cancellationToken);
        var gitVersion = await GetVersionAsync("git", "--version", null, cancellationToken);
        var gitVersionToolVersion = await GetVersionAsync("dotnet", "gitversion version", null, cancellationToken);

        return new GetHostInfoResponse
        {
            DotnetVersion = dotnetVersion,
            GitVersion = gitVersion,
            GitVersionToolVersion = gitVersionToolVersion
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
