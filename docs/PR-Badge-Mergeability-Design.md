# PR Badge Mergeability – Design Document (Not Implemented)

## Overview

This document describes how to extend the **PR column** so the **open PR badge** (`#{number}`) reflects **mergeability** using the GitHub REST API. The badge color would indicate:

| State            | Badge color | Meaning                          |
|------------------|------------|-----------------------------------|
| Unknown          | Gray       | Mergeability not known yet        |
| Mergeable        | Green      | No conflicts, ready to merge      |
| Conflict         | Red        | Merge conflicts                   |
| Checks running   | #d29922    | Status/CI checks in progress      |

**Status:** Implemented.

---

## 1. What the GitHub API Delivers

### 1.1 Endpoints and fields

GitHub’s REST API exposes mergeability on pull request resources:

- **Get a pull request:** `GET /repos/{owner}/{repo}/pulls/{pull_number}`  
  Returns the full pull request object, including:
  - **`mergeable`** (boolean or null)  
    - `true` – can be merged without conflicts  
    - `false` – has merge conflicts  
    - `null` – GitHub is still computing mergeability (background job)
  - **`mergeable_state`** (string)  
    More detailed state; see below.

- **List pull requests:** `GET /repos/{owner}/{repo}/pulls?state=all&head=owner:branch&per_page=1`  
  The response schema includes the same top-level fields, so **list responses can include `mergeable` and `mergeable_state`** when the API returns the full pull request shape. In practice, list and get often return these fields; the **single-PR endpoint is the one that guarantees** them (see [REST API – Get a pull request](https://docs.github.com/en/rest/pulls/pulls#get-a-pull-request)).

### 1.2 `mergeable` (boolean | null)

From [GitHub Docs – Checking mergeability of pull requests](https://docs.github.com/en/rest/pulls/pulls#get-a-pull-request):

- When you get, create, or edit a pull request, GitHub may create a test merge commit to see if it can be merged.
- **`mergeable`** can be:
  - **`true`** – No conflicts; if applicable, `merge_commit_sha` is the test merge commit.
  - **`false`** – There are merge conflicts.
  - **`null`** – A background job is computing mergeability; after a short delay, a second request can return a non-null value.

So the API **does deliver** “mergeable”, “conflict”, and “don’t know yet” via this single field (plus null).

### 1.3 `mergeable_state` (string)

GitHub’s REST API reference marks **`mergeable_state`** as a required string but **does not document an official enum**. From community usage and support threads, the values that appear in practice include:

| Value       | Typical meaning                                      |
|------------|-------------------------------------------------------|
| `unknown`  | Mergeability not computed yet (accompanies `mergeable: null`) |
| `clean`    | No conflicts; can be merged (accompanies `mergeable: true`)     |
| `dirty`    | Merge conflicts (accompanies `mergeable: false`)               |
| `unstable` | Mergeable from a conflict perspective but has failing or pending status/CI checks |
| `blocked`  | Cannot merge (e.g. branch protection, required reviews)        |

So the API **does deliver** something we can use for “conflict” (dirty / mergeable false), “mergeable” (clean / mergeable true), and “don’t know” (unknown / mergeable null). For “checks running”, we interpret **`unstable`** (and possibly `blocked`) as “checks or other requirements in progress / not satisfied”, and map that to a “checks running” badge (#d29922) in the UI. **`blocked`** is implemented as a separate state: orange badge (#e67700) with tooltip “Awaiting approval”.

---

## 2. How to Get Mergeability in the App

### 2.1 Current flow

1. Page loads; workspace repos and branches are available.
2. Background: `GET /repos/{owner}/{repo}/pulls?state=all&head=owner:branch&per_page=1` → one PR per repo (if any).
3. Response is mapped to `PullRequestInfo` (number, state, merged_at, html_url) and shown as **none** / **create** / **#number** / **merged**.

The **list** response may already include `mergeable` and `mergeable_state`; the exact schema is “array of pull request” and the Get-a-pull-request docs confirm those two fields exist on the pull request object.

### 2.2 Option A: Use list response only

- If the list endpoint returns `mergeable` and `mergeable_state` in the items, extend the app’s DTO and `PullRequestInfo` to carry them.
- No extra request per repo.
- **Risk:** If the list endpoint omits these fields in some contexts, we would show “unknown” (gray) until we add Option B.

### 2.3 Option B: Get single PR when we have a number

- After getting the PR number from the list (or from a first request), call **`GET /repos/{owner}/{repo}/pulls/{pull_number}`** for that repo.
- Use this response as the source of truth for `mergeable` and `mergeable_state`.
- **Cost:** One extra request per repo that has an open PR.
- **When:** Either always for open PRs, or only when the list response does not include mergeability (e.g. both null/empty).

### 2.4 Recommendation

- **First:** Extend the **list** response DTO and parsing to read `mergeable` and `mergeable_state` if present. If they are always present and reliable, no extra call is needed.
- **Fallback:** If the list response omits them or they are always null, add a second step: for each open PR, call **GET single pull request** and use that for mergeability. Optionally cache for a short time to avoid repeated calls when the user refreshes.

---

## 3. Data Model (Proposed)

### 3.1 Extend GitHub DTO

On the object that represents a pull request (list item or single PR), ensure:

- **`mergeable`** (bool?): `true` / `false` / `null`
- **`mergeable_state`** (string): e.g. `"unknown"`, `"clean"`, `"dirty"`, `"unstable"`, `"blocked"`

(If the list endpoint uses a “simple” schema without these, we still need to fetch the full PR via GET single.)

### 3.2 Extend `PullRequestInfo` (app model)

Add to the display model used for the PR badge:

- **`Mergeable`** (bool?) – null = unknown, true = mergeable, false = conflict (or blocked).
- **`MergeableState`** (string?) – raw value from API for mapping to badge color.

---

## 4. Badge Color Mapping (Open PR only)

Apply only when the badge is **open PR** (`#{number}`). Other badges (none, create, merged) are unchanged.

| Condition (in order)                    | Badge color | Label / tooltip   |
|----------------------------------------|------------|-------------------|
| `mergeable == null` or `mergeable_state == "unknown"` | Gray       | “Mergeability unknown” / “Checking…” |
| `mergeable == false` or `mergeable_state == "dirty"`  | Red        | “Merge conflicts” |
| `mergeable_state == "unstable"` (or “checks”)         | #d29922    | “Checks running” or “Failing checks” |
| `mergeable_state == "blocked"`                      | Orange (#e67700) | “Awaiting approval” |
| `mergeable == true` or `mergeable_state == "clean"`   | Green      | “Mergeable”       |
| Any other (e.g. `blocked`)                            | Gray       | “Unknown”         |

So:

- **Gray:** Don’t know at all (null / unknown) or other/unmapped state.
- **Green:** Mergeable (clean, no conflict).
- **Red:** Conflict (dirty or mergeable false).
- **#d29922:** Checks (unstable = checks running or failing).

---

## 5. Null / “unknown” and Retries

GitHub often returns `mergeable: null` and `mergeable_state: "unknown"` on first load and fills them in after a background job. Options:

- **A)** Show gray “unknown” and do **not** retry; next page load or manual refresh will get the updated value.
- **B)** After a short delay (e.g. 2–3 seconds), **retry** once per PR that is still null/unknown, then update the badge (good for real-time feel; more logic and one extra request per such PR).

Recommendation: start with **A**; add **B** later if desired.

---

## 6. Implementation Outline (When Approved)

1. **GitHub DTO:** Add `Mergeable` (bool?) and `MergeableState` (string) to the pull request DTO used for list and/or get.
2. **GitHubService:** If using Option B, add a method that calls `GET /repos/{owner}/{repo}/pulls/{pull_number}` and returns the same DTO (with mergeability). Ensure list response parsing maps these fields when present.
3. **PullRequestInfo:** Add `Mergeable` and `MergeableState`.
4. **GitHubPullRequestService:** When building `PullRequestInfo` from the API response, set the new fields. If list doesn’t provide them, call GET single PR for open PRs and merge into `PullRequestInfo`.
5. **PRBadge:** For the open-PR branch only, choose CSS class for the `#{number}` badge based on the mapping in §4 (gray / green / red / #d29922).
6. **CSS:** Add classes e.g. `.pr-badge-open-unknown`, `.pr-badge-open-mergeable`, `.pr-badge-open-conflict`, `.pr-badge-open-checks` with the chosen colors.

---

## 7. Summary

- **Yes, the GitHub API delivers mergeability:** via **`mergeable`** (true / false / null) and **`mergeable_state`** (e.g. unknown, clean, dirty, unstable, blocked) on the pull request object.
- **Source:** These are guaranteed on **GET /repos/{owner}/{repo}/pulls/{pull_number}`**; they may also be present on the **list** response.
- **Proposed badge colors for `#{number}`:** Gray (unknown), Green (mergeable), Red (conflict), #d29922 (checks running / unstable).
- **Implementation:** See PR column and PRBadge component; mergeability is loaded from list response with fallback to GET single PR when unknown.
