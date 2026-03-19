namespace GrayMoon.App.Services;

/// <summary>
/// Raised when synchronized push is requested but required package registry mapping is incomplete.
/// </summary>
public sealed class SynchronizedPushNotPossibleException : Exception
{
    public int MissingPackagesCount { get; }

    public SynchronizedPushNotPossibleException(int missingPackagesCount)
        : base($"Synchronized push is not possible because {missingPackagesCount} required package mapping(s) are missing.")
    {
        MissingPackagesCount = missingPackagesCount;
    }
}

