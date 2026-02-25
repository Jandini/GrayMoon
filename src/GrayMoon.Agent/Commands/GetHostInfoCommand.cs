using System.Diagnostics;
using GrayMoon.Agent.Abstractions;
using GrayMoon.Agent.Jobs.Requests;
using GrayMoon.Agent.Jobs.Response;

namespace GrayMoon.Agent.Commands;

public sealed class GetHostInfoCommand : ICommandHandler<GetHostInfoRequest, GetHostInfoResponse>
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

    private static async Task<string?> GetVersionAsync(string fileName, string arguments, string? workingDirectory, CancellationToken cancellationToken)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                WorkingDirectory = workingDirectory ?? "",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(startInfo);
            if (process == null)
                return null;

            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            var stdout = await stdoutTask;

            if (process.ExitCode != 0)
                return null;

            var line = stdout?.Trim().Split('\n', '\r').FirstOrDefault()?.Trim();
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
