namespace GrayMoon.Common;

/// <summary>
/// Async-local extra environment variables applied to every <c>git</c> process started by
/// <see cref="CommandLineService"/> in the current async flow. Used to hand git a credential header via
/// <c>GIT_CONFIG_*</c> so the secret never appears on the process command line.
/// </summary>
public static class GitProcessEnvironmentAmbient
{
    public static readonly AsyncLocal<IReadOnlyDictionary<string, string>?> Current = new();
}

/// <summary>Pushes ambient git environment variables for the current async flow; restores the previous value on dispose.</summary>
public sealed class GitProcessEnvironmentScope : IDisposable
{
    private readonly IReadOnlyDictionary<string, string>? _previous;

    public GitProcessEnvironmentScope(IReadOnlyDictionary<string, string> variables)
    {
        _previous = GitProcessEnvironmentAmbient.Current.Value;
        GitProcessEnvironmentAmbient.Current.Value = variables;
    }

    public void Dispose() => GitProcessEnvironmentAmbient.Current.Value = _previous;
}
