using System.Diagnostics;
using GrayMoon.Worker.Abstractions;
using GrayMoon.Worker.Jobs.Requests;
using GrayMoon.Worker.Jobs.Response;
using Microsoft.Extensions.Logging;

namespace GrayMoon.Worker.Commands;

public sealed class SelfUpdateCommand(ILogger<SelfUpdateCommand> logger) : ICommandHandler<SelfUpdateRequest, SelfUpdateResponse>
{
    /// <summary>Set by the hidden self-update PowerShell so the install script does not wait for a password.</summary>
    internal const string NonInteractiveVariable = "GRAYMOON_WORKER_NONINTERACTIVE";

    internal static string BuildLaunchArguments(string installUrl) =>
        "/c start \"\" /b powershell.exe -NoProfile -NonInteractive -Command \""
        + "$env:" + NonInteractiveVariable + "='1'; irm '" + installUrl + "' | iex\"";

    public Task<SelfUpdateResponse> ExecuteAsync(SelfUpdateRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.InstallUrl))
            throw new ArgumentException("InstallUrl is required.");

        if (!OperatingSystem.IsWindows())
            throw new NotSupportedException("Self-update via the badge is only supported on Windows. Run the install script manually on this platform.");

        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = BuildLaunchArguments(request.InstallUrl),
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        _ = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start PowerShell process.");

        logger.LogInformation("Started self-update from {InstallUrl}", request.InstallUrl);
        return Task.FromResult(new SelfUpdateResponse());
    }
}
