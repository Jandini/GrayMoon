namespace GrayMoon.Abstractions.Agent;

/// <summary>
/// HTTP header the Worker sends on the hub connection and on the connector token request so the App
/// can tell the real Worker from any other local program (F2).
/// </summary>
public static class WorkerSecretHeader
{
    public const string Name = "X-GrayMoon-Worker-Secret";
}
