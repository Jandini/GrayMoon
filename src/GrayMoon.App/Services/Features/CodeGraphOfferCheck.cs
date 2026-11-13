namespace GrayMoon.App.Services.Features;

/// <summary>Whether the Workspace dialog may offer Use CodeGraph.</summary>
public enum CodeGraphOffer
{
    /// <summary>The Worker has not answered the host check yet. The switch is shown but cannot be changed.</summary>
    Checking,

    /// <summary>The Worker reported a CodeGraph CLI.</summary>
    Offered,

    /// <summary>The Worker answered and has no CodeGraph CLI.</summary>
    NotInstalled
}

/// <summary>
/// Reads the host check for the Workspace dialog. A host check without an answer (Worker still connecting, or a
/// failed read) is "not yet", never "CodeGraph is not installed": the dialog keeps asking while it is open.
/// </summary>
public static class CodeGraphOfferCheck
{
    /// <summary>Time between two reads while the Worker has not answered.</summary>
    public static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(2);

    public static CodeGraphOffer From(HostInfoQuery query) =>
        query.Read is null
            ? CodeGraphOffer.Checking
            : query.Read.IsCodeGraphInstalled ? CodeGraphOffer.Offered : CodeGraphOffer.NotInstalled;

    /// <summary>
    /// Reads until the Worker has answered. <paramref name="pause"/> runs between two reads; cancelling
    /// <paramref name="cancellationToken"/> (the dialog closed) ends the wait.
    /// </summary>
    public static async Task<CodeGraphOffer> WaitForAnswerAsync(
        Func<CancellationToken, Task<HostInfoQuery>> read,
        Func<CancellationToken, Task> pause,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var offer = From(await read(cancellationToken));
            if (offer != CodeGraphOffer.Checking)
                return offer;

            await pause(cancellationToken);
        }
    }
}
