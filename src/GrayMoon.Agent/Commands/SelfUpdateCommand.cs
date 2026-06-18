using System.Diagnostics;
using GrayMoon.Agent.Abstractions;
using GrayMoon.Agent.Jobs.Requests;
using GrayMoon.Agent.Jobs.Response;
using Microsoft.Extensions.Logging;

namespace GrayMoon.Agent.Commands;

public sealed class SelfUpdateCommand(ILogger<SelfUpdateCommand> logger) : ICommandHandler<SelfUpdateRequest, SelfUpdateResponse>
{
    public Task<SelfUpdateResponse> ExecuteAsync(SelfUpdateRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.InstallUrl))
            throw new ArgumentException("InstallUrl is required.");

        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -NonInteractive -Command \"irm '{request.InstallUrl}' | iex\"",
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        _ = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start PowerShell process.");

        logger.LogInformation("Started self-update from {InstallUrl}", request.InstallUrl);
        return Task.FromResult(new SelfUpdateResponse());
    }
}
