# Packable projects, props files, and sync cleanup

**Goal:** A project that no longer produces a NuGet package leaves Packages on the next sync, including when `IsPackable` is set in `Directory.Build.props` or `Directory.Build.targets`. The project row stays. Its package identity is cleared.

No startup migration. Existing workspaces update when that workspace or feature is synced, because sync already persists whatever the project scan returns.

---

## 1. What the user sees

After sync of a repository whose projects changed:

- A `.csproj` that is no longer packable stays on Projects as a library. It disappears from Packages.
- A newly added packable `.csproj` appears on Projects and on Packages.
- A `.csproj` that was deleted is removed, as it is today.
- Dependency edges that used the former package id are dropped, and repository dependency levels are recomputed.
- Generated package rows (the ones inferred from version files, with no physical packable project) are left alone.

Each feature context has its own project rows. Syncing the workspace updates the workspace context. A feature updates when that feature is synced. A feature created after the workspace sync is seeded from the cleaned rows.

---

## 2. Why the current scan keeps them

`CsProjFileParser.IsPackagedAsNuGet` treats any non-empty `PackageId` as a package and never consults `IsPackable=false` in that case. The parser also reads only the `.csproj`. It does not see `Directory.Build.props`, `Directory.Build.targets`, or `Import`s.

Sync then stores that result. `MergeProjectsForRepository` updates an existing row in place (`ProjectType`, `PackageId`) when the project name still matches, and Packages lists every row whose `PackageId` is set. A project that the scan still calls a package stays on Packages.

There is a second leak in `MergeWorkspaceProjectDependenciesAsync`. When `PackageId` is empty, the dependency index falls back to `ProjectName`. That name is often the old package id, so consumers would still link to the project after its package id was cleared.

---

## 3. Packable rule

Classify executable, test, and web projects first, exactly as today. This change applies only to the library-versus-package decision.

Effective values:

- Explicit `IsPackable=false` means the project is a library, even when the file still contains `PackageId`.
- Explicit `IsPackable=true` means the project is a package, even with no `PackageId`. The stored package id stays the explicit `PackageId`, or the project name when `PackageId` is absent.
- When `IsPackable` is unset, a non-empty `PackageId` still means the project is a package. No `PackageId` still means a library.

Do not adopt the SDK default that every `Microsoft.NET.Sdk` class library is packable. That would put ordinary libraries on Packages.

Keep the current project name: explicit `PackageId` element, then `AssemblyName`, then the file name. Sync matches rows by that name, so the existing row is updated in place. Clearing the stored `PackageId` must not rename the row.

`GeneratePackageOnBuild` is not a substitute for `IsPackable`.

---

## 4. Where `IsPackable` and `PackageId` are read

Resolve both properties in the Worker while parsing the project. Pass the repository root in so the walk cannot leave the repo. A `Directory.Build.props` above the repository root is out of scope.

Apply layers in this order. A later unconditional value replaces an earlier one. Inside one file, property groups apply in document order.

1. The nearest `Directory.Build.props`. Walk from the project directory up to the repository root and take the first file found. A higher file applies only when this nearest file imports it (section 5).
2. The `.csproj`, in document order, including the simple imports it contains, at the point of each import.
3. The nearest `Directory.Build.targets`, same walk, applied last so it can override the project.

Accepted boolean text is `true` and `false`, case insensitive. A value that is still an unresolved `$(...)` token does not change the property.

When the project file sets `ImportDirectoryBuildProps` or `ImportDirectoryBuildTargets` to false, skip that automatic file.

---

## 5. Imports in this pass

Follow an import when all of these hold:

- `Project` is a literal relative path with no MSBuild property and no wildcard.
- The element has no `Condition`.
- The resolved path stays inside the repository root.
- The depth stays within a small cap, and a file already on the import stack is skipped.

Also follow the usual parent chain inside `Directory.Build.props` and `Directory.Build.targets`, and only that form:

```xml
<Import Project="$([MSBuild]::GetPathOfFileAbove('Directory.Build.props', '$(MSBuildThisFileDirectory)../'))" />
```

The same form with `Directory.Build.targets`. Apply the parent first, then the child, so the nearer file wins.

### TODO, left unimplemented

Document this list on the resolver. Do not call MSBuild.

- Conditional imports.
- Wildcard imports.
- MSBuild property functions other than the `GetPathOfFileAbove` chain above.
- `Choose` / `When` / `Otherwise`.
- Props and targets imported by the SDK or by a NuGet package.
- `Directory.Build.props` that lives outside the repository root.
- Disabling the automatic import through an environment variable or a global MSBuild property rather than the project file.

---

## 6. Cleanup on sync

No new write path and no migration. These existing paths already persist the scan:

- `RefreshRepositoryProjectsCommand` to `MergeWorkspaceProjectsAsync` (project refresh).
- `WorkspaceRepositoryStateWriter.ApplyProjectsAsync` (repository sync).

For a project that is no longer packable, that write sets `ProjectType` to Library and `PackageId` to null. Packages already filters on a non-empty `PackageId`. Repository type is recomputed from the same project list, so a repository whose only package projects were reclassified stops being reported as a package repository.

Change the dependency index in `MergeWorkspaceProjectDependenciesAsync` so a package-reference target is only a project with a non-empty `PackageId`. Do not fall back to `ProjectName`. Generated package rows already have a package id and stay eligible. The same sync removes edges whose target lost its package id and recomputes dependency levels.

---

## 7. Tests

Resolver, on temporary repositories:

- `IsPackable=false` and `PackageId` in the `.csproj` produces a library and a null package id.
- `IsPackable=false` only in `Directory.Build.props`, with `PackageId` in the `.csproj`, produces a library.
- `IsPackable=false` in props and `IsPackable=true` in the `.csproj` produces a package.
- `IsPackable=true` in the `.csproj` and `IsPackable=false` in `Directory.Build.targets` produces a library.
- The nearest `Directory.Build.props` wins over a higher one that the nearest file does not import.
- A `GetPathOfFileAbove` parent chain applies the parent, then the child.
- A new project with `IsPackable=true` and no `PackageId` is still a package.
- `ImportDirectoryBuildProps` set to false in the project ignores `Directory.Build.props`.
- An `Import` of a literal relative `.props` file inside the repo is applied. A conditional import, a wildcard, and a path outside the repo are ignored.

Persistence:

- An existing package row is the same project after merge: type Library, package id empty, project name unchanged. A new packable project is inserted.
- A dependency edge whose target lost its package id is removed.
- A generated package row is not deleted by the project merge.

---

## 8. Where to change the code

- `GrayMoon.Worker` project parsing (`CsProjFileParser`, and the repository root available from `CsProjFileService` / `RefreshRepositoryProjectsCommand`). New resolver next to the parser. The parser keeps executable, test, and web classification.
- `WorkspaceProjectRepository.MergeWorkspaceProjectDependenciesAsync`: index package targets by `PackageId` only.
- Worker and App tests listed in section 7.

Do not add a Microsoft.Build evaluation package for this pass.
