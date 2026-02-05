using GrayMoon.Agent.Models;

namespace GrayMoon.Agent.Abstractions;

/// <summary>Parses SDK-style .csproj files.</summary>
public interface ICsProjFileParser
{
    /// <summary>Parses an SDK-style .csproj file and returns project type, target framework, name, and package references; returns null if the file is missing or invalid.</summary>
    Task<CsProjFileInfo?> ParseAsync(string csprojPath, CancellationToken cancellationToken = default);
}
