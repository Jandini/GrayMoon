namespace GrayMoon.App.Models;

/// <summary>Project kind for SDK-style projects (matches Agent CsProjFileInfo).</summary>
public enum ProjectType
{
    Executable = 0,
    Test = 1,
    Service = 2,
    Package = 3,
    Library = 4
}
