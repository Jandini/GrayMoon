namespace GrayMoon.Worker.Services;

/// <summary>
/// Checks once, at Worker start, that the native libraries the repository code needs can load. A torn install
/// (files missing after two installs ran at once) otherwise shows up as a failed type initializer on every
/// later repository call. A failed initializer stays cached for the life of the process, so one check is enough.
/// </summary>
public interface IWorkerInstallProbe
{
    /// <summary>Null when the install is intact; otherwise a short description of what failed to load.</summary>
    string? Check();
}

public sealed class WorkerInstallProbe : IWorkerInstallProbe
{
    private readonly Lazy<string?> _result = new(Probe);

    public string? Check() => _result.Value;

    private static string? Probe()
    {
        try
        {
            // Reading the version runs the type initializer of LibGit2Sharp's native methods, which loads git2-*.dll.
            _ = LibGit2Sharp.GlobalSettings.Version;
            return null;
        }
        catch (Exception ex)
        {
            return $"{ex.GetType().Name}: {ex.InnerException?.Message ?? ex.Message}";
        }
    }
}
