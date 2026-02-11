# Connector Extension Design Document

## Quick Summary

**Goal**: Extend existing GitHub-only connector system to support three connector types (GitHub, NuGet, Docker) with multiple registry options, while maintaining backward compatibility.

**Key Changes**:
- Add `ConnectorType` enum (GitHub, NuGet, Docker)
- Make `UserToken` nullable (not required for NuGet.org, optional for Docker Hub)
- Unified UI on single `/connectors` page with dynamic form fields
- Service factory pattern for connector-specific operations
- Registry behavior derived from `ConnectorType` + `ApiBaseUrl` (no separate RegistryType enum)
- Backward compatible: existing GitHub connectors continue to work

**Registry Support**:
- **GitHub**: GitHub Packages (NuGet), GitHub Container Registry (Docker)
- **ProGet**: NuGet feeds, Docker feeds (requires token)
- **NuGet.org**: Public NuGet registry (no token)
- **Docker Hub**: Public Docker registry (no token, optional for private)

## Overview
This document outlines the design for extending the existing GitHub connector system to support multiple connector types: **GitHub**, **NuGet**, and **Docker**. The design maintains backward compatibility while providing a unified, user-friendly interface for managing all connector types.

## Requirements

### Connector Types
1. **GitHub** - Existing functionality (repositories, workflows, actions)
2. **NuGet** - Package registry access (GitHub Packages, ProGet, NuGet.org)
3. **Docker** - Container registry access (GitHub Container Registry, ProGet, Docker Hub)

### Registry Support
Each connector points to a specific API base URL:
- **GitHub**: `https://api.github.com/` (for repositories)
- **NuGet**: 
  - GitHub Packages: `https://nuget.pkg.github.com/{owner}/`
  - ProGet: `https://proget.example.com/nuget/{feed-name}/`
  - NuGet.org: `https://api.nuget.org/v3/index.json` (no token required)
- **Docker**: 
  - GitHub Container Registry: `ghcr.io`
  - ProGet: `https://proget.example.com/docker/{feed-name}/`
  - Docker Hub: `docker.io` (no token required for public)

### Authentication Requirements
- **GitHub**: Requires token (Personal Access Token)
- **ProGet**: Requires token (API key)
- **NuGet.org**: No token required (public)
- **Docker Hub**: No token required (public), optional for private repos

## Database Schema Changes

### Connector Model Extensions

```csharp
public enum ConnectorType
{
    GitHub = 1,
    NuGet = 2,
    Docker = 3
}

public class Connector
{
    public int ConnectorId { get; set; }
    
    [Required]
    [MaxLength(100)]
    public string ConnectorName { get; set; } = "GitHub";
    
    [Required]
    public ConnectorType ConnectorType { get; set; } = ConnectorType.GitHub;
    
    [Required]
    [MaxLength(300)]
    public string ApiBaseUrl { get; set; } = "https://api.github.com/";
    
    [MaxLength(100)]
    public string? UserName { get; set; }
    
    // Made nullable - not required for NuGet.org and Docker Hub
    // Token requirement determined by ConnectorType + ApiBaseUrl pattern
    [MaxLength(500)]
    public string? UserToken { get; set; }
    
    [Required]
    [MaxLength(20)]
    public string Status { get; set; } = "Unknown";
    
    public bool IsActive { get; set; } = true;
    
    [MaxLength(1000)]
    public string? LastError { get; set; }
}
```

### Helper Methods for Registry Detection

```csharp
public static class ConnectorHelpers
{
    public static bool RequiresToken(ConnectorType connectorType, string apiBaseUrl)
    {
        return connectorType switch
        {
            ConnectorType.GitHub => true, // Always requires token
            ConnectorType.NuGet => !IsNuGetOrg(apiBaseUrl),
            ConnectorType.Docker => !IsDockerHub(apiBaseUrl),
            _ => true
        };
    }
    
    public static bool IsNuGetOrg(string apiBaseUrl)
    {
        return apiBaseUrl.Contains("nuget.org", StringComparison.OrdinalIgnoreCase);
    }
    
    public static bool IsDockerHub(string apiBaseUrl)
    {
        return apiBaseUrl.Contains("docker.io", StringComparison.OrdinalIgnoreCase) ||
               apiBaseUrl.Contains("dockerhub.com", StringComparison.OrdinalIgnoreCase);
    }
    
    public static string GetDefaultUrl(ConnectorType connectorType, string? registryHint = null)
    {
        return connectorType switch
        {
            ConnectorType.GitHub => "https://api.github.com/",
            ConnectorType.NuGet => registryHint switch
            {
                "github" => "https://nuget.pkg.github.com/",
                "nugetorg" => "https://api.nuget.org/v3/index.json",
                _ => "https://api.nuget.org/v3/index.json" // Default to NuGet.org
            },
            ConnectorType.Docker => registryHint switch
            {
                "github" => "ghcr.io",
                "dockerhub" => "docker.io",
                _ => "docker.io" // Default to Docker Hub
            },
            _ => throw new NotSupportedException($"Connector type {connectorType} not supported")
        };
    }
}
```

### Migration Strategy
1. Add new column with default value:
   - `ConnectorType` defaults to `GitHub` (1)
2. Existing connectors remain functional without changes
3. `UserToken` becomes nullable but existing data remains non-null
4. Registry behavior determined dynamically from `ConnectorType` + `ApiBaseUrl`

## UI/UX Design

### Single Page Approach
The `/connectors` page will be enhanced to support all connector types with a unified interface.

### Connector List View
- Display connector type badge (GitHub, NuGet, Docker)
- Display API base URL (shows which registry/feed)
- Show connection status (same as current)
- Filter/search by connector type
- Group connectors by type (optional enhancement)

### Add/Edit Connector Modal

#### Step 1: Select Connector Type
- Radio buttons or dropdown for:
  - GitHub
  - NuGet
  - Docker

#### Step 2: Configure Connection
- **Connector Name**: Free text (required)
- **API Base URL**: 
  - Pre-filled with default based on connector type
  - For NuGet: Defaults to NuGet.org, but user can change to GitHub Packages or ProGet feed URL
  - For Docker: Defaults to Docker Hub, but user can change to GitHub Container Registry or ProGet feed URL
  - Always editable (user specifies exact feed/registry URL)
- **User Name**: 
  - Optional for GitHub
  - Optional for ProGet
  - Hidden for NuGet.org and Docker Hub (determined from URL pattern)
- **User Token**: 
  - Required for GitHub (always)
  - Required for ProGet (detected from URL pattern)
  - Required for GitHub Packages/Container Registry (detected from URL pattern)
  - Optional for Docker Hub (for private repos, detected from URL pattern)
  - Hidden for NuGet.org (detected from URL pattern)

### Form Validation
- Conditional validation based on connector type + URL pattern
- Token required validation determined dynamically:
  - Always required for GitHub
  - Required if URL contains "proget", "pkg.github.com", or "ghcr.io"
  - Not required if URL contains "nuget.org"
  - Optional if URL contains "docker.io" (for Docker type)
- URL validation: Basic URL format validation

## Service Layer Architecture

### Service Interface Pattern
```csharp
public interface IConnectorService
{
    Task<bool> TestConnectionAsync(Connector connector);
    ConnectorType ConnectorType { get; }
}

public class GitHubService : IConnectorService
{
    public ConnectorType ConnectorType => ConnectorType.GitHub;
    // Existing implementation
}

public class NuGetService : IConnectorService
{
    public ConnectorType ConnectorType => ConnectorType.NuGet;
    // New implementation
}

public class DockerService : IConnectorService
{
    public ConnectorType ConnectorType => ConnectorType.Docker;
    // New implementation
}
```

### Service Factory
```csharp
public class ConnectorServiceFactory
{
    private readonly IServiceProvider _serviceProvider;
    
    public IConnectorService GetService(ConnectorType connectorType)
    {
        return connectorType switch
        {
            ConnectorType.GitHub => _serviceProvider.GetRequiredService<GitHubService>(),
            ConnectorType.NuGet => _serviceProvider.GetRequiredService<NuGetService>(),
            ConnectorType.Docker => _serviceProvider.GetRequiredService<DockerService>(),
            _ => throw new NotSupportedException($"Connector type {connectorType} is not supported.")
        };
    }
}
```

## Default Configuration Values

### GitHub
- **API Base URL**: `https://api.github.com/`
- **Token**: Required (always)

### NuGet
- **Default**: `https://api.nuget.org/v3/index.json` (no token)
- **GitHub Packages**: `https://nuget.pkg.github.com/{owner}/` (requires token)
- **ProGet**: `https://proget.example.com/nuget/{feed-name}/` (requires token, user-provided)

### Docker
- **Default**: `docker.io` (no token for public)
- **GitHub Container Registry**: `ghcr.io` (requires token)
- **ProGet**: `https://proget.example.com/docker/{feed-name}/` (requires token, user-provided)

**Note**: Each connector points to a specific feed/registry URL. For ProGet, multiple connectors can point to different feeds on the same ProGet instance.

## Implementation Plan

### Phase 1: Database & Models
1. Create migration to add `ConnectorType` column
2. Update `Connector` model with `ConnectorType` property
3. Make `UserToken` nullable
4. Create `ConnectorType` enum
5. Create `ConnectorHelpers` static class for registry detection
6. Update `AppDbContext` configuration

### Phase 2: Form Models & Validation
1. Replace `GitHubConnectorForm` with generic `ConnectorForm`
2. Add conditional validation logic based on connector type + URL pattern
3. Create helper methods for default URLs and token requirement detection

### Phase 3: UI Components
1. Update `GitHubConnectors.razor` to `Connectors.razor`
2. Update modal to support all connector types
3. Add connector type selection UI
4. Implement dynamic URL defaults based on connector type
5. Implement conditional field visibility based on connector type + URL pattern
6. Update table to show connector type and API base URL

### Phase 4: Services
1. Create `NuGetService` with test connection logic
2. Create `DockerService` with test connection logic
3. Create `ConnectorServiceFactory`
4. Update connector testing logic to use factory

### Phase 5: Backward Compatibility
1. Ensure existing GitHub connectors continue to work
2. Update any hardcoded references to use connector type
3. Test migration with existing data

## Testing Strategy

### Unit Tests
- Connector form validation for each type
- Service factory resolution
- Default URL generation
- Token requirement detection based on URL patterns
- Registry detection helpers

### Integration Tests
- Create connectors of each type with various URLs
- Test connection for GitHub, ProGet, NuGet.org, Docker Hub
- Verify token optionality based on URL patterns
- Test multiple ProGet feeds (different connectors, same ProGet instance)

### Migration Tests
- Verify existing GitHub connectors work after migration
- Ensure default values are applied correctly

## Future Enhancements

1. **Package/Image Discovery**: Services to list packages/images from registries
2. **Version Tracking**: Track package/image versions
3. **Dependency Analysis**: Analyze NuGet package dependencies
4. **Security Scanning**: Integrate vulnerability scanning for packages/images
5. **Filtering**: Filter connectors by type in UI
6. **Bulk Operations**: Enable/disable multiple connectors at once

## Backward Compatibility Notes

- All existing GitHub connectors will automatically have:
  - `ConnectorType = GitHub`
  - `UserToken` remains non-null (nullable column, but existing data preserved)
  - Registry behavior determined from existing `ApiBaseUrl` (GitHub API)
- No breaking changes to existing API calls
- GitHubService remains unchanged in functionality
- Repository model remains unchanged (still linked to Connector)

## File Structure Changes

```
src/GrayMoon.App/
├── Models/
│   ├── Connector.cs (updated)
│   ├── ConnectorType.cs (new)
│   ├── ConnectorForm.cs (replaces GitHubConnectorForm.cs)
│   ├── ConnectorModalMode.cs (renamed from GitHubConnectorModalMode.cs)
│   └── ConnectorHelpers.cs (new - static helper methods)
├── Components/
│   ├── Pages/
│   │   └── Connectors.razor (renamed from GitHubConnectors.razor)
│   ├── Modals/
│   │   └── ConnectorModal.razor (renamed from GitHubConnectorModal.razor)
│   └── Layout/
│       ├── NavMenu.razor (update icon/text - line 97-98)
│       └── Pages/
│           └── Home.razor (update card title/text - line 73-76)
├── Services/
│   ├── GitHubService.cs (existing, implements IConnectorService)
│   ├── NuGetService.cs (new)
│   ├── DockerService.cs (new)
│   └── ConnectorServiceFactory.cs (new)
└── Data/
    └── Migrations/ (new migration for schema changes)
```

### UI Text Updates Required

1. **Home.razor** (line 73-76):
   - Change "GitHub Connectors" → "Connectors"
   - Change description to: "Configure access to GitHub, NuGet, and Docker registries and manage connector tokens."
   - Consider changing icon from `bi-github` to `bi-plug` or `bi-link-45deg` for generic connector icon

2. **NavMenu.razor** (line 97-98):
   - Change icon from `bi-github` to `bi-plug` or `bi-link-45deg` for generic connector icon
   - Text already says "Connectors" (no change needed)

## Questions & Considerations

1. **Repository Model**: Currently linked to Connector. Should we track which repositories come from which connector type? (Answer: Yes, but no schema change needed - ConnectorType on Connector is sufficient)

2. **Workspace Integration**: Do workspaces need to know about connector types? (Answer: No, workspaces work with repositories regardless of source)

3. **Token Storage**: Should we encrypt tokens at rest? (Future enhancement)

4. **Rate Limiting**: Different registries have different rate limits. Should we track this? (Future enhancement)

5. **ProGet Custom URLs**: How do we validate ProGet URLs? (Answer: Basic URL validation + connection test)

6. **RegistryType Enum**: Why removed? (Answer: Registry type is determined by the API base URL. Each connector points to a specific feed/registry URL. For ProGet, multiple connectors can point to different feeds. The URL pattern determines authentication requirements, not a separate enum field.)
