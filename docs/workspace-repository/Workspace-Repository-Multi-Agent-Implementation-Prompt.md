# GrayMoon Workspace Repository - Multi-Agent Implementation Prompt

> **Superseded in part (2026-10-08):** the `workspaceRepository` Worker capability (`supportedFeatures`, `GetCapabilities`, `IWorkerFeatureSupportService`) and the Repositories-page compatibility banner were removed. App/Worker compatibility is now the version lock (`WorkerVersionPolicy`), and Restore validates `.graymoon.json` before creating anything; "Restored without definition" no longer exists. See `docs/feedback/graymoon-restore-workspace-and-worker-version-lock.md`.

Paste this whole file as the first message to the orchestrating model. It runs the plan in
`Workspace-Repository-Implementation-Plan.md` by delegating one plan unit at a time to subagents and
integrating their work wave by wave.

---

## Role

You are the **orchestrator** for implementing "Workspace as a Git Repository" in the GrayMoon repository
checked out at the path the user gives you (the folder that contains `GrayMoon.slnx`, `CLAUDE.md` and
`AGENTS.md`). You do not write feature code yourself except for Unit O. You assign units to subagents,
verify their output, integrate it, and keep the plan document truthful.

Optimize for:

```text
no regression in existing behaviour
exact adherence to the three documents
small reviewable units
green build and green tests after every integration
a plan file that always reflects reality
```

Do not optimize for speed.

---

## Settings (the user fills these in before pasting)

```text
REPO_PATH            = <absolute path to the GrayMoon repo folder>
INTEGRATION_BRANCH   = workspace-as-git-repository
UNIT_BRANCH_PREFIX   = wr/unit-
WORKTREE_PARENT      = <absolute path where unit worktrees may be created, e.g. C:\Users\name\.graymoon\wr-units>
COMMIT_PERMISSION    = none | unit-branches
MAX_PARALLEL_UNITS   = 3
```

`COMMIT_PERMISSION`:

- `none` (default) - nobody commits. After each unit you print the commit message and stop until the
  user says the unit is committed and merged. This matches `AGENTS.md`.
- `unit-branches` - the user explicitly allows you to run `git add` and `git commit` **only on branches
  named `wr/unit-*` inside unit worktrees**, and to `git merge --no-ff` a finished unit branch into
  `INTEGRATION_BRANCH`. Never commit directly on `INTEGRATION_BRANCH`, never on `main`, never `git push`,
  never `--force`, never rewrite history. If any of those would be needed, stop and ask.

---

## Required documents

All in `docs/workspace-repository/`. Read them completely before anything else, in this order:

```text
1. Workspace-Repository-Implementation-Plan.md          (state, units, steps, rules for agents)
2. Workspace-Repository-Design-Supplement-v3.1.md       (binding decisions D1-D15)
3. Workspace-Repository-Design-Review-2026-10-06.md     (why the decisions exist)
4. GrayMoon-Workspace-As-Git-Repository-Design-v3.md    (architecture and invariants)
```

Also read `CLAUDE.md` and `AGENTS.md` at the repo root completely.

Authority order when documents disagree:

```text
1. AGENTS.md and CLAUDE.md             (repository rules, always win)
2. Design Supplement v3.1              (D1-D15)
3. Implementation Plan                 (steps and sequencing)
4. Design v3
5. this prompt
```

Do not reinterpret a decision. If a step cannot be done as written, the unit becomes `BLOCKED` and you
report to the user; you do not design around it.

---

## Code baseline

The plan was verified against:

```text
branch   workspace-as-git-repository
commit   f7f94ce  (parent ee5b536, includes workspace profiles a6d951d)
build    dotnet build GrayMoon.slnx -> 0 warnings
tests    App 999/999, Worker 318 + 1 skipped, Common 236/236
```

Before Wave 0: check out `INTEGRATION_BRANCH`, run the build and the three suites, and confirm the
numbers above (the skipped Worker test is a pre-existing conditional GitVersion test). If they differ,
stop and report; do not start.

---

## Absolute rules (repeat these verbatim to every subagent)

```text
Edit only the files listed under "Files owned" for your unit.
Read only what the unit's "Reading list" and "Files to read" name.
Use the exact type, file, member and test names written in the plan.
CRLF line endings. ASCII hyphen only - never U+2013 or U+2014 anywhere.
sealed classes, primary constructors, [JsonPropertyName] on Worker DTOs.
Never inject AppDbContext into a Blazor component; use IDbContextFactory<AppDbContext>.
Every schema change = entity config in AppDbContext*.cs + a new entry in Migrations.StrictSteps.
Never commit, never push. End with a one-line commit message in a fenced block.
If a step cannot be done as written: set status BLOCKED, write why in the Handoff log, stop.
Do not refactor, rename, or clean up anything the steps do not mention.
For every replace-all step: record the search count before, replace, record the count after.
Update the plan file (status, Handoff log, Discoveries, Follow-ups) before you finish.
```

---

## The orchestrator loop

Work wave by wave exactly as the plan's wave table says:

```text
Wave 0  O              (you do this one yourself)
Wave 1  A, W1, B       (parallel)
Wave 2  C, W2          (parallel)
Wave 3  D
Wave 4  E, F           (parallel)
Wave 5  G
Wave 6  I
```

For each wave:

### 1. Prepare

- Confirm every dependency unit of the wave is `DONE` in the plan.
- For each unit in the wave, create a worktree and branch from the current `INTEGRATION_BRANCH` tip:

  ```powershell
  git -C REPO_PATH worktree add WORKTREE_PARENT\<id> -b wr/unit-<id> INTEGRATION_BRANCH
  ```

- Set the unit's status to `IN PROGRESS` in the plan (on `INTEGRATION_BRANCH`; this is the only file you
  edit outside Unit O).

### 2. Delegate

Launch at most `MAX_PARALLEL_UNITS` subagents, one per unit, each with the **Subagent prompt** below filled
in. Give each subagent the absolute path of its own worktree, never the integration checkout.

### 3. Verify each returned unit

In the unit's worktree, run yourself (do not trust the subagent's numbers):

```powershell
dotnet build GrayMoon.slnx
dotnet test src/GrayMoon.Common.Tests/GrayMoon.Common.Tests.csproj --no-build
dotnet test src/GrayMoon.Worker.Tests/GrayMoon.Worker.Tests.csproj --no-build
dotnet test src/GrayMoon.App.Tests/GrayMoon.App.Tests.csproj --no-build
git -C <worktree> status --short
git -C <worktree> diff --stat INTEGRATION_BRANCH
```

Then check, and reject the unit back to its subagent with a precise list if any item fails:

```text
[ ] build has 0 warnings and 0 errors
[ ] all three suites pass; test counts only went up
[ ] every changed file is in the unit's "Files owned" (compare diff --stat against the plan)
[ ] every new test named in the unit's steps exists
[ ] the unit's "Acceptance" row is satisfied literally
[ ] no U+2013 / U+2014 in changed files: Select-String -Path <files> -Pattern "[\u2013\u2014]" prints nothing
[ ] changed .cs/.razor/.md files are CRLF
[ ] no "git commit" happened unless COMMIT_PERMISSION = unit-branches
[ ] the plan file in the worktree has the unit's Handoff log filled with the template, status REVIEW
[ ] Discoveries written by the subagent are things you must now triage (see step 5)
```

### 4. Integrate

- `COMMIT_PERMISSION = none`: print the subagent's commit message and the worktree path; tell the user
  to commit on `wr/unit-<id>` and merge into `INTEGRATION_BRANCH`; wait.
- `COMMIT_PERMISSION = unit-branches`: in the worktree `git add -A && git commit -m "<message>"`; then in
  the integration checkout `git merge --no-ff wr/unit-<id>`. On a conflict in any file other than the
  plan document, abort the merge and ask the user. Conflicts in the plan document you resolve by keeping
  both units' Handoff logs.
- After every merge, re-run build and all three suites on `INTEGRATION_BRANCH`. If anything fails, the
  wave is not done; fix forward only by re-delegating to the responsible unit's subagent in its worktree.
- Set the unit to `DONE` in the plan, update "Overall status" (current phase, last verified numbers).
- Remove the worktree: `git worktree remove WORKTREE_PARENT\<id>`. Keep the branch.

### 5. Triage Discoveries

For every entry a subagent wrote under "Discoveries":

- If it is a bug in existing code unrelated to this feature: leave it, move it to "Follow-ups".
- If it is coupling that blocks a later unit: add a numbered note to that unit's steps **before** the unit
  starts, and record the change under "Decisions made during execution" with the date.
- If it contradicts a supplement decision: stop and ask the user. Do not change D1-D15 yourself.

### 6. Report to the user after every wave

```text
Wave N complete.
Units: <id DONE / BLOCKED ...>
Integration build: 0 warnings. Tests: App x/x, Worker x/x (+1 skipped), Common x/x
New tests this wave: <count>
Discoveries triaged: <count> (<n> follow-ups, <n> plan notes, <n> need your decision)
Next wave: <units>, starting after your go-ahead.
```

Wait for the user's go-ahead before the next wave. Between waves is where the user reviews.

---

## Unit O (you do this yourself)

Follow the plan's Unit O steps literally on a worktree `wr/unit-O`. It is contracts only: nine files, no
behaviour. The build must stay green (the plan chose the green-build option: add `GetWorkerArgsAsync`
beside the existing tuple method; do not change or delete the old one). Verify with the step-3 checklist,
integrate, then start Wave 1.

---

## Special instruction before Wave 4

W1 must be present in a Worker build the user can run before Units E, F or G can be tested end to end
(supplement D2: the App refuses to enable a Workspace repository until the Worker reports the
`workspaceRepository` feature). After Wave 2 is integrated, tell the user explicitly:

```text
Worker changes from W1/W2 are integrated. Build and install the Worker from INTEGRATION_BRANCH before
Wave 4 so the manual checks in Units E, F and G can run against a Worker that reports supportedFeatures.
```

---

## Subagent prompt (fill in the angle brackets, send as one message)

```text
You are implementing Unit <ID> - <unit title> of the GrayMoon "Workspace as a Git Repository" feature.

Work ONLY inside this worktree: <absolute worktree path>
Branch: wr/unit-<ID>  (already checked out; do not switch branches)

Read, in this order, and nothing else:
1. <worktree>\CLAUDE.md - sections: Coding conventions; Architecture (first two bullets); Database schema; DbContext lifetime
2. <worktree>\AGENTS.md - sections: DbContext handling; Feature-context scoping; Never commit or push
3. <worktree>\docs\workspace-repository\Workspace-Repository-Design-Supplement-v3.1.md - only decisions: <list from the unit's Decisions row>
4. <worktree>\docs\workspace-repository\Workspace-Repository-Implementation-Plan.md - sections: "Rules for agents", "Handoff template", "Standard commands", and the section "Unit <ID> - ..." in full
5. The files listed in the unit's "Files to read" row

Then do the unit's numbered Steps in order. Rules:

- Edit only the files in the unit's "Files owned". If you need a change elsewhere, write it under
  "Discoveries" in the plan and skip that part of the step.
- Use the exact names (types, files, members, tests) written in the plan. Do not invent alternatives.
- CRLF line endings in every file you create or edit. ASCII hyphen only; never U+2013 or U+2014,
  not even in comments or Markdown.
- sealed classes, primary constructors, [JsonPropertyName("camelCase")] on every Worker DTO property.
- Never inject AppDbContext into a Blazor component. Use IDbContextFactory<AppDbContext>.
- A schema change needs both the AppDbContext entity configuration and a new Migrations.StrictSteps entry.
- For every "replace in every file" step: run the search command given in the step, write down the
  count, do the replacements, run it again, write down the count. Both numbers go in your Handoff log.
- Do not refactor, rename or clean up anything the steps do not mention.
- Never run git commit or git push. Leave changes in the working tree.
- If a step cannot be done exactly as written, set the unit status to BLOCKED in the plan, explain in
  the Handoff log, and stop. Do not design a workaround.

Before you finish:
1. Run exactly: <the unit's Acceptance commands>
2. Run: Select-String -Path <each file you changed> -Pattern "[\u2013\u2014]"   (must print nothing)
3. In the plan file, set Unit <ID> status to REVIEW and fill the Handoff template under "Handoff log"
   with every line completed (files changed with full paths, search counts, build warnings/errors,
   exact test counts, new test names, deviations, discoveries, follow-ups, commit message).
4. Reply with: the filled Handoff template, and a one-line commit message in a fenced code block.
```

---

## When to stop and ask the user

Stop the loop and ask before continuing when any of these happens:

```text
baseline numbers differ from the plan before Wave 0
a subagent reports BLOCKED
a Discovery contradicts a supplement decision D1-D15
a merge conflicts in a code file
build or tests fail on INTEGRATION_BRANCH after a merge and the responsible unit cannot fix it in one retry
a step would require git push, force, history rewrite, or a commit outside wr/unit-* branches
a unit's diff touches a file outside its "Files owned" and the subagent cannot explain why in one sentence
Unit I manual matrix needs a human to click through the UI
```

Never make a product decision. Everything a user would see is already decided in the supplement and the
plan; your job is to make the code match them.
