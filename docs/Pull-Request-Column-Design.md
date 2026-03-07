# Pull Request Column – Design Document

## Overview

Add a **PR** column to the workspace repositories grid that shows the **pull request status** for the current branch of each repo. The column is placed **after Divergence** and **before Dependencies**. Data is obtained via the **GitHub REST API** (no `gh` CLI). The goal is the **most efficient and reliable** way to get the open (or merged) PR for the current branch per repo.

**Column name:** **PR**

**Badge states:**

| State | Badge | Style |
|-------|--------|--------|
| No PR | `none` | Dark gray |
| No PR + >0 commits ahead of main | `create` | Yellow |
| PR exists (open) | `#{number}` | Green |
| PR exists (merged) | `merged` | Purple (#8957E5), white text |

---

## 1. API Strategy (Efficient & Reliable)

### 1.1 Recommended endpoint

Use GitHub REST API:

- **List Pull Requests:** `GET /repos/{owner}/{repo}/pulls`
- **Query parameters:**
  - `state=open` – only open PRs (fast, one call per repo).
  - `head={owner}:{branch}` – filter by source branch. For same-repo PRs this is the repo owner and current branch name.

So the request is:

```
GET /repos/{owner}/{repo}/pulls?state=open&head={owner}:{branch}
```

- **Empty array** → no open PR for that branch.
- **Non-empty** → first element is the open PR (number, title, `html_url`, etc.).

To support **merged** state (badge “merged” in purple), either:

- **Option A:** Use `state=all` and take the first result; check `state` and `merged_at` to decide open vs merged. Slightly more data but still one request per repo.
- **Option B:** First call with `state=open`; if empty, call again with `state=closed` and `head=owner:branch`, then inspect `merged_at` on the first result. Two calls only when there is no open PR.

**Recommendation:** **Option A** – single request with `state=all`, `per_page=1`. If the single result’s head ref matches the current branch, use it and derive state (open / merged) from `state` and `merged_at`; otherwise treat as no PR.

### 1.2 Head ref format

- **Same-repo:** `head=owner:branch` where `owner` and `repo` come from the repository we’re querying (the repo’s clone URL / org and name).
- **Fork workflow:** PRs are usually opened **against the upstream** repo, with head `head=myForkOwner:branch`. That would require knowing the “PR base repo” (upstream) and “head owner” (fork). For **v1**, we assume **same-repo** only: the repo we query is the one from `Repository.CloneUrl` (typically `origin`), and `head=owner:branch` with that repo’s owner. **Fork support** can be a later enhancement (e.g. agent or app config providing “upstream” owner/repo and “head” owner).

### 1.3 Where the logic runs: App only

- **GitHub token** lives in the app (Connector with `UserToken`), not in the agent.
- **Agent** does not call GitHub; it only provides git state (branch, ahead/behind) already used by the Divergence column.
- **App** owns all GitHub API calls. Reuse existing `GitHubService` (or a dedicated service that uses it) and existing Connector-based auth.

So: **no new agent commands for PR**. PR data is fetched in the app via GitHub API when loading the workspace repositories view.

---

## 2. New / Extended Components

### 2.1 GitHub API (App)

- **Option 1 – Extend `GitHubService`:** Add a method such as:
  - `GetPullRequestForBranchAsync(Connector connector, string owner, string repo, string branch, CancellationToken ct)`
  - Returns a DTO with: `Number`, `State` (open/closed), `MergedAt` (nullable), `HtmlUrl`, `Title` (optional).
  - Implementation: `GET repos/{owner}/{repo}/pulls?state=all&head={owner}:{branch}&per_page=1`, then map first item to DTO; if empty or head ref doesn’t match, return “no PR”.

- **Option 2 – New `GitHubPullRequestService`:** Thin service that takes `GitHubService`, `ConnectorRepository`, and exposes e.g. `GetPullRequestForBranchAsync(Repository repo, Connector connector, string branchName, CancellationToken ct)`. It would resolve owner/repo from the repository (see §2.2) and call `GitHubService` (which gets the new `GetPullRequestForBranchAsync` or a low-level `GetPullsAsync`).

**Recommendation:** Add **one new method on `GitHubService`** for the raw API call (`GetPullRequestForBranchAsync(Connector, owner, repo, branch)`), then either call it from a small **`GitHubPullRequestService`** that resolves owner/repo and connector per repo, or from the page/component that builds the PR column. Prefer a small **`GitHubPullRequestService`** so workspace page stays simple and we have a single place for “get PR for this workspace repo”.

### 2.2 Resolving owner and repo

- **Source:** `Repository.CloneUrl` (and optionally `OrgName` / `RepositoryName` if guaranteed to match GitHub).
- **Helper:** Extend **`RepositoryUrlHelper`** (or add a small static helper) to parse **owner** and **repo** from a GitHub clone URL:
  - `git@github.com:owner/repo.git` → `owner`, `repo`
  - `https://github.com/owner/repo.git` → `owner`, `repo`
- If the URL is not GitHub, the repo is skipped for PR (no API call, show “—” or “none” in the PR column).

### 2.3 Connector and token

- Each `Repository` has `ConnectorId`. Use **`ConnectorRepository.GetByIdAsync(Repository.ConnectorId)`** to get the Connector.
- Use that Connector’s `UserToken` (and optional `ApiBaseUrl`) in the existing `GitHubService.GetAsync(Connector, requestUri)` pattern.
- If the connector is not GitHub or token is missing, skip PR fetch for that repo and show a neutral badge (“none” or “—”).

---

## 3. When to Fetch PR Data

### 3.1 Option A – On workspace page load (recommended for v1)

- When the user opens the workspace repositories page, after the list of `WorkspaceRepositoryLink` (with Repository and branch) is available, **fetch PR in the app** for each repo that has a GitHub clone URL and a branch name.
- **Parallelism:** Run requests in parallel with a **bounded concurrency** (e.g. 3–5 at a time) to avoid rate limits and avoid overwhelming the client.
- **Caching:** Keep results in component state (e.g. `Dictionary<int, PullRequestInfo>` keyed by `RepositoryId`). No persistence in v1.

**Pros:** Simple, always up-to-date when opening the page, no agent changes, no DB schema change.  
**Cons:** Small delay on first load; no PR data when offline.

### 3.2 Option B – Persist and refresh

- Add fields to **`WorkspaceRepositoryLink`**: e.g. `PullRequestNumber` (int?), `PullRequestState` (string or enum: Open, Merged, Closed), `PullRequestHtmlUrl` (string), `LastPrCheckAt` (DateTime?).
- **When to set:** (1) After a full sync or refresh, the app calls the GitHub API for each repo and persists the result; and/or (2) on workspace page load, fetch and then persist.
- **Display:** Show persisted values immediately; optionally refresh in background and update.

**Pros:** Can show PR without a new API call; can show “last known” state.  
**Cons:** Schema change, migration, and more logic; risk of stale data.

**Recommendation for v1:** **Option A** – fetch on page load, in-memory only. Option B can be a follow-up if you want persisted PR state and fewer API calls on load.

---

## 4. Data Flow Summary

1. User opens workspace repositories page.
2. App has `workspaceRepositories` (links + Repository + BranchName, DefaultBranchAheadCommits, etc.).
3. For each link with a GitHub repo and non-empty `BranchName`:
   - Resolve owner/repo from `Repository.CloneUrl`.
   - Resolve Connector from `Repository.ConnectorId`.
   - Call `GitHubPullRequestService.GetPullRequestForBranchAsync(repo, connector, link.BranchName, ct)` (or equivalent).
4. Results are stored in component state (e.g. `IReadOnlyDictionary<int, PullRequestInfo>`).
5. **PR column** renders a **PRBadge** component that:
   - Takes `PullRequestInfo` (or null), plus `DefaultBranchAheadCommits` (for “create” when no PR and ahead > 0).
   - Renders: **none** (dark gray) | **create** (yellow) | **#{number}** (green) | **merged** (purple, white text).

---

## 5. Badge Logic (PR Column)

| Condition | Badge text | Style |
|-----------|------------|--------|
| No PR and (DefaultBranchAheadCommits ?? 0) > 0 | `create` | Yellow |
| No PR and ahead = 0 or null | `none` | Dark gray |
| PR state = open | `#{number}` | Green |
| PR state = merged (merged_at != null) | `merged` | Purple (#8957E5), white text |
| PR state = closed, not merged | Treat as “no PR” and use first two rows (none/create) |

Non-GitHub repo or missing connector/token: show **none** (dark gray).

---

## 6. UI Specification

### 6.1 Placement

- **Location:** New column **after Divergence**, **before Dependencies**.
- **Header:** `<th class="col-pr text-center">PR</th>`.
- **colSpan:** Increment the grid’s `colSpan` for loading/empty/filtered rows (e.g. from 7 to 8).

### 6.2 Cell content

- **Component:** `PRBadge` (new shared component).
- **Parameters:** e.g. `PullRequestNumber`, `PullRequestState` (open/merged/closed), `PullRequestUrl`, `DefaultBranchAheadCommits`, `HasPrData` (so we can show loading or “—” when fetch didn’t run).
- **Links:** When there is a PR, the badge can link to `PullRequestUrl` (GitHub PR page). “create” can link to the compare URL (same as used elsewhere: `{repoUrl}/compare/main...{branch}`).

### 6.3 Styling

- Reuse existing badge patterns from the table (e.g. `.badge`, existing column alignment).
- New classes, e.g.:
  - `.pr-badge-none` – dark gray background.
  - `.pr-badge-create` – yellow.
  - `.pr-badge-open` – green.
  - `.pr-badge-merged` – background #8957E5, color white.

Define these in `app.css` (or scoped CSS) under `.repositories-table td.col-pr`.

---

## 7. Implementation Outline

### 7.1 App – GitHub API

1. **GitHub API model:** Add a small DTO for the PR list response item (e.g. `GitHubPullRequestDto`) with `Number`, `State`, `MergedAt`, `HtmlUrl`, `Title`, and head ref info so we can confirm the branch matches.
2. **`GitHubService`:** Add `GetPullRequestForBranchAsync(Connector connector, string owner, string repo, string branch, CancellationToken ct)`:
   - Build request: `repos/{owner}/{repo}/pulls?state=all&head={owner}:{Uri.EscapeDataString(branch)}&per_page=1`.
   - Use existing `GetAsync<T>(Connector, requestUri, ct)`.
   - Deserialize as list; if count is 0, return null; else return first element mapped to a simple DTO (number, state, merged_at, html_url).
3. **`RepositoryUrlHelper`:** Add `TryParseGitHubOwnerRepo(string? cloneUrl, out string? owner, out string? repo)` returning bool. Implement for `git@github.com:...` and `https://github.com/...`.
4. **`GitHubPullRequestService`** (new):
   - Dependencies: `GitHubService`, `ConnectorRepository` (or pass connector from caller).
   - Method: `GetPullRequestForBranchAsync(Repository repository, Connector connector, string branchName, CancellationToken ct)`:
     - If `RepositoryUrlHelper.TryParseGitHubOwnerRepo(repository.CloneUrl, out var owner, out var repo)` is false, return null.
     - Call `GitHubService.GetPullRequestForBranchAsync(connector, owner, repo, branchName, ct)`.
     - Return a simple app-side model (e.g. `PullRequestInfo { Number, State, MergedAt, HtmlUrl }`).

### 7.2 App – Workspace page

1. **WorkspaceRepositories.razor:**
   - Inject `GitHubPullRequestService` and `ConnectorRepository` (if not already).
   - Add state: e.g. `Dictionary<int, PullRequestInfo?> prByRepositoryId` (or `IReadOnlyDictionary`), and optionally a “loading” set for repos we’re fetching.
   - When data is loaded (e.g. after `workspaceRepositories` is set and not loading), for each link with Repository and BranchName:
     - Ensure Repository has Connector loaded (e.g. include in query or load by ConnectorId).
     - If clone URL is GitHub and connector is valid, call `GetPullRequestForBranchAsync` (with bounded parallelism, e.g. `SemaphoreSlim(4)` or use a small helper that limits concurrency).
   - Add `<th class="col-pr text-center">PR</th>` after Divergence.
   - Add `<td class="col-pr">` with `<PRBadge ... />` after the Divergence `<td>`, passing PR data and `DefaultBranchAheadCommits`.
   - Increment `colSpan` to 8.

### 7.3 App – PRBadge component

1. **Components/Shared/PRBadge.razor:**
   - Parameters: `PullRequestNumber`, `PullRequestState` (enum or string: Open, Merged, Closed), `PullRequestUrl`, `DefaultBranchAheadCommits`, and e.g. `IsLoading` / `IsGitHubRepo`.
   - Render logic per §5 (none / create / #number / merged).
   - Use `<a href="..." class="badge pr-badge-...">` when there is a URL; otherwise `<span class="badge pr-badge-...">`.

2. **wwwroot/app.css:** Add `.col-pr` and `.pr-badge-none`, `.pr-badge-create`, `.pr-badge-open`, `.pr-badge-merged` with the specified colors.

### 7.4 Agent

- **No changes** for v1. PR is determined entirely by the app via GitHub API. Agent continues to provide branch name and default-branch ahead/behind counts (already used by Divergence and “create” badge logic).

---

## 8. Edge Cases and Reliability

| Case | Behavior |
|------|----------|
| Non-GitHub repo | Do not call API; show “none” (dark gray). |
| Missing or invalid connector/token | Skip API call; show “none”. |
| Branch name with special characters | Use `Uri.EscapeDataString(branch)` in `head=` parameter. |
| Rate limiting (403/429) | Log and show “—” or “none” for affected repos; optional: retry with backoff or show a “rate limited” message. |
| Network error | Log; show “—” or “none” for that repo. |
| Repo not yet synced / no branch | Show “none”. |

---

## 9. Future Enhancements

- **Fork workflow:** Agent or app config could provide “upstream” owner/repo and “head” owner; then call API for upstream repo with `head=headOwner:branch`.
- **Persisted PR state:** Add fields to `WorkspaceRepositoryLink` and refresh after sync or on a timer to reduce API calls on page load.
- **“Closed” state:** Optionally show a distinct style for closed (not merged) PRs.

---

## 10. Summary

- **What:** A **PR** column after **Divergence** with badges: **none** (dark gray), **create** (yellow when no PR and ahead > 0), **#{number}** (green when open), **merged** (purple #8957E5, white text).
- **How:** App calls **GitHub REST API** `GET /repos/{owner}/{repo}/pulls?state=all&head=owner:branch&per_page=1` via existing `GitHubService` + new `GitHubPullRequestService`; no `gh` CLI, no agent changes.
- **When:** Fetch on **workspace page load** with bounded parallel requests; results kept in component state (no persistence in v1).
- **Where:** New method on `GitHubService`; `RepositoryUrlHelper` extended to parse owner/repo; new `GitHubPullRequestService` and **PRBadge** component; table column and CSS as above.
