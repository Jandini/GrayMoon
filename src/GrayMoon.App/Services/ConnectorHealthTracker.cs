namespace GrayMoon.App.Services;

/// <summary>Tracks connector health for UI (e.g. token decryption issues).</summary>
public sealed class ConnectorHealthTracker
{
    public bool HasTokenDecryptionErrors { get; set; }
}

