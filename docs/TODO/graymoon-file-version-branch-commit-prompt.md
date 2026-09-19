# GrayMoon — File Version `:branch` and `:commit` Tokens

## Goal

Extend GrayMoon's existing file-version configuration token syntax.

Existing syntax:

```text
{@repository-name}
```

must continue to resolve to the repository's **GitVersion**.

Add:

```text
{@repository-name:branch}
{@repository-name:commit}
```

Semantics:

```text
{@repo}         -> persisted GitVersion
{@repo:branch}  -> persisted current branch name
{@repo:commit}  -> current local HEAD commit SHA
```

Maintain full backward compatibility with existing configurations.

---

## Architecture

Do **not** add a commit SHA column to `WorkspaceRepositoryLink` and do **not** add a DB migration just for this feature.

GrayMoon already has:

```csharp
WorkspaceRepositoryLink.GitVersion
WorkspaceRepositoryLink.BranchName
```

and the Git Changes projection also has:

```csharp
WorkspaceGitRepositoryStatus.HeadCommit
```

However, `WorkspaceGitRepositoryStatus` is a cached Git Changes projection and may be stale relative to the repository at the exact moment file versions are updated.

Therefore use:

```text
GitVersion -> WorkspaceRepositoryLink.GitVersion
Branch     -> WorkspaceRepositoryLink.BranchName
Commit     -> retrieve from local Git on demand
```

Only repositories actually referenced by `:commit` tokens should incur Git work.

For commit use:

```bash
git rev-parse HEAD
```

No fetch, GitHub call, or GitVersion execution is required.

Retrieve the **full SHA**.

---

## 1. Introduce a Structured Token Model

The existing `ExtractTokens()` approach assumes everything inside `{@...}` identifies a repository.

That no longer works because:

```text
{@Repo:commit}
```

must mean repository `Repo` with selector `commit`, not repository `Repo:commit`.

Introduce a central token representation, for example:

```csharp
public enum FileVersionTokenKind
{
    GitVersion,
    Branch,
    Commit
}

public sealed record FileVersionToken(
    string RepositoryName,
    FileVersionTokenKind Kind);
```

The parser must support:

```text
{@Repo}
{@Repo:branch}
{@Repo:commit}
```

The leading `@` is part of GrayMoon's token syntax and must be required/preserved.

Selectors should be case-insensitive.

Repository matching should remain case-insensitive.

Unsupported selectors such as:

```text
{@Repo:foo}
```

must be treated as invalid selectors, not as repository names.

Centralize parsing. Do not scatter `Split(':')` logic throughout the codebase.

Search all callers of `ExtractTokens` and update them appropriately.

---

## 2. Define a Canonical Token Key

Resolved values should be keyed by the complete logical token identity.

For example:

```text
@Repo
@Repo:branch
@Repo:commit
```

or another internal representation that preserves the same distinction.

The actual configured syntax remains:

```text
{@Repo}
{@Repo:branch}
{@Repo:commit}
```

Example resolved values:

```text
{@Repo}         -> 2.8.1-alpha.5
{@Repo:branch}  -> feature/search
{@Repo:commit}  -> a351169a63ad799f1f24b0897a1ada4dafd07449
```

Generalize existing names such as:

```text
RepoVersions
ExpectedVersions
```

where appropriate to:

```text
TokenValues
ExpectedValues
```

because these values are no longer exclusively GitVersions.

---

## 3. Central Token Resolver

Implement one central resolver in/around:

```text
src/GrayMoon.App/Services/Workspaces/WorkspaceFileVersionService.cs
```

Conceptually:

```csharp
ResolveTokenValuesAsync(...)
```

It must resolve:

### GitVersion

```text
{@Repo}
```

from:

```csharp
WorkspaceRepositoryLink.GitVersion
```

No Git operation.

### Branch

```text
{@Repo:branch}
```

from:

```csharp
WorkspaceRepositoryLink.BranchName
```

No Git operation.

### Commit

```text
{@Repo:commit}
```

from the local repository using:

```bash
git rev-parse HEAD
```

through the Agent.

Both file-version **update** and file-version **check** must use the same resolver.

Do not duplicate resolution logic.

---

## 4. Resolve Commits On Demand Only

Before contacting the Agent, collect all unique repositories referenced by `:commit`.

Example configuration:

```text
VERSION={@RepoA}
BRANCH={@RepoA:branch}
COMMIT={@RepoA:commit}
OTHER_COMMIT={@RepoB:commit}
```

This should require HEAD lookup only for:

```text
RepoA
RepoB
```

and each repository should be resolved only once for the operation.

Do not execute Git once per token or once per configured file.

Prefer one logical App -> Agent request containing all required repositories rather than N separate bridge calls.

The Agent may resolve those repositories concurrently using bounded concurrency consistent with existing GrayMoon patterns.

---

## 5. Agent Git Support

Inspect:

```text
src/GrayMoon.Agent/Abstractions/IGitService.cs
src/GrayMoon.Agent/Services/GitService.cs
```

Add/reuse a focused method such as:

```csharp
Task<string?> GetHeadCommitAsync(
    string repoPath,
    CancellationToken cancellationToken);
```

using:

```bash
git rev-parse HEAD
```

GrayMoon already contains essentially this implementation in:

```text
src/GrayMoon.Agent/Services/GitChanges/GitCliRepositoryGitChangesService.cs
```

via the private `GetHeadCommitShaAsync`.

Avoid duplicate Git implementations where practical.

Use the existing `GitProcessRunner` and argument-list execution.

Do not use a shell command string.

---

## 6. Agent Batch Request

Add an Agent request/response capable of resolving HEAD for the required repository names in one logical operation.

Conceptually:

```json
{
  "workspaceName": "...",
  "workspaceRoot": "...",
  "repositoryNames": [
    "RepoA",
    "RepoB"
  ]
}
```

Response can be a simple repository -> SHA mapping.

Keep the contract focused and lightweight.

Do not return unrelated repository state.

---

## 7. Update `UpdateFileVersionsCommand`

Inspect:

```text
src/GrayMoon.Agent/Commands/UpdateFileVersionsCommand.cs
```

Currently pattern parsing effectively produces:

```text
prefix
repoName
suffix
```

Change this concept to:

```text
prefix
tokenKey
suffix
```

Example:

```text
COMMIT={@Repo:commit}
```

must identify:

```text
Prefix     = "COMMIT="
Repository = "Repo"
Selector   = "commit"
Suffix     = ""
```

or internally reduce that to a canonical token key.

The important point is that the parser recognizes the complete GrayMoon token syntax, including the leading `@`.

The update command should ultimately perform a resolved-value lookup rather than assuming every token means GitVersion.

Preserve current prefix/suffix/whitespace behavior.

---

## 8. Update `CheckFileVersionsCommand`

Inspect:

```text
src/GrayMoon.Agent/Commands/CheckFileVersionsCommand.cs
```

Generalize `ExpectedVersions` to expected token values.

These must be distinct:

```text
{@Repo}
{@Repo:branch}
{@Repo:commit}
```

For example:

```text
COMMIT={@Repo:commit}
```

must compare against the expected commit value associated with the `Repo:commit` token, not against the GitVersion for `Repo`.

Keep update/check semantics symmetrical.

---

## 9. Dependency Graph

Inspect:

```text
src/GrayMoon.App/Repositories/WorkspaceProjectRepository.DependencyGraph.cs
```

Dependency semantics are based on the **repository**, not selector.

Therefore:

```text
{@Repo}
{@Repo:branch}
{@Repo:commit}
```

all refer to the same repository dependency.

If one configured file contains all three, it still represents one dependency edge to `Repo`.

Use the parsed `RepositoryName` when building dependency relationships.

Do not create separate graph nodes/edges for branch or commit selectors.

---

## 10. Repository Counters

Review all code calculating things such as:

```text
TotalFileConfigRepos
OutOfDateFileRepos
HasSelfFileVersionToken
```

Repository counts must deduplicate by repository, not complete token.

Thus:

```text
{@Repo}
{@Repo:branch}
{@Repo:commit}
```

means one referenced repository.

Individual out-of-date **lines** may still be counted separately according to existing semantics.

---

## 11. Self References

All of these are self references:

```text
{@CurrentRepo}
{@CurrentRepo:branch}
{@CurrentRepo:commit}
```

They should participate in existing `HasSelfFileVersionToken` semantics.

Do not create dependency graph cycles for them.

---

## 12. Filtering

Inspect:

```text
FilterPatternLinesToRepos(...)
```

and any equivalent logic.

Filtering must compare the parsed:

```csharp
token.RepositoryName
```

against allowed repository names.

For example:

```text
{@ServiceA:commit}
```

belongs to repository `ServiceA`.

Do not compare `ServiceA:commit` or `@ServiceA:commit` directly against repository names.

---

## 13. Version Configuration UI

Update:

```text
src/GrayMoon.App/Components/Modals/VersionConfigModal.razor
```

Explain the three supported forms concisely:

```text
{@repository}         GitVersion
{@repository:branch}  current branch
{@repository:commit}  current commit
```

Existing `@@` repository autocomplete may continue inserting the normal GrayMoon repository token.

Users must be able to append:

```text
:branch
:commit
```

within the token.

Autocomplete enhancement for selectors is optional and should not complicate the core implementation.

---

## 14. UI Validation

Current validation must not interpret:

```text
{@Repo:commit}
```

as repository `Repo:commit`.

Parse tokens first.

Report separately:

```text
unknown repository
unsupported selector
```

Examples:

```text
{@MissingRepo:commit}
```

means unknown repository `MissingRepo`.

```text
{@ExistingRepo:foo}
```

means unsupported selector `foo`.

Do not report `ExistingRepo:foo` as an unknown repository.

---

## 15. Detached HEAD / Tags

GrayMoon already models tag checkout separately from branch.

For a repository checked out at a tag/detached HEAD:

```text
{@Repo:branch}
```

should be unresolved if `BranchName` is null.

Do not substitute the tag name as the branch.

However:

```text
{@Repo:commit}
```

must still work because:

```bash
git rev-parse HEAD
```

is valid for detached HEAD.

Therefore:

```text
tag checkout:

{@Repo:branch} -> unresolved
{@Repo:commit} -> valid SHA
```

---

## 16. Unborn Repository / Failed Resolution

If:

```bash
git rev-parse HEAD
```

fails because the repository has no commit yet, treat `{@Repo:commit}` as unresolved.

Do not substitute an empty string and do not destroy the existing configured file value.

Likewise, if `BranchName` is unavailable for `{@Repo:branch}`, do not replace the existing value with empty text.

Log an actionable warning.

---

## 17. Do Not Use Git Changes Cache as Authoritative Commit

Although:

```csharp
WorkspaceGitRepositoryStatus.HeadCommit
```

already exists, do not make `:commit` depend on it.

It is a persisted Git Changes read model and can lag the local checkout depending on watcher timing.

For file stamping, use:

```bash
git rev-parse HEAD
```

at the start of the file-version operation for repositories requiring `:commit`.

Do not run the heavier:

```bash
git status --porcelain=v2 ...
```

just to obtain HEAD.

---

## 18. No Additional GitVersion Calls

This feature must not introduce GitVersion executions.

Expected cost:

```text
{@Repo}         -> zero commands
{@Repo:branch}  -> zero commands
{@Repo:commit}  -> one cheap local HEAD lookup per unique referenced repo
```

No fetch.

No remote call.

No GitVersion.

---

## 19. Tests

Add/update tests covering at least:

```text
{@Repo}
{@Repo:branch}
{@Repo:commit}
```

Verify existing `{@Repo}` behavior remains unchanged.

Test selector case handling.

Test unsupported selectors.

Test unknown repositories.

Test GitVersion + branch + commit for the same repository.

Test multiple `{@Repo:commit}` occurrences resolve HEAD only once per repository.

Test multiple repositories using `:commit`.

Test dependency graph deduplication:

```text
{@Repo}
{@Repo:branch}
{@Repo:commit}
```

must produce one repository dependency.

Test self references using `:branch` and `:commit`.

Test detached/tag checkout:

```text
{@Repo:branch} -> unresolved
{@Repo:commit} -> valid
```

Test unborn repository/failed HEAD lookup.

Test Agent update substitution, for example:

```text
COMMIT={@Repo:commit}
```

Test Agent check comparison for all three token kinds.

Test prefix/suffix preservation.

---

## 20. Files to Inspect

At minimum inspect:

```text
src/GrayMoon.App/Services/Workspaces/WorkspaceFileVersionService.cs

src/GrayMoon.App/Repositories/WorkspaceProjectRepository.DependencyGraph.cs

src/GrayMoon.App/Components/Modals/VersionConfigModal.razor

src/GrayMoon.Agent/Abstractions/IGitService.cs

src/GrayMoon.Agent/Services/GitService.cs

src/GrayMoon.Agent/Services/GitChanges/GitCliRepositoryGitChangesService.cs

src/GrayMoon.Agent/Commands/UpdateFileVersionsCommand.cs

src/GrayMoon.Agent/Commands/CheckFileVersionsCommand.cs
```

Also search the entire solution for:

```text
ExtractTokens
RepoVersions
ExpectedVersions
TokenName
UpdateFileVersions
CheckFileVersions
VersionPattern
```

and remove assumptions that a token is always exactly a repository name.

---

## Completion Criteria

These must all work:

```text
{@Repo}
{@Repo:branch}
{@Repo:commit}
```

with:

```text
{@Repo}         = existing persisted GitVersion
{@Repo:branch}  = persisted local branch
{@Repo:commit}  = full current local HEAD SHA resolved on demand
```

Example valid file-version configuration:

```text
VERSION={@GrayMoon}
BRANCH={@GrayMoon:branch}
COMMIT={@GrayMoon:commit}
```

Existing configurations using only:

```text
{@Repo}
```

must behave exactly as before.

Do not add unnecessary persistence or migrations.

Only repositories actually using `:commit` should incur additional Git work.

Run the affected App, Agent, Common and test projects and report the actual build/test results.
