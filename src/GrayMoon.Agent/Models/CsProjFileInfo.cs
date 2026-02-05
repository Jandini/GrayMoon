using System.Text.Json.Serialization;

namespace GrayMoon.Agent.Models;

/// <summary>Information from parsing an SDK-style .csproj file.</summary>
public sealed class CsProjFileInfo
{
    /// <summary>Path to the .csproj file relative to the repository root (set when returned from FindAsync).</summary>
    [JsonPropertyName("projectPath")]
    public string? ProjectPath { get; init; }

    /// <summary>Project type: Executable, Test, Service (Web SDK), Package (NuGet package), or Library.</summary>
    [JsonPropertyName("projectType")]
    public required ProjectType ProjectType { get; init; }

    /// <summary>Target framework(s), e.g. "net8.0" or "net6.0;net8.0" for multi-targeting.</summary>
    [JsonPropertyName("targetFramework")]
    public required string TargetFramework { get; init; }

    /// <summary>Project name (PackageId when present and used, otherwise AssemblyName or file name).</summary>
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    /// <summary>NuGet package ID when project type is Package (explicit or project name).</summary>
    [JsonPropertyName("packageId")]
    public string? PackageId { get; init; }

    /// <summary>NuGet package references declared in the project (Include and Version).</summary>
    [JsonPropertyName("packageReferences")]
    public required IReadOnlyList<NuGetPackageReference> PackageReferences { get; init; }
}

/// <summary>Project kind for SDK-style projects.</summary>
public enum ProjectType
{
    /// <summary>Executable (OutputType Exe or WinExe).</summary>
    Executable,

    /// <summary>Test project (references a test SDK).</summary>
    Test,

    /// <summary>Service / web app (Sdk=Microsoft.NET.Sdk.Web).</summary>
    Service,

    /// <summary>NuGet package project (class library with packaging intent).</summary>
    Package,

    /// <summary>Class library not packaged as NuGet.</summary>
    Library
}

/// <summary>A NuGet package reference (name and version as declared in the project).</summary>
public sealed class NuGetPackageReference
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("version")]
    public required string Version { get; init; }
}
