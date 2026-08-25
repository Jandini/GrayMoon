# TOFIX - Known product bugs

Tracked bugs found during feature use or investigation. Prefer fixing these over expanding related docs until the UI numbers match the tooltip.

Related doc gaps: [TODO.md](TODO.md) (version-file walkthrough still missing).

---

## Dependency badge shows "X of Y" with Y too small when a version file self-references its own repo

**Status:** Open  
**Surface:** Repositories (`/workspaces/{id}`) - dependency badge on a repository row  
**Example workspace:** MezzoRecovery (`/workspaces/2`) on MezzoRecovery after configuring a version file that tokens its own repo name

### Symptom

Hovering the red/mismatch dependency badge shows a correct tooltip with **two** out-of-date file-config lines (including a self-reference), but the badge text is **"2 of 1"** (numerator larger than denominator).

Example tooltip (values illustrative):

```
File dependencies requiring update:
.env
  MezzoRecovery: 0.0.0-local -> 0.1.0-main.55
  MezzoRecovery.Api: 0.1.0-mezzorecovery-api.3 -> 0.1.0-main.41
```

Badge: **2 of 1**

### Steps to reproduce

1. Open Repositories for a workspace that includes **MezzoRecovery** and **MezzoRecovery.Api** (or any pair A + B).
2. On Files, pin a file in repo A (e.g. MezzoRecovery `docker/.env` or `.env`).
3. Configure a version pattern that references **A itself** and **another workspace repo**, e.g.:

```
APP_VERSION={MezzoRecovery}
API_VERSION={MezzoRecovery.Api}
```

4. Ensure both token values in the file differ from each repo's current `GitVersion` (so both lines are out of date).
5. Return to Repositories and wait for file-version status to recompute (sync / check).
6. Hover MezzoRecovery's dependency badge.

### Observed vs expected

| | Observed | Expected |
| --- | --- | --- |
| Tooltip | Two out-of-date file lines (self + other) | Same (correct) |
| Badge | **2 of 1** | **2 of 2** if self-refs count as deps for the badge, **or** **1 of 1** if self-refs are excluded from both X and Y but still listed in the tooltip as informational |

Either consistent policy is fine; the bug is the **split policy** between numerator and denominator.

### Suspected root cause

Badge formula in `WorkspaceRepositoriesRow.razor`:

- **X** = `UnmatchedDeps + OutOfDateFileRepos`
- **Y** = `Dependencies` only (`totalWithFiles = depCount`; `TotalFileConfigRepos` is not used for Y)

Two counters use different rules for **self-referencing** version-file tokens (`{MezzoRecovery}` when the file lives in MezzoRecovery):

1. **Numerator includes self-refs**  
   `WorkspaceFileVersionService.CheckAndPersistFileVersionStatusCoreAsync` records every out-of-date `TokenName` into `repoOutOfDateTokens` / `WorkspaceFileLineStatuses` with **no** skip when the token resolves to the same repository as the file.  
   `OutOfDateFileRepos` = that set's count → **2** (MezzoRecovery + MezzoRecovery.Api).

2. **Denominator excludes self-refs**  
   `WorkspaceProjectRepository.BuildRepoDependencyEdgeSetsAsync` builds file-config edges with:

   `if (referencedRepoId == dependentRepoId) continue;`

   Self-loops are intentionally omitted from the dependency graph (levels / Kahn / custom-deps cycle checks).  
   `PersistRepositoryDependencyLevelAndDependenciesAsync` sets `Dependencies` from those edges → only MezzoRecovery.Api → **1**.

3. **Same self-skip on the unused total**  
   `BuildTotalFileConfigReposByDependentRepo` also skips `referencedRepoId == dependentRepoId`, so `TotalFileConfigRepos` would also be **1**. Even switching the badge Y to that field would still show **2 of 1** unless `OutOfDateFileRepos` is aligned.

So: tooltip and `OutOfDateFileRepos` treat self-tokens as first-class version staleness; graph / `Dependencies` / `TotalFileConfigRepos` treat them as non-edges. The badge adds those counts together and prints **"2 of 1"**.

### Code locations

| Area | Path / symbol |
| --- | --- |
| Badge X of Y | `WorkspaceRepositoriesRow.razor` (`unmatchedWithFiles of totalWithFiles`) |
| Out-of-date file repo count (X file portion) | `WorkspaceFileVersionService.ApplyFileConfigLinkCountersAsync` / `CheckAndPersistFileVersionStatusCoreAsync` → `OutOfDateFileRepos` |
| Total file-config repos (unused for Y) | `WorkspaceFileVersionService.BuildTotalFileConfigReposByDependentRepo` (skips self) |
| Graph / `Dependencies` (Y) | `WorkspaceProjectRepository.BuildRepoDependencyEdgeSetsAsync` (skips self) + `PersistRepositoryDependencyLevelAndDependenciesAsync` |
| Implicit FromFile set | `GetImplicitReferencedRepoIdsBySourceAsync` (uses FileConfig edges; self excluded) |
| Link field docs | `WorkspaceRepositoryLink.OutOfDateFileRepos`, `TotalFileConfigRepos`, `Dependencies` |

### Suggested fix direction (docs only - no code change here)

Pick one policy and apply it to **both** badge X and Y (and keep the tooltip honest):

**Option A - Exclude self-refs from badge counts (align with graph)**  
- When computing `OutOfDateFileRepos`, skip tokens whose resolved repo id equals the dependent file's repository id (same skip as `BuildRepoDependencyEdgeSetsAsync` / `BuildTotalFileConfigReposByDependentRepo`).  
- Keep self-ref stale lines in the tooltip if product wants visibility, but do not inflate X.  
- Result for the repro: badge **1 of 1**; tooltip can still list the self line under file deps.

**Option B - Include self-refs in badge denominator (align with version-file UX)**  
- Count self-tokens in `TotalFileConfigRepos` (remove the self skip there).  
- Change the badge Y to include file-config totals in a way that does not double-count repos already in `Dependencies` (e.g. union of project/custom edge refs and file tokens, including self).  
- Result for the repro: badge **2 of 2**.  
- Do **not** add self-loops to Kahn level edges; keep graph self-skip separate from badge accounting.

**Option C - Hybrid**  
- Tooltip: show all out-of-date lines including self.  
- Badge X/Y: only non-self file tokens + project unmatched, with Y = matching universe.  
- Same numeric outcome as A for this repro.

Also add a regression test: version file in repo A with tokens `{A}` and `{B}`, both stale → badge never shows X > Y; assert chosen expected pair (`1 of 1` or `2 of 2`).

### Notes

- Self-reference via file config is a valid user scenario (pin own app version in `.env` alongside dependency versions).  
- Graph self-skip is correct for leveling; the bug is only the badge mixing that denominator with a numerator that still counts self-tokens.