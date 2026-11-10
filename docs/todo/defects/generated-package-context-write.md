# Feature sync can remove global generated packages

## What is still open

Generated package rows are workspace-global (special Workspace context). The sync that writes them is still driven by one context:

- `WorkspaceFileVersionService.SyncGeneratedPackageDependenciesAsync` overlays that context's missing-file flags, then skips a version file the context marks missing.
- If no `.csproj` version files remain, it calls `WorkspaceProjectRepository.SyncGeneratedPackageDependenciesAsync` with an empty list. That method deletes generated `WorkspaceProject` rows that are not in the desired set, so a Feature that cannot see the version file removes the global rows.
- The same repository method writes the version resolved for the calling context onto every matching consumer edge, including real projects in other contexts.

## Why it matters

A Feature whose version file is missing can drop the generated package for the whole Workspace. A version resolved in one context can change another context's dependency edge version and unmatched count.

## Source

`GrayMoon/docs/workspace-profiles/Workspace-Profiles-Implementation-Plan.md` (Unit C findings, and the Deferred TODOs table: "Generated-package write scoping across contexts").

## Where architecture is silent

Checked `docs/architecture/01-product-model-and-user-workflows.md` section 14, `03-data-model-and-persistence.md` section 6, `05-user-capability-reference.md` section 32, and `06-developer-extension-guide.md` section 24. Section 32 says generated packages stay global to the Workspace. It does not record this write path.
