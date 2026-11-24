# Version file tokens without a value are skipped silently

Status: open, 2026-11-24. Found while the file-version check was changed to read the stored commit (`sync-performance-fix-plan.md`). Needs a decision before it is fixed.

## Symptom

A version file line whose token has no value is not compared, and the file then looks up to date:

| Token | When it has no value |
|---|---|
| `{@Repo}` | The repository's version is unresolved (GitVersion failed or has not run), in a Workspace that uses repository versioning |
| `{@Repo:branch}` | The repository is on a detached HEAD: pinned to a tag, or a read only snapshot in a Feature |

The same happened for `{@Repo:commit}` and is fixed: that token is stored as a "not checked" row with a reason (`WorkspaceFileLineStatus.NotCheckedReason`), counts on the dependency badge and shows in its tooltip.

## Cause

`WorkspaceFileVersionService.ResolveTokenValuesAsync` logs a warning and gives the token no value. The check sends only tokens with a value, then rebuilds the line statuses, so an earlier out-of-date row for that token is deleted.

## Code

- `src/GrayMoon.App/Services/Workspaces/WorkspaceFileVersionService.cs`: `ResolveTokenValuesAsync` (the `GitVersion` and `Branch` cases), `CheckAndPersistFileVersionStatusCoreAsync`.
- The mechanism to reuse: `TokenResolution.NotChecked`, filled today only for commit tokens.

## Why it is not simply done the same way

For a commit, "Sync the repository" fixes it. For these two there is often nothing the user can do:

- A repository pinned to a tag has no branch on purpose. A consumer with `{@Repo:branch}` would show "not checked" for as long as the pin exists, and Update could not clear it.
- An unresolved version is already shown on the repository's own row. Counting it again on every consumer adds badges that all point at one cause.

## Options

1. **Not checked for `{@Repo}` only.** A missing version is an error state the user should see where it has an effect. The branch token stays as it is.
2. **Not checked for both, with a reason that says what to do** ("Repo is on tag v1.2.0 and has no branch"). Honest, but a tag-pinned producer gives its consumers a badge that never clears.
3. **Leave both.** Document that a token without a value is not compared.

Recommended: option 1. The catch: every consumer of a repository whose GitVersion fails shows the badge until that is fixed.

## Fix plan (after the decision)

1. Fill `NotChecked` in the chosen cases with a reason.
2. Test with a seeded out-of-date row for the token, as `FileVersionStoredCommitTests.A_token_without_a_stored_commit_is_not_checked_instead_of_up_to_date` does.
3. Update "WorkspaceFileLineStatus" in `docs/architecture/03-data-model-and-persistence.md` and "File versioning" in `05-user-capability-reference.md`.
