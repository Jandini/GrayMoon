# GrayMoon Workspace as a Git Repository

**Status:** Design proposal  
**Target:** Post workspace-profiles implementation  
**Code baseline reviewed:** `Jandini/GrayMoon` default branch at `a6d951d8ffd8713b36d79ff6b5ea6a2fc60b6843`  
**Date:** 2026-10-06

---

## 1. Summary

GrayMoon should allow a Workspace itself to be backed by a normal Git repository.

The Workspace repository is not a replacement for the repositories contained in the Workspace. It is the Git home for the **Workspace definition and Workspace-level files**:

- `.graymoon.json`
- `.gitignore`
- `CLAUDE.md`
- `AGENTS.md`
- Workspace documentation
- prompts and AI instructions
- scripts
- shared configuration
- other files that belong to the development Workspace as a whole rather than to one product repository

The Workspace repository should be represented inside GrayMoon as a **first-class Workspace repository** so it benefits from the same repository infrastructure as every other repository:

- branches and tags
- Git Changes
- commits
- pull and push
- pull requests
- GitHub integration
- GitHub Actions when enabled
- Features/worktrees
- context-scoped Git state

It is special only in **role and physical location**:

- its working tree is the Workspace root itself;
- ordinary repositories remain children of that root and are ignored by the Workspace repository;
- at most one repository in a Workspace has the `Workspace` role.

This design deliberately does **not** add another `WorkspaceType`.

Workspace Git backing is orthogonal to the already-implemented Workspace profile axes:

```text
WorkspaceType            Basic | DotNetDependency
WorkspaceVersioningMode  None  | GitVersion
WorkspaceCiProvider      None  | GitHubActions
```

A Basic Workspace can be Git-backed.  
A .NET Dependency Workspace can be Git-backed.  
GitVersion and CI remain independent.

The Workspace profile describes **what GrayMoon does with repositories**.  
The Workspace repository describes **how the Workspace definition itself is versioned, shared and branched**.

A critical invariant of this design is:

> **If a Workspace repository exists, it is always part of every Feature.**

There is no opt-out in Feature creation.

This guarantees that Workspace-level AI rules and other shared files are present, live, editable, tracked, committable and mergeable in every Feature.

---

## 2. Design goals

The design should achieve the following:

1. Give Workspace-level files a real Git home.
2. Keep Source repositories independent rather than turning the Workspace into a monorepo.
3. Make the Workspace portable between computers.
4. Keep the manifest independent of GrayMoon local persistence.
5. Reuse the existing repository, Feature, Git Changes, PR and Actions infrastructure.
6. Preserve the implemented Workspace profile architecture.
7. Avoid introducing a parallel Workspace-Git subsystem.
8. Keep secrets and machine-specific state out of the manifest.

---

## 3. Why the design changes now

The original Workspace-as-Git concept predated Workspace profiles and the current Feature/worktree architecture.

The current codebase now has several architectural boundaries that the design must preserve.

### 3.1 Workspace profiles are already modeled correctly

`Workspace` persists three independent axes:

- `WorkspaceType`
- `WorkspaceVersioningMode`
- `WorkspaceCiProvider`

`WorkspaceCapabilities` derives behavior from those values, and the code intentionally avoids scattering direct type checks through low-level Git code.

Workspace Git backing should follow the same principle:

> Do not encode "Git-backed Workspace" as a new Workspace type and do not overload `WorkspaceType.Basic`.

Git backing is a storage/version-control characteristic, not a dependency-processing profile.

### 3.2 A Workspace repository already fits the current repository abstraction

The existing model separates:

```text
Repository
    global repository identity / connector / repository URL

WorkspaceRepositoryLink
    membership of that repository in one Workspace
    plus the Workspace context's Git state
```

That is exactly what is needed.

The Workspace Git repository should still be a `Repository` with a `WorkspaceRepositoryLink`.

GrayMoon should add a **role on the link**, rather than inventing a parallel repository abstraction.

### 3.3 Features already model per-repository worktrees

A Feature contains a `WorkspaceFeatureRepository` row for each repository participating in the Feature, including:

- `WorktreePath`
- `BaseCommitSha`
- `ParentBranchName`
- `PinnedTag`

The current implementation creates a Feature worktree for every Workspace repository.

The new Workspace repository should follow that same invariant.

If it exists, it participates in every Feature.

### 3.4 Context isolation remains a hard architectural rule

The current code has a strong distinction between:

- the special Workspace context; and
- Feature contexts.

Repository state belonging to a Feature must not leak into the Workspace context.

The Workspace repository obeys the same rule and must not introduce a second context model.

---

## 4. Terminology

### Workspace

The GrayMoon logical Workspace.

Example:

```text
AVR
```

It has:

- a root folder;
- a profile;
- a set of repository links;
- a special Workspace context;
- zero or more Feature contexts.

### Workspace repository

The optional Git repository whose working tree is the Workspace root.

Example:

```text
C:\Workspace\AVR\.git
C:\Workspace\AVR\.graymoon.json
C:\Workspace\AVR\CLAUDE.md
```

This is a normal GrayMoon `Repository` linked to the Workspace with role `Workspace`.

### Source repository

Any ordinary repository linked to the Workspace.

Example:

```text
C:\Workspace\AVR\Avr.Api
C:\Workspace\AVR\Avr.Web
C:\Workspace\AVR\Avr.Shared
```

In the data model these remain normal `WorkspaceRepositoryLink` rows with role `Source`.

### Workspace manifest

The machine-readable Workspace definition stored in the Workspace repository:

```text
.graymoon.json
```

### Connector

A portable description of the external Git hosting endpoint used by one or more repositories.

Examples:

```text
https://github.com
https://github.company.com
```

The manifest stores connector identity/configuration, but never credentials.

### Repository URL

The portable external Git repository location.

Field name:

```text
repositoryUrl
```

Example:

```text
https://github.com/example/Avr.Api.git
```

### Workspace context

The existing special `WorkspaceFeatureContextKind.Workspace` context.

Do not confuse "Workspace context" with "Workspace repository".

---

## 5. Core invariants

### 5.1 At most one Workspace repository

A Workspace has:

```text
0 or 1 Workspace-role repository
0..N Source-role repositories
```

### 5.2 The Workspace repository owns the Workspace root

If present:

```text
Workspace repository working tree == Workspace root
```

### 5.3 Source repositories remain children

```text
Source repository working tree == Workspace root / repository name
```

### 5.4 The Workspace repository always participates in Features

If the Workspace has a Workspace-role repository, every Feature has a corresponding:

```text
WorkspaceFeatureRepository
```

for that link.

There is no Feature-level opt-out.

### 5.5 Feature root mirrors Workspace root

If a Workspace repository exists:

```text
Feature root == Workspace-repository worktree root
```

and Source repository worktrees live beneath it.

### 5.6 `.graymoon.json` is authoritative only in the special Workspace context

A Feature copy of `.graymoon.json` is:

- tracked;
- visible in Git Changes;
- committable;
- reviewable in PRs;

but it is **not interpreted as active runtime Workspace configuration**.

### 5.7 The manifest contains no GrayMoon persistence identity

Never serialize:

```text
WorkspaceId
RepositoryId
WorkspaceRepositoryId
ConnectorId
WorkspaceFeatureContextId
GitHubRepositoryId
NodeId
```

or equivalent local/internal IDs.

The manifest describes external resources and portable Workspace configuration only.

---

## 6. Filesystem model

### 6.1 Git-backed Workspace

```text
C:\Workspace\AVR\                   <-- Workspace repository working tree
│
├── .git\
├── .graymoon.json
├── .gitignore
├── CLAUDE.md
├── AGENTS.md
├── README.md
├── docs\
├── prompts\
│
├── Avr.Api\                        <-- Source repository
│   └── .git\
│
├── Avr.Web\                        <-- Source repository
│   └── .git\
│
└── Avr.Shared\                     <-- Source repository
    └── .git\
```

The Workspace repository owns the root.

The Source repositories are independent Git repositories and must be ignored by the Workspace repository.

### 6.2 Existing non-Git-backed Workspace

This remains valid:

```text
C:\Workspace\AVR\
├── Avr.Api\
├── Avr.Web\
└── Avr.Shared\
```

Git backing remains optional at Workspace level.

No existing Workspace should be forced to convert.

---

## 7. Data model

### 7.1 Do not add a Workspace type

Rejected:

```csharp
WorkspaceType.GitBacked
WorkspaceType.WorkspaceRepository
```

These mix unrelated concerns.

### 7.2 Add repository role to `WorkspaceRepositoryLink`

Recommended:

```csharp
public enum WorkspaceRepositoryRole
{
    Source = 0,
    Workspace = 1
}
```

Add:

```csharp
public WorkspaceRepositoryRole Role { get; set; }
    = WorkspaceRepositoryRole.Source;
```

to `WorkspaceRepositoryLink`.

Reasons to put the role on the link rather than `Repository`:

1. "Workspace repository" is a relationship to one Workspace.
2. The same underlying Git repository could theoretically be used elsewhere as an ordinary repository.
3. Existing repository identity remains external-resource-centric and reusable.
4. Feature and Workspace code already operates on `WorkspaceRepositoryLink`.

### 7.3 Uniqueness

Enforce:

```text
max one Role == Workspace per WorkspaceId
```

in application logic and, where practical, persistence.

For SQLite a filtered unique index is suitable:

```sql
CREATE UNIQUE INDEX ...
ON WorkspaceRepositories(WorkspaceId)
WHERE Role = 1;
```

Application validation remains necessary for user-friendly errors.

### 7.4 Do not duplicate Git state on `Workspace`

Do not add fields such as:

```text
Workspace.WorkspaceGitBranch
Workspace.WorkspaceGitVersion
Workspace.WorkspaceGitStatus
```

The Workspace repository's state flows through:

```text
WorkspaceRepositoryLink
WorkspaceRepositoryContextState
WorkspaceRepositoryContextPullRequest
WorkspaceRepositoryContextAction
Workspace Git Changes projections
```

just like every other repository.

---

## 8. Relationship with Workspace profiles

The Workspace repository participates in the existing profile capability system.

Its Workspace role affects some behavior, but does not create a new profile.

### 8.1 `.NET Dependency` discovery

The Workspace repository root contains child repositories.

GrayMoon must not recursively discover child repositories' `.csproj` files as if they belong to the Workspace repository.

Therefore:

> The Workspace-role repository never participates in `.csproj` project discovery or NuGet dependency production, even when the Workspace type is `DotNetDependency`.

This should be decided at an orchestration boundary using repository role.

Do not teach low-level project parsing what a Workspace repository is.

### 8.2 GitVersion

The Workspace repository may use GitVersion if:

```text
WorkspaceVersioningMode == GitVersion
```

There is no architectural reason to disable it.

### 8.3 CI

The Workspace repository may use GitHub Actions if:

```text
WorkspaceCiProvider == GitHubActions
```

It is a normal GitHub repository for CI purposes.

---

## 9. Manifest design principles

The Workspace repository root contains:

```text
.graymoon.json
```

The manifest is the portable, declarative Workspace definition.

It is intentionally **GrayMoon-installation-agnostic**.

It should be possible to:

1. clone the Workspace repository on another computer;
2. read `.graymoon.json`;
3. understand what external connectors and repositories the Workspace needs;
4. recreate equivalent local GrayMoon persistence from those external identities.

### 9.1 What belongs in the manifest

Portable configuration such as:

- Workspace name;
- Workspace profile;
- connector definitions;
- repository names;
- repository URLs;
- connector URLs;
- future portable version-file configuration;
- future non-secret Workspace settings.

### 9.2 What does not belong in the manifest

Never persist:

- GrayMoon database IDs;
- provider-specific numeric repository IDs;
- provider node IDs;
- access tokens;
- refresh tokens;
- passwords;
- Worker secrets;
- cached Git state;
- branch status;
- Feature runtime state;
- machine-local IDs;
- GrayMoon-internal persistence keys.

### 9.3 External identity rather than internal identity

The manifest answers:

```text
What external resource is this?
```

GrayMoon's local database answers:

```text
How does this installation represent and authenticate to it?
```

Those concerns must stay separate.

---

## 10. Connector manifest model

Connectors may be stored in the manifest because they are part of the portable Workspace definition.

They contain no tokens.

Recommended shape:

```json
{
  "connectors": [
    {
      "type": "github",
      "url": "https://github.com"
    }
  ]
}
```

GitHub Enterprise example:

```json
{
  "connectors": [
    {
      "type": "github",
      "url": "https://github.company.com"
    }
  ]
}
```

### 10.1 Connector identity

Portable matching key:

```text
normalized connector URL
```

Examples:

```text
https://github.com
https://github.company.com
```

Do not use:

```text
ConnectorId
connector display name
```

as identity.

Display names can change and local IDs are installation-specific.

### 10.2 Connector credentials

Never serialize credentials.

On restore:

```text
manifest connector URL
        ↓
find local Connector by normalized URL
        ↓
found?
 ├─ yes -> use local connector and credentials
 └─ no  -> ask user to connect/create it
```

### 10.3 Connector type

`type` identifies the connector implementation/provider family.

Examples:

```text
github
```

In future:

```text
gitlab
azureDevOps
bitbucket
```

The URL remains the external endpoint identity.

---

## 11. Repository manifest model

Use:

```text
repositoryUrl
```

rather than `cloneUrl`.

`cloneUrl` describes one Git operation and sounds implementation-specific.

`repositoryUrl` better describes the external repository resource.

Recommended shape:

```json
{
  "name": "Avr.Api",
  "repositoryUrl": "https://github.com/example/Avr.Api.git",
  "connectorUrl": "https://github.com"
}
```

### 11.1 Repository identity

Portable matching key:

```text
normalized repositoryUrl
```

No provider repository ID is required.

### 11.2 Why IDs are intentionally omitted

Even if an external provider has stable repository IDs, GrayMoon does not need them in the Workspace manifest.

The manifest should not depend on:

- GrayMoon persistence;
- provider-specific metadata;
- one provider's API model.

The external repository URL is sufficient as the portable resource locator and matching key for this design.

### 11.3 Connector relationship

A repository references its connector through:

```text
connectorUrl
```

This is explicit and portable.

Do not store a local `ConnectorId`.

### 11.4 `connectorUrl` and `repositoryUrl`

They are different fields because they identify different things.

Example:

```json
{
  "connectorUrl": "https://github.com",
  "repositoryUrl": "https://github.com/example/Avr.Api.git"
}
```

Conceptually:

```text
connectorUrl  = external Git service endpoint
repositoryUrl = specific Git repository
```

The connector URL is often the host/base endpoint and therefore often a prefix of an HTTPS repository URL, but GrayMoon should not rely on naive string-prefix matching.

For example, a repository may be cloned over SSH:

```text
git@github.com:example/Avr.Api.git
```

while the connector remains:

```text
https://github.com
```

Therefore connector association stays explicit in the manifest.

---

## 12. Recommended initial manifest schema

```json
{
  "schemaVersion": 1,
  "workspace": {
    "name": "AVR",
    "profile": {
      "type": "dotNetDependency",
      "versioning": "gitVersion",
      "ci": "githubActions"
    }
  },
  "connectors": [
    {
      "type": "github",
      "url": "https://github.com"
    }
  ],
  "repositories": [
    {
      "name": "Avr.Api",
      "repositoryUrl": "https://github.com/example/Avr.Api.git",
      "connectorUrl": "https://github.com"
    },
    {
      "name": "Avr.Web",
      "repositoryUrl": "https://github.com/example/Avr.Web.git",
      "connectorUrl": "https://github.com"
    }
  ]
}
```

### 12.1 Do not list the Workspace repository in `repositories`

The repository containing `.graymoon.json` is inherently the Workspace repository.

The `repositories` array contains Source repositories only.

### 12.2 Why explicit connectors are useful

This allows the manifest to describe:

```text
which external systems the Workspace depends on
```

without containing any credentials.

It also allows multiple connector endpoints later.

Example:

```json
{
  "connectors": [
    {
      "type": "github",
      "url": "https://github.com"
    },
    {
      "type": "github",
      "url": "https://github.company.com"
    }
  ]
}
```

Repositories can then explicitly reference the correct connector URL.

---

## 13. URL normalization and matching

Matching should be based on normalized external URLs.

### 13.1 Connector normalization

Examples of normalization decisions:

- scheme and host casing normalized;
- trailing slash removed;
- default port normalized where appropriate;
- path normalization for provider endpoint conventions.

Example:

```text
https://GitHub.com/
https://github.com
```

should normally match.

### 13.2 Repository normalization

Repository URLs may differ syntactically while identifying the same repository.

Examples:

```text
https://github.com/example/Avr.Api.git
https://github.com/example/Avr.Api
git@github.com:example/Avr.Api.git
```

GrayMoon should have one centralized repository-URL normalization/matching service rather than ad hoc comparisons.

Important:

> Do not normalize by destructive string manipulation at call sites.

Provider-aware URL parsing may be required.

### 13.3 Matching sequence on restore

For connectors:

```text
normalized connectorUrl
        ↓
local Connector
```

For repositories:

```text
normalized repositoryUrl
        ↓
local Repository
```

If no local repository record exists:

```text
resolve connector
        ↓
discover/import repository
        ↓
create local Repository row
```

The local IDs are generated only after matching/import.

---

## 14. Paths

The current code assumes:

```text
<workspace root>/<RepositoryName>
```

Keep that invariant in schema v1.

Do not add arbitrary Source repository relative paths unless a separate feature requires them.

This keeps restore compatible with the current Worker path model.

---

## 15. `.graymoon.json` and Features

This file needs explicit semantics.

### 15.1 Physical behavior

Because the Workspace repository always participates in every Feature, the Feature's Workspace-repository worktree naturally contains:

```text
.graymoon.json
```

Do not use sparse checkout or `skip-worktree` merely to hide it.

### 15.2 Runtime authority

Only this file is authoritative:

```text
special Workspace context / .graymoon.json
```

This file is not authoritative:

```text
Feature context / .graymoon.json
```

### 15.3 Feature copy behavior

Inside a Feature, `.graymoon.json` is a normal tracked Git file.

It may:

- appear in Git Changes;
- be edited;
- be committed;
- be pushed;
- be reviewed in a Workspace-repository PR;
- be merged to the parent Workspace-repository branch.

But GrayMoon does **not** apply its contents to:

- Workspace membership;
- Workspace profile;
- connector configuration;
- local runtime state.

### 15.4 Proposed Workspace-definition changes

A Feature may propose a Workspace-definition change.

Example:

```diff
 repositories:
   - Avr.Api
   - Avr.Web
+  - Avr.Notifications
```

GrayMoon treats this as:

```text
proposed configuration change
```

not:

```text
immediate Feature-local repository membership mutation
```

### 15.5 After merge

When the Workspace-repository change reaches the authoritative parent branch and the special Workspace context checks out or pulls that branch, GrayMoon can detect that `.graymoon.json` changed and offer reconciliation.

---

## 16. `.gitignore` ownership

The Workspace repository must ignore Source repositories.

Recommended generated section:

```gitignore
# <graymoon-repositories>
/Avr.Api/
/Avr.Web/
/Avr.Shared/
# </graymoon-repositories>
```

GrayMoon owns only this section.

User-authored rules outside it must be preserved.

### 16.1 Update rules

When authoritative Workspace Source repository membership changes:

1. regenerate the GrayMoon section;
2. preserve user lines outside it;
3. write only if content actually changed.

### 16.2 Workspace repository is never ignored

Only:

```text
Role == Source
```

is emitted.

### 16.3 Feature behavior

Because the Workspace repository Feature worktree contains the same `.gitignore`, Source repository worktrees beneath the Feature root are ignored naturally.

---

## 17. Path resolution

Today `WorkspaceContextPathResolver.GetRepositoryPathAsync` effectively assumes:

```text
repository path = context root + repository name
```

That remains correct for Source repositories.

It is wrong for the Workspace repository.

### 17.1 Special Workspace context

Workspace role:

```text
repository path = Workspace context root
```

Source role:

```text
repository path = Workspace context root / repository name
```

### 17.2 Feature context

Workspace role:

```text
repository path = Feature root
```

Source role:

```text
repository path = persisted WorkspaceFeatureRepository.WorktreePath
```

The path resolver remains the single source of truth.

Callers must not reproduce these rules.

---

## 18. Features

The Workspace repository is always part of every Feature.

This is a hard invariant.

### 18.1 No checkbox

Do not add:

```text
[ ] Include Workspace repository
```

to Feature creation.

### 18.2 Why

The Workspace repository is likely to contain:

- `CLAUDE.md`;
- `AGENTS.md`;
- AI prompts;
- architecture instructions;
- scripts;
- Workspace-level documentation.

A Feature should see the same Workspace context as the base Workspace.

If those files are modified by an AI or developer in the Feature, the changes must:

- remain isolated to the Feature branch;
- appear in Git Changes;
- be committable;
- be pushable;
- be reviewable;
- be mergeable back to the Workspace repository parent branch.

A real Workspace-repository worktree provides this naturally.

---

## 19. Feature filesystem shape

### 19.1 Workspace without Workspace repository

Current behavior remains:

```text
<ManagedFeatureStorageRoot>\<FeatureName>\
├── Avr.Api\
├── Avr.Web\
└── Avr.Shared\
```

### 19.2 Workspace with Workspace repository

The Feature root itself becomes the Workspace repository worktree:

```text
<ManagedFeatureStorageRoot>\<FeatureName>\    <-- Workspace repo worktree
│
├── .git
├── .graymoon.json
├── .gitignore
├── CLAUDE.md
├── AGENTS.md
├── prompts\
├── docs\
│
├── Avr.Api\                                  <-- Source repo worktree
├── Avr.Web\                                  <-- Source repo worktree
└── Avr.Shared\                               <-- Source repo worktree
```

This mirrors the normal Workspace exactly.

---

## 20. Feature creation ordering

With a Workspace repository:

1. resolve HEAD/branch/tag snapshot for every Workspace repository;
2. persist Feature/context/intent rows;
3. create Workspace-repository worktree at `featureRootPath`;
4. after it succeeds, create all Source repository worktrees beneath it using existing bounded parallel fan-out;
5. seed Feature projections;
6. mark Feature Ready.

Without a Workspace repository:

1. create Feature root directory;
2. create Source repository worktrees in parallel;
3. seed projections.

### 20.1 Root creation failure

If Workspace-repository worktree creation fails:

- do not create Source worktrees;
- persist repairable state;
- mark Feature `NeedsRepair`.

---

## 21. Feature removal ordering

When a Workspace repository exists:

1. remove Source repository worktrees;
2. clean their state;
3. remove Workspace-repository worktree last;
4. clean remaining Feature state.

Never remove the root worktree before child worktrees.

---

## 22. Feature branch and tag behavior

The Workspace repository behaves like every other Feature repository.

### 22.1 Branch

At Feature creation:

```text
ParentBranchName = current branch
BaseCommitSha     = current HEAD
Feature branch    = Feature name
```

### 22.2 Tag

Reuse current pinned-tag behavior:

```text
PinnedTag         = checked-out tag
ParentBranchName  = null
Feature worktree  = detached
Feature branch    = none
```

### 22.3 Parent branch remains provenance

`ParentBranchName` remains:

- provenance;
- PR target suggestion.

Feature removal must not restore or switch the base Workspace repository.

---

## 23. Pull requests

The Workspace repository uses the same PR model as every other repository.

Workspace-level changes such as:

```text
CLAUDE.md
AGENTS.md
docs/
prompts/
.graymoon.json
```

can be committed and pushed on the Workspace repository's Feature branch and opened as a PR.

Example:

```text
Workspace repository branch before Feature: developer/john
Feature branch:                             improve-auth
ParentBranchName:                           developer/john
```

PR target should naturally default to:

```text
developer/john
```

---

## 24. Git Changes

The Workspace repository participates fully in existing Git Changes.

Workspace-level files are simply files in that repository.

No hidden/synthetic Workspace Git source should be created.

This provides:

- staged/unstaged state;
- diffs;
- commit;
- file counts;
- Feature context isolation;
- normal history.

---

## 25. Workspace repository UI

Show the Workspace repository as a first-class row.

Example:

```text
AVR.Workspace      [Workspace]
Avr.Api
Avr.Web
Avr.Shared
```

It receives normal actions where valid:

- Sync
- branch/tag state
- Switch Branch
- Create Branch
- Git Changes
- commit
- pull
- push
- PR
- Actions
- open repository

### 25.1 Dependency presentation

In `.NET Dependency` Workspaces, do not show Workspace repository as:

```text
Level 0
0 dependencies
0 projects
```

Those concepts are not applicable.

---

## 26. Manifest synchronization

There are two sources of state:

```text
GrayMoon database
.graymoon.json
```

They serve different roles.

### 26.1 Normal local operation

The GrayMoon database remains the runtime source of truth.

When portable authoritative Workspace settings change, GrayMoon updates the special Workspace context's `.graymoon.json`.

Examples:

- Source repository added;
- Source repository removed;
- Workspace profile changed;
- connector definition changed;
- future portable settings changed.

The manifest modification appears naturally in Git Changes.

GrayMoon must never auto-commit it.

### 26.2 Feature manifests

Feature copies are not runtime sources of truth.

A Feature may change its `.graymoon.json`, but that remains proposal state.

### 26.3 External authoritative edits

If the special Workspace context's `.graymoon.json` changes because of:

- pull;
- branch switch;
- external edit;

GrayMoon should detect and offer reconciliation.

MVP:

```text
Workspace definition changed on disk.
Review changes.
```

Do not silently add/remove repositories or connectors.

### 26.4 Deterministic writes

Manifest writes should:

1. build canonical model;
2. serialize deterministically;
3. compare existing content;
4. write only when changed.

---

## 27. Adding a repository from inside a Feature

This is not part of the first implementation.

A Feature may modify `.graymoon.json` to propose:

```text
+ Source repository
- Source repository
```

GrayMoon does not dynamically mutate Feature membership from that change.

It does not:

- clone the new repo into the Feature;
- create Feature branch/worktree for it;
- mutate runtime Workspace membership;
- alter dependency graph membership.

A future feature can explicitly support:

```text
Workspace changes proposed by this Feature

+ Avr.Notifications

[Apply changes to this Feature]
```

but that requires separate design.

---

## 28. Cross-computer restore

This is a primary goal.

### 28.1 Desired flow

On computer A:

1. configure Workspace;
2. enable Workspace repository;
3. commit `.graymoon.json` and Workspace-level files;
4. push.

On computer B:

1. clone/select Workspace repository;
2. GrayMoon detects `.graymoon.json`;
3. GrayMoon creates local Workspace;
4. restores profile;
5. resolves connectors by normalized `connectorUrl`;
6. resolves repositories by normalized `repositoryUrl`;
7. creates/imports missing local persistence rows;
8. clones missing Source repositories;
9. syncs;
10. Workspace is ready.

### 28.2 No local IDs cross machines

Computer B generates its own:

```text
WorkspaceId
RepositoryId
ConnectorId
WorkspaceRepositoryId
```

These do not need to match computer A.

That is expected and correct.

### 28.3 Connector resolution

For each manifest connector:

```text
normalized connector URL
        ↓
find local connector
        ↓
found?
 ├─ yes -> use it
 └─ no  -> ask user to connect/create
```

### 28.4 Repository resolution

For each repository:

```text
normalized repository URL
        ↓
find local Repository
        ↓
found?
 ├─ yes -> reuse
 └─ no  -> resolve connector and import/discover
```

### 28.5 Lazy discovery

A repository containing:

```text
.graymoon.json
```

can be recognized as a GrayMoon Workspace repository.

Future UX:

```text
Open as Workspace
```

---

## 29. Creating a Git-backed Workspace

### 29.1 New Workspace with local Workspace repository

1. create Workspace;
2. enable Workspace Git repository;
3. initialize Git at Workspace root;
4. create `.graymoon.json`;
5. create managed `.gitignore`;
6. optionally publish/connect remote later.

### 29.2 Convert existing Workspace

1. verify root is suitable;
2. block conversion while Features exist;
3. initialize Git at Workspace root;
4. generate manifest;
5. generate managed `.gitignore`;
6. register/link Workspace repository;
7. mark link `Role = Workspace`.

Never add Source repository content into Workspace repo index.

### 29.3 Restore from existing Workspace repository

1. clone Workspace repository into Workspace root;
2. parse manifest;
3. resolve/create connectors;
4. resolve/create repositories;
5. create Workspace DB model;
6. link Workspace repo as `Role = Workspace`;
7. restore Source repositories.

### 29.4 Attach unrelated remote to non-empty root

Defer initially.

---

## 30. Repository membership changes

Keep current structural protections.

### 30.1 Source membership

Do not change Workspace repository membership while Features exist.

### 30.2 Workspace repository enable/disable

Also blocked while Features exist.

Feature filesystem shape depends on whether the Workspace repository exists.

### 30.3 Role changes

Changing which repository has Workspace role is blocked while Features exist.

---

## 31. Workspace rename and root changes

Keep the current rule:

```text
blocked while Features exist
```

Also:

> Workspace display name and remote repository name are independent.

Do not rename remote repository automatically.

---

## 32. Connector lifecycle

Because connectors are now part of the portable manifest model, runtime connector behavior needs explicit separation.

Manifest:

```text
connector type
connector URL
```

Local persistence:

```text
ConnectorId
credentials
tokens
user-specific connection state
```

Connector refresh must preserve repositories in use.

Connector deletion must refuse while used by Workspace or Feature repositories.

---

## 33. Sync behavior

### 33.1 Repository sync

Workspace repository uses normal Git state pipeline.

### 33.2 Safe parallel operations

Ordinary Git status/sync may remain parallel where already safe.

### 33.3 Root-owning operations

These require ordering:

- Feature create;
- Feature remove;
- Workspace restore;
- Workspace conversion.

### 33.4 Hooks

Hook attribution must support:

```text
repositoryPath == Workspace root
repositoryPath == Feature root
```

Do not assume repository path ends with repository name.

---


## 34. File watchers and Git Changes

The Workspace repository should participate in the existing file-watcher and Git Changes pipeline exactly like any other repository, with one important role-aware rule:

> **The Workspace repository watcher must exclude every Source repository root in the same context.**

This prevents nested Source repositories from producing duplicate or misattributed Git Changes events in the Workspace repository.

### 34.1 Base Workspace watcher topology

Without a Workspace repository, the effective model is:

```text
Workspace/
├── RepoA/   <- watched Git repository
├── RepoB/   <- watched Git repository
└── RepoC/   <- watched Git repository
```

With a Workspace repository:

```text
Workspace/       <- watched Workspace repository
├── CLAUDE.md
├── AGENTS.md
├── .graymoon.json
├── prompts/
├── docs/
├── RepoA/       <- watched Source repository
├── RepoB/       <- watched Source repository
└── RepoC/       <- watched Source repository
```

The Workspace repository watcher covers Workspace-level files such as:

```text
CLAUDE.md
AGENTS.md
.graymoon.json
README.md
docs/**
prompts/**
scripts/**
```

but excludes:

```text
RepoA/**
RepoB/**
RepoC/**
```

Each Source repository continues to own its own watcher and Git Changes state.

### 34.2 Do not use `.gitignore` as watcher authority

The generated `.gitignore` and the watcher exclusion set should describe the same physical boundaries, but watcher correctness must not depend on parsing `.gitignore`.

GrayMoon already knows repository membership and physical paths.

For the special Workspace context:

```text
Workspace repository watcher root
    = Workspace root

Workspace repository watcher exclusions
    = all Source repository roots in that Workspace context
```

For a Feature context:

```text
Workspace repository watcher root
    = Feature root

Workspace repository watcher exclusions
    = all Source WorkspaceFeatureRepository.WorktreePath values
      in that Feature context
```

This should be built from GrayMoon's repository/context model, not from filename heuristics.

### 34.3 Feature watcher topology

When the Workspace repository exists, a Feature looks like:

```text
FeatureRoot/          <- Workspace repository worktree + watcher
├── CLAUDE.md
├── AGENTS.md
├── .graymoon.json
├── prompts/
├── RepoA/            <- Source repository worktree + watcher
├── RepoB/            <- Source repository worktree + watcher
└── RepoC/            <- Source repository worktree + watcher
```

Event attribution must therefore remain unambiguous:

```text
FeatureRoot/CLAUDE.md
    -> Workspace repository
    -> Feature context

FeatureRoot/RepoA/src/Service.cs
    -> RepoA
    -> Feature context
```

The Workspace-repository watcher must never report the second event.

### 34.4 One watcher model, not a new Workspace-file watcher

Do not introduce a separate watcher subsystem for Workspace files.

The Workspace repository is already a real repository, so its file watcher should flow through the same Git Changes invalidation and refresh path as Source repositories.

This is an architectural benefit of making the Workspace repository first-class:

```text
Workspace-level file change
    -> Workspace repository watcher
    -> Workspace repository Git Changes refresh
```

No synthetic "Workspace files" Git source is needed.

### 34.5 Watcher events and Git Changes

Watcher events should remain lightweight.

A file watcher event should:

1. identify the repository/context whose working tree changed;
2. invalidate or schedule refresh of that repository's Git Changes snapshot;
3. coalesce/debounce bursts using the existing Git Changes monitoring behavior;
4. avoid doing Workspace-definition reconciliation directly inside the watcher callback.

The watcher is a signal that disk state changed, not the owner of application-level reconciliation.

### 34.6 `.graymoon.json` write-loop protection

A common authoritative flow will be:

```text
Workspace setting changes
    -> GrayMoon updates database
    -> GrayMoon writes .graymoon.json
    -> Workspace repository watcher fires
    -> Git Changes refreshes
```

This is correct and desirable.

It must not become:

```text
watcher fires
    -> parse manifest
    -> save same database state
    -> rewrite manifest
    -> watcher fires again
```

Therefore Git Changes watching and manifest reconciliation are separate concerns.

Recommended rule:

```text
File watcher
    -> Git Changes invalidation / refresh only

Manifest reconciliation
    -> content-aware authoritative manifest comparison
    -> explicit reconciliation workflow when semantic content differs
```

Manifest writes should remain deterministic and write only when content actually changes.

### 34.7 Authoritative manifest change detection

The Workspace repository watcher may be used as the low-level signal that `.graymoon.json` changed, but a higher-level manifest service decides whether reconciliation is required.

The service should compare the authoritative special-Workspace-context manifest against the last/current canonical Workspace definition.

Feature copies of `.graymoon.json` remain non-authoritative and must never trigger runtime Workspace reconciliation.

Therefore:

```text
special Workspace context .graymoon.json change
    -> Git Changes refresh
    -> authoritative manifest difference may be detected
    -> offer reconciliation

Feature context .graymoon.json change
    -> Git Changes refresh only
```

### 34.8 Watcher lifetime

The Workspace-repository design increases the value of keeping file watchers active for the active Workspace/Feature context.

For an active context, GrayMoon should keep watchers alive for:

- the Workspace repository root, when present;
- every Source repository root in that context.

This allows AI tools and external editors to modify root-level Workspace files or Source repository files and have Git Changes update consistently.

The existing strategy of retaining watchers longer for active Workspaces should extend naturally to the Workspace repository.

### 34.9 Structural operations

Watcher lifecycle must account for root-owning structural operations.

During operations such as:

```text
Feature create
Feature remove
Workspace repository conversion
Workspace restore
```

GrayMoon should pause or reconfigure affected watchers so intermediate directory creation/removal does not produce misleading Git Changes snapshots.

After the operation completes:

1. rebuild watcher scopes from the authoritative repository/context paths;
2. restart/resume monitoring;
3. perform one explicit Git Changes refresh.

### 34.10 Watcher tests

Add coverage for:

- Workspace repository root watcher detects `CLAUDE.md` changes;
- Workspace repository root watcher detects `.graymoon.json` changes;
- Source-repository file changes do not surface as Workspace-repository Git Changes;
- Source repository watcher still detects the same child change;
- Feature Workspace-root watcher attributes Workspace-level changes to the Feature context;
- Feature Source worktree changes do not leak into Workspace-repository watcher results;
- authoritative `.graymoon.json` change can trigger reconciliation detection;
- Feature `.graymoon.json` change never triggers runtime reconciliation;
- GrayMoon-authored manifest write does not create a rewrite loop;
- watcher scopes are rebuilt correctly after Feature create/remove;
- active-context watcher retention includes the Workspace repository.

---

## 35. Workspace files

Workspace-file functionality remains generic.

A non-Git-backed Workspace can still have Workspace files.

A Git-backed Workspace gives those files a natural Git home.

In Features, Workspace-level files are real files in the Workspace repository worktree.

No projection/copy mechanism is needed.

---

## 36. Security and privacy

Never write these into `.graymoon.json`:

- tokens;
- passwords;
- refresh tokens;
- OAuth secrets;
- Worker secrets;
- machine-local auth state;
- private credentials.

Connector entries are descriptive only.

Example:

```json
{
  "type": "github",
  "url": "https://github.com"
}
```

not credentials.

---

## 37. Failure handling

### 36.1 Invalid authoritative manifest

Report validation error.

Do not partially apply membership or connector changes.

### 36.2 Invalid Feature manifest

Do not affect runtime state.

It remains a Git file change.

### 36.3 Missing connector on restore

Pause only the affected resolution path and ask user to connect/select connector.

Do not fabricate credentials.

### 36.4 Missing repository on restore

Continue where possible and report unresolved repository.

### 36.5 Workspace repository Git failure

Scope failure to that repository where possible.

### 36.6 Feature root failure

If Workspace worktree creation fails:

- do not create Source worktrees;
- persist repairable state;
- mark Feature `NeedsRepair`.

---

## 38. Migration

### 37.1 Existing database

Add `Role` default:

```text
Source
```

No existing Workspace becomes Git-backed automatically.

### 37.2 Fresh database

Same default.

### 37.3 Existing root `.git`

Do not auto-adopt during migration.

Offer explicit action later.

---

## 39. APIs and domain boundaries

### 38.1 Repository role

Add:

```csharp
WorkspaceRepositoryRole
```

### 38.2 URL identity service

Introduce one centralized abstraction for URL canonicalization/matching.

For example:

```csharp
IRepositoryIdentityService
```

Responsibilities:

- normalize connector URLs;
- normalize repository URLs;
- compare equivalent repository URLs;
- derive provider-specific canonical form where needed.

Do not spread URL matching logic through repositories/pages.

### 38.3 Manifest service

Add:

```csharp
IWorkspaceManifestService
```

Responsibilities:

- build manifest;
- parse/validate;
- deterministic serialization;
- compare/write;
- reconciliation differences.

### 38.4 Workspace repository orchestration

Add:

```csharp
IWorkspaceRepositoryOperations
```

Responsibilities:

- initialize;
- convert/adopt;
- restore;
- role changes;
- managed `.gitignore`;
- structural safety.

---

## 40. Feature creation API

No optional inclusion flag is required.

Current conceptual API can remain:

```csharp
CreateFeatureAsync(
    int workspaceId,
    string featureName,
    WorkspaceFeatureBaseKindApplication baseKind,
    ...)
```

If a Workspace-role link exists, it is always included and created first.

---

## 41. Team Workspace branches

Workspace repository branches can represent different Workspace definitions/rules.

Example:

```text
main
john
mary
```

These remain independent of Source repository branch switching.

Every Feature branches the Workspace repository from whichever branch/tag is active at Feature creation.

That gives the Feature an exact snapshot of Workspace-level rules.

---

## 42. Manifest reconciliation after branch switch/pull

Switching or pulling Workspace repository may change:

- profile;
- connectors;
- repositories;
- future portable settings.

MVP:

1. Git operation completes;
2. GrayMoon detects authoritative manifest change;
3. compute differences;
4. show review;
5. user explicitly applies reconciliation.

Do not automatically mutate Workspace composition during checkout.

---

## 43. Recommended implementation phases

### Phase A - persistence and role

- add `WorkspaceRepositoryRole`;
- add `Role` to `WorkspaceRepositoryLink`;
- migrate existing links to `Source`;
- enforce one Workspace role;
- tests.

### Phase B - portable identity model

- introduce connector URL normalization;
- introduce repository URL normalization;
- centralize equivalent URL matching;
- no manifest IDs.

### Phase C - path model

- Workspace role resolves to Workspace/Feature root;
- Source behavior unchanged;
- hook attribution root-path tests.

### Phase D - Workspace repository UI

- show Workspace repository distinctly;
- normal Git operations;
- Git Changes;
- PRs;
- Actions;
- suppress dependency presentation.

### Phase E - initialize/convert

- initialize Git at Workspace root;
- create/link Workspace repository;
- create manifest;
- create managed `.gitignore`;
- block while Features exist.

### Phase F - manifest schema and synchronization

- schema v1;
- connectors using `type` + `url`;
- repositories using `repositoryUrl` + `connectorUrl`;
- no local/provider IDs;
- deterministic serializer;
- authoritative updates;
- parse/validate;
- reconciliation diff.

### Phase G - Feature integration

- Workspace repository always included;
- root worktree created first;
- Source worktrees underneath;
- root removed last;
- AI files live in Feature;
- Feature `.graymoon.json` non-authoritative;
- repair tests.

### Phase H - restore

- detect manifest;
- clone Workspace repository;
- resolve connectors by URL;
- resolve repositories by URL;
- create local persistence independently;
- clone missing Source repos;
- initial sync.

### Future Phase - Feature-proposed Workspace changes

Not v1.

Potentially:

- understand Feature manifest diff;
- proposed add/remove connectors/repositories;
- explicit apply-to-Feature;
- dynamic membership;
- merge reconciliation.

---

## 44. Test matrix

### Persistence

- existing links migrate to `Source`;
- one Workspace role succeeds;
- second refused.

### URL identity

- connector URLs normalize;
- trailing slash/case equivalent where valid;
- repository HTTPS `.git` normalization;
- SSH/HTTPS equivalence where provider rules allow;
- no local IDs involved in matching.

### Manifest

- schema v1 round-trip;
- connectors serialize without secrets;
- repositories serialize with `repositoryUrl` and `connectorUrl`;
- no `RepositoryId`;
- no `ConnectorId`;
- no provider numeric repository ID;
- deterministic serialization;
- invalid authoritative manifest rejected.

### Restore portability

Create manifest on logical machine A and restore into a database on machine B where all local IDs differ.

Verify successful matching solely through:

```text
connectorUrl
repositoryUrl
```

### Paths

- Workspace role special context -> root;
- Workspace role Feature context -> Feature root;
- Source paths unchanged.

### Profiles

- Basic Workspace repository pure Git;
- DotNetDependency Workspace repository does not produce project/dependency state;
- GitVersion and Actions work when enabled.

### Features

- Workspace repository always participates;
- Workspace worktree created first;
- Source worktrees underneath;
- Workspace removed last;
- AI rules present;
- AI-rule edits appear in Workspace-repo Git Changes;
- Feature `.graymoon.json` edit does not mutate runtime state.

### `.gitignore`

- Source repos emitted;
- Workspace role excluded;
- user rules preserved.

### Context isolation

Verify Workspace repository state differs safely between special Workspace and Feature contexts.

### Connector restore

- existing matching connector reused;
- missing connector requests user connection;
- credentials never come from manifest.

### Repository restore

- existing matching repository reused by URL;
- missing repository imported through resolved connector;
- local IDs generated independently.

---

## 45. Explicit non-goals for first implementation

Do not include:

- provider repository IDs in manifest;
- GrayMoon local IDs in manifest;
- optional Workspace repository Feature participation;
- late Workspace-repo attach/detach to Feature;
- arbitrary Source repository paths;
- dynamic Feature-local repository membership;
- automatic Feature-manifest application;
- automatic destructive reconciliation;
- secret synchronization;
- multiple Workspace repositories;
- submodules;
- auto-committing manifest changes.

---

## 46. Rejected alternatives

### 45.1 Provider repository IDs in manifest

Rejected.

They add provider-specific coupling and are unnecessary for Workspace portability.

Portable identity is:

```text
connectorUrl
repositoryUrl
```

### 45.2 GrayMoon persistence IDs in manifest

Rejected.

Different installations will generate different local IDs.

### 45.3 Connector display name as identity

Rejected.

Display names are user-editable.

Use normalized connector URL.

### 45.4 `cloneUrl`

Rejected as manifest terminology.

Use:

```text
repositoryUrl
```

because it represents the repository resource, not just the clone operation.

### 45.5 Derive connector by naive repository URL prefix

Rejected.

SSH and provider-specific URL forms break simple prefix assumptions.

Keep `connectorUrl` explicit.

### 45.6 Hidden Workspace Git repository

Rejected.

Use the existing first-class repository infrastructure.

### 45.7 `WorkspaceType.Git`

Rejected.

Git backing is independent of Workspace profile.

### 45.8 Optional Workspace repository in Features

Rejected.

Workspace-level files must always be live and branchable in Features.

### 45.9 Project/copy Workspace AI files into Features

Rejected.

A real Workspace-repository worktree provides correct Git semantics directly.

### 45.10 Apply Feature `.graymoon.json` immediately

Rejected.

That creates Feature-local Workspace composition and requires a larger context-membership model.

---

## 47. Final architecture

```text
Workspace
│
├── Profile
│   ├── Type
│   ├── Versioning
│   └── CI
│
├── Portable Manifest
│   ├── Workspace settings
│   ├── Connectors
│   │   └── identity = normalized connectorUrl
│   └── Source repositories
│       └── identity = normalized repositoryUrl
│
├── Special Workspace Context
│
├── WorkspaceRepositoryLink [Role=Workspace]   optional, max one
│   └── working tree == Workspace root
│
├── WorkspaceRepositoryLink [Role=Source]
│   └── working tree == Workspace root / repo name
│
└── Features
    └── Feature Context
        ├── WorkspaceFeatureRepository [Workspace role]
        │   └── worktree == Feature root
        └── WorkspaceFeatureRepository [Source...]
            └── worktree == Feature root / repo name
```

The manifest contains no GrayMoon-local identity.

Another computer can recreate equivalent local state using only external portable definitions.

---

## 48. Design conclusion

The Workspace repository should be a first-class repository whose working tree is the Workspace root.

Workspace profiles remain independent.

Every Feature always includes the Workspace repository when it exists, which ensures that Workspace-level AI instructions and other shared files are real Feature-branch files with normal Git/PR semantics.

The manifest should be intentionally installation-agnostic.

The portable identity model is:

```text
Connector:
    normalized connectorUrl

Repository:
    normalized repositoryUrl
    explicit connectorUrl relationship
```

GrayMoon persistence IDs and provider-specific repository IDs must never be serialized.

Connector definitions may travel with the Workspace, but credentials never do.

This gives GrayMoon a clean split:

```text
.graymoon.json
    portable description of external resources and Workspace configuration

GrayMoon database
    local operational identity, credentials, runtime state and projections
```

That separation is what makes a Workspace repository genuinely portable across machines while still fitting the existing GrayMoon architecture.
