namespace GrayMoon.App.Models;

public enum ConnectorType
{
    GitHub = 1,
    NuGet = 2,

    /// <summary>A NuGet feed on a ProGet server. Read like a NuGet connector, signed in the ProGet way.</summary>
    ProGet = 3
}
