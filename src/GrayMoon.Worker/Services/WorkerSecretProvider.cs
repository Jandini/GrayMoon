using Microsoft.Extensions.Logging;

namespace GrayMoon.Worker.Services;

/// <summary>Supplies the shared secret the Worker presents to the App on the hub connection and on the
/// connector token request (F2). Null means this Worker was installed before secrets existed.</summary>
public interface IWorkerSecretProvider
{
    string? GetSecret();
}

/// <summary>
/// Resolution order: the <c>GRAYMOON_WORKER_SECRET</c> environment variable, then the secret file
/// (<c>worker.secret</c> under the machine-wide GrayMoon data folder, written by the install flow).
/// The file lives outside the install folder so an update, which wipes that folder, keeps the secret.
/// </summary>
internal sealed class WorkerSecretProvider(ILogger<WorkerSecretProvider> logger) : IWorkerSecretProvider
{
    public const string EnvironmentVariableName = "GRAYMOON_WORKER_SECRET";
    public const string FileName = "worker.secret";

    public static string DefaultFilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "GrayMoon",
        FileName);

    public string? GetSecret() => Resolve(Environment.GetEnvironmentVariable, DefaultFilePath, logger);

    internal static string? Resolve(Func<string, string?> getEnvironmentVariable, string filePath, ILogger? logger = null)
    {
        var fromEnvironment = getEnvironmentVariable(EnvironmentVariableName)?.Trim();
        if (!string.IsNullOrEmpty(fromEnvironment))
            return fromEnvironment;

        try
        {
            if (!File.Exists(filePath))
                return null;

            var fromFile = File.ReadAllText(filePath).Trim();
            return string.IsNullOrEmpty(fromFile) ? null : fromFile;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Could not read the worker secret file at {FilePath}; connecting without a secret.", filePath);
            return null;
        }
    }
}
