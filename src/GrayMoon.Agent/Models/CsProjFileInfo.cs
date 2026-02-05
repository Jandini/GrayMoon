namespace GrayMoon.Agent.Models;

/// <summary>Information from parsing an SDK-style .csproj file.</summary>
public sealed class CsProjFileInfo
{
    /// <summary>Full path to the .csproj file (set when returned from FindAsync).</summary>
    public string? ProjectPath { get; init; }

    /// <summary>Project type: Executable, Test, Service (Web SDK), Package (NuGet package), or Library.</summary>
    public required ProjectType ProjectType { get; init; }

    /// <summary>Target framework(s), e.g. "net8.0" or "net6.0;net8.0" for multi-targeting.</summary>
    public required string TargetFramework { get; init; }

    /// <summary>Project name (PackageId, or AssemblyName, or file name).</summary>
    public required string Name { get; init; }

    /// <summary>NuGet package references declared in the project (Include and Version).</summary>
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
    public required string Name { get; init; }
    public required string Version { get; init; }
}
