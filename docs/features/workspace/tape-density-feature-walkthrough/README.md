# Feature walkthrough: `tape-density` (multi-repo development)

This walkthrough demonstrates how to develop a feature across a multi-repository GrayMoon workspace, using a realistic case scenario in `MezzoRecovery`.

## What this walkthrough is showing

GrayMoon helps you coordinate the full lifecycle of a cross-repo feature:

1. Create a feature branch across multiple repositories in one place (optionally including dependency rollouts).
2. Apply and validate a baseline implementation plan locally (you implement the actual changes).
3. Demo what changed in GrayMoon (status, diffs, and the â€œwhat is ready to commit/pushâ€ signals).
4. Create one coordinated set of pull requests across the impacted repositories.

In this specific case, we are implementing the plan titled **â€œTape density LTO generation trackingâ€** (your existing plan file: `tape_density_lto_generation_tracking_cbf37004.plan.md`).

## Privacy / redaction rules for this doc

To keep your private project details safe:

- We will avoid deep code snippets and internal implementation specifics from the `MezzoRecovery` repo.
- We will freely describe everything else that is not private: repository names, libraries/packages, dependency relationships, and the user-visible behavior in GrayMoon.
- Any code-level detail will be summarized at a â€œuser-relevant outcomeâ€ level (what the feature does, not how each method is written).

## Workspace used in the demo

- Workspace: **MezzoRecovery**
- Workspace id: **2**
- Route: `/workspaces/2`
- URL (for your browser): `http://localhost:8384/workspaces/2`

## How the steps are controlled (pause points)

You said you want every step controlled by you. This doc is organized as â€œpause pointsâ€:

- I will describe what to do in GrayMoon and what to look for.
- You will perform the action.
- You will confirm what happened (or paste any user-visible output).
- Only then will I move to the next step and capture the next screenshot(s), if needed.

When a step involves a potentially destructive action (reset, push, etc.), the doc includes an explicit â€œconfirm before proceedâ€ note.

## Step plan (high level)

### Step 1 - Create the feature branch: `tape-density`

Documented in [Phase 2 - New Feature](phase-2-new-feature.md) (screenshots captured from `/workspaces/2`).

Goal: Use GrayMoon to create a coordinated feature branch across the workspace.

What GrayMoon did in Phase 2:

- Open **New Feature** from the Branch menu.
- Branch name `tape-density`, based on `main`, **Update dependencies** and **Push changes** on.
- One job: create branches, commit dependency updates per level, synchronized push with NuGet wait.

Pause point: implement the baseline plan locally (your execution). See [Phase 2 - Baseline implementation (AI coding)](phase-2-baseline-implementation.md).

### Step 2 - Baseline implementation (you execute)

Documented in [Phase 2 - Baseline implementation (AI coding)](phase-2-baseline-implementation.md).

Goal: Implement the tape-density plan locally with your AI IDE against the full sibling-repo workspace. GrayMoon already prepared the branch; **Changes** starts empty (`0 of 11 repositories`).

Progress captured in the baseline doc: first edits (3 repos), growing to **5 of 11 repos**, collapsed/expanded tree, staging/unstaging, and commit-message demo.

### Step 3 - Commit, push, and GitHub Actions

Documented in [Phase 3 - Commit, push, and GitHub Actions](phase-3-commit-push-gha.md).

**Done:** Commit All (one message, five repos), notification card, **Push Updated**, synchronized push overlay, Actions before/after push, **none** filter, **Run Deploy MezzoRecovery to VPS** on **`tape-density`**, sidebar navigation between Repositories and Actions.

When you are ready, ask for Step 4 (Create PRs).

### Step 4 - Explain and create coordinated PRs

Goal: Create pull requests for the set of repositories affected by the feature.

What we will demonstrate:

- GrayMoonâ€™s PR creation flow for multi-repo workspaces.
- How dependency ordering and â€œonly PR what is neededâ€ reduces noise compared to a traditional approach.
- How PR labels and statuses help you keep the team aligned.

Pause point: You will confirm PR titles, base branch, and reviewers (you control those inputs).

## Benefits vs a traditional multi-repo workflow

In this scenario, the walkthrough highlights these â€œday to dayâ€ benefits:

- One UI controls the feature branch story across many repos (instead of hand-coordinating branch names and checkouts).
- When dependency rollouts matter, GrayMoon can apply them in a coordinated order and keep the â€œrestore and pushâ€ story consistent (reducing CI surprises).
- Git hooks update GrayMoonâ€™s view of what is â€œready to push/pullâ€ even when you change repos outside the app.
- PR creation is coordinated: less risk of missing a repo, and less PR churn from â€œaccidentalâ€ changes.

## Phases

- **Phase 1 (Preparation):** [phase-1-preparation.md](phase-1-preparation.md)
- **Phase 2 (New Feature):** [phase-2-new-feature.md](phase-2-new-feature.md) - create `tape-density` branch, dependency update, synchronized push
- **Phase 2 (Baseline - AI coding):** [phase-2-baseline-implementation.md](phase-2-baseline-implementation.md) - multi-repo workspace for AI, empty Changes baseline, **start coding here**
- **Phase 3 (Commit + push + Actions):** [phase-3-commit-push-gha.md](phase-3-commit-push-gha.md) - Commit All, Push Updated, GHA live feed
- **Phase 4 (PRs):** coordinated PR creation (on your signal)

## Remaining PR clarifications (we will pause until you tell me to start Step 4 / PRs)

- PR base branch target (example: `main`).
- Which repositories to open PRs for (all impacted repos vs only a smaller subset you pre-select).

Also: no additional plan/code details will be shown beyond user-relevant outcomes.

