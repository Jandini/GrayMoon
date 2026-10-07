namespace GrayMoon.Worker.Tests;

/// <summary>A throw-away workspace root with <c>workspace/repository</c> folders already created, for tests that stub git but still need the folders to exist.</summary>
internal sealed class TempWorkspaceRoot : IDisposable
{
    public TempWorkspaceRoot(string workspaceName, string repositoryName)
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "graymoon-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(System.IO.Path.Combine(Path, workspaceName, repositoryName));
    }

    public string Path { get; }

    public void Dispose()
    {
        try { Directory.Delete(Path, true); }
        catch { /* best-effort */ }
    }
}
