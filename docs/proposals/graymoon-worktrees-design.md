Worktrees fit GrayMoon unusually well because GM already thinks in terms of a **workspace containing many repositories**. I would not manage worktrees as arbitrary Git folders sprinkled around disk. I’d make them a first-class **workspace-level concept**.

For example, if a GrayMoon workspace currently looks roughly like:

```text
C:\Workspace\AuroraReview\
    aurorareview-common\
    aurorareview-api\
    aurorareview-web\
    aurorareview-ingestion\
```

then I would preserve those as the **primary working trees** and create a dedicated GM-managed area beside them:

```text
C:\Workspace\AuroraReview\
    repos\
        aurorareview-common\
        aurorareview-api\
        aurorareview-web\
        aurorareview-ingestion\

    worktrees\
        feature-metadata-filter\
            aurorareview-common\
            aurorareview-api\
            aurorareview-web\

        fix-search-timeout\
            aurorareview-api\
            aurorareview-ingestion\
```

Conceptually:

```text
Workspace
│
├── Primary repositories
│
│   ├── common
│   ├── api
│   ├── web
│   └── ingestion
│
└── Worktree sets
    │
    ├── feature/metadata-filter
    │   ├── common
    │   ├── api
    │   └── web
    │
    └── fix/search-timeout
        ├── api
        └── ingestion
```

The key idea is that GM should manage a **worktree set**, not merely individual Git worktrees.

## Why "worktree set" matters

Normally Git thinks:

```text
repository
    ├── main working tree
    ├── worktree A
    └── worktree B
```

But GrayMoon has another dimension:

```text
workspace
    ├── repository A
    ├── repository B
    ├── repository C
    └── repository D
```

A feature often spans A + B + C.

So GrayMoon should let you say:

> Create workspace worktree `feature/search-v2`

and GM would create:

```text
worktrees/feature-search-v2/common
worktrees/feature-search-v2/api
worktrees/feature-search-v2/web
```

only for the repositories participating in that feature.

That's much better than making the user manually create three separate Git worktrees.

---

# I'd actually change the physical structure slightly

If your current workspace is essentially:

```text
C:\Workspace\GrayMoon\
C:\Workspace\OtherRepo\
...
```

I wouldn't force a migration immediately.

But for a clean future workspace layout I like:

```text
C:\GrayMoonWorkspaces\
    AuroraReview\
        .graymoon\
        repos\
        worktrees\
```

Example:

```text
AuroraReview\
│
├── .graymoon\
│   ├── workspace.json
│   └── ...
│
├── repos\
│   ├── common\
│   ├── api\
│   ├── ingestion\
│   └── web\
│
└── worktrees\
    ├── feature-search-v2\
    │   ├── common\
    │   ├── api\
    │   └── web\
    │
    └── agent-5472\
        ├── api\
        └── web\
```

There is a significant advantage here:

**you can immediately tell whether you're looking at the canonical checkout or a secondary worktree.**

That becomes important once agents start creating them.

---

# But I would not duplicate every repository

Suppose your workspace has 50 repos.

Creating:

```text
feature-xyz\
    repo1
    repo2
    repo3
    ...
    repo50
```

would be wasteful and confusing.

Instead:

```text
feature-xyz\
    common\
    api\
    web\
```

Only participating repositories get worktrees.

GrayMoon already knows which repos have the branch / PR / dependency change, so it can grow the set dynamically.

For example:

```text
feature/search-v2

Repositories

✓ common
✓ api
✓ web
○ ingestion
○ worker
○ export
```

Then:

```text
[ Add repository ]
```

Selecting `ingestion` runs conceptually:

```bash
git -C repos/ingestion worktree add \
    worktrees/feature-search-v2/ingestion \
    feature/search-v2
```

or creates the branch when necessary.

---

# Important: don't create separate clones

This is what makes worktrees attractive.

You do **not** want:

```text
worktrees/
    feature1/
        api/.git/objects/...
    feature2/
        api/.git/objects/...
```

That's effectively cloning repeatedly.

A Git worktree shares the original repository's object database.

So roughly:

```text
repos/api/.git
        │
        ├── objects
        ├── refs
        └── worktrees
             ├── feature-search-v2
             └── agent-4711
```

and the worktree itself contains a tiny `.git` pointer:

```text
worktrees/feature-search-v2/api/.git
```

pointing back to the original repository metadata.

Disk usage is therefore mostly just another copy of the checked-out files, rather than another copy of history.

---

# I would make branch and worktree separate concepts

Don't assume:

```text
one branch = one GrayMoon worktree
```

because you will eventually want things such as:

```text
feature/search
agent/codex-search-1
agent/codex-search-2
```

or:

```text
main
release/2.1
feature/search
```

I'd model something like:

```text
WorkspaceWorktree
-----------------
Id
WorkspaceId
Name
Path
Purpose
CreatedAt
CreatedBy
State
```

and:

```text
WorkspaceWorktreeRepository
---------------------------
WorkspaceWorktreeId
RepositoryId
Path
Branch
HeadSha
IsDirty
```

Maybe later:

```text
OwnerType
---------
User
Codex
Cursor
Claude
Automation
```

So GM can distinguish:

```text
feature/search-v2
Human worktree

codex-search-v2-42
Codex worktree
```

without trying to infer meaning from directory names.

---

# I would expose it at workspace level

Something like:

```text
Workspace
 ├─ Repositories
 ├─ Git Changes
 ├─ Pull Requests
 ├─ Actions
 └─ Worktrees
```

Then:

```text
Worktrees

Primary
C:\Workspace\AuroraReview\repos
main
42 repositories
2 modified

────────────────────────────────────────

feature/search-v2

3 repositories
Branch: feature/search-v2

common        clean
api           4 changed
web           2 staged

PRs: 2
CI: ✓

[ Open ] [ Git Changes ] [ Remove ]

────────────────────────────────────────

agent/codex-934

2 repositories
Owner: Codex
Age: 47 min

api           modified
web           modified

[ Open ] [ Inspect ] [ Remove ]
```

This starts becoming extremely useful.

---

# A very interesting integration with your Git Changes page

Right now Git Changes is essentially attached to:

```text
workspace
    +
repository working directory
```

I'd generalize it slightly:

```text
GitContext
    WorkspaceId
    WorktreeSetId?
```

Where:

```text
WorktreeSetId = null
```

means the primary workspace.

Then the same UI could switch:

```text
Git Changes

Workspace:
Aurora Review

Working tree:
[ Primary ▾ ]
```

Dropdown:

```text
Primary
feature/search-v2
fix/search-timeout
agent/codex-934
```

Selecting one changes all repository paths behind the scenes.

That means you don't need a separate Git Changes implementation for worktrees.

Your existing operations become:

```text
GetStatus(repositoryPath)
GetDiff(repositoryPath)
Stage(repositoryPath)
Commit(repositoryPath)
```

GM just resolves:

```text
RepositoryId + WorktreeSetId
```

into a physical path.

That's an important architectural point.

**Don't teach every Git service about worktrees.**

Create something like:

```csharp
IRepositoryPathResolver
```

Conceptually:

```csharp
string GetRepositoryPath(
    WorkspaceId workspace,
    RepositoryId repository,
    WorktreeId? worktree);
```

Then everything else works against a normal filesystem path.

That keeps this feature much less invasive.

---

# Creating a worktree could be a really nice GM workflow

Imagine:

```text
New Worktree

Name
feature/metadata-v2

Based on
main

Repositories

☑ common
☑ api
☑ web
☐ ingestion
☐ worker

Branch

● Create feature/metadata-v2
○ Existing branch

[ Create ]
```

GM then runs the relevant worktree commands on each agent.

Result:

```text
Created workspace worktree

feature/metadata-v2

✓ common
✓ api
✓ web

C:\...\worktrees\feature-metadata-v2
```

One click instead of manually orchestrating all the repos.

---

# There's one Git limitation GM needs to handle carefully

Normally Git does not allow the same branch to be checked out in two worktrees simultaneously.

For example:

```text
primary/api       → feature/search
worktrees/x/api   → feature/search
```

Git will normally reject that.

That's actually useful protection.

So GM should make the distinction very visible:

```text
feature/search

Already checked out:
Primary / aurorareview-api
```

and offer choices such as:

```text
Move branch to new worktree
Create new branch
Cancel
```

I would **not** casually use Git's override options to force the same branch into multiple worktrees. That creates very confusing behavior.

---

# Cleanup becomes a first-class GM feature

This is where GrayMoon could be considerably nicer than CLI Git.

After a PR is merged:

```text
feature/search-v2

✓ all PRs merged
✓ branches deleted remotely
✓ worktrees clean

Safe to remove

[ Remove Worktree ]
```

GM could remove:

```text
worktrees/feature-search-v2/common
worktrees/feature-search-v2/api
worktrees/feature-search-v2/web
```

and then prune stale Git metadata.

You could even have:

```text
Worktree cleanup

3 safe to remove

feature/search-v2      merged 4 days ago
fix/export             merged 9 days ago
agent/codex-338        abandoned 2 days ago

[ Clean All ]
```

That's a very good GM feature.

---

# Agent-created worktrees are potentially even more valuable

This is where I think the feature becomes strategically interesting.

Suppose you tell an AI coding agent:

> Refactor metadata filtering.

Instead of allowing it to touch your current checkout, GM could automatically create:

```text
worktrees/
    agents/
        codex-20260828-1314/
            common/
            api/
            web/
```

Then:

```text
Main workspace
✓ untouched

Codex worktree
● working

Cursor worktree
● working
```

You could have two agents independently working on the same workspace without fighting over files.

Conceptually:

```text
                  main workspace
                        │
           ┌────────────┼────────────┐
           │            │            │
       Codex #1      Codex #2      User
           │            │            │
      worktree A    worktree B   primary
```

This is exactly the kind of problem Git worktrees solve very elegantly.

---

# Directory naming

I would avoid using raw branch names directly:

```text
feature/search/metadata
```

because `/` creates nested paths.

Instead GM generates a filesystem-safe worktree name:

```text
feature-search-metadata
```

while storing the real branch separately:

```text
Display name:
feature/search/metadata

Directory:
feature-search-metadata

Branch:
feature/search/metadata
```

For agents:

```text
codex-7f12a4
cursor-a81d20
```

You might eventually allow a friendly name:

```text
Search Refactor
```

rather than exposing Git branch names everywhere.

---

# One thing I would definitely avoid

Don't put worktrees **inside the repository that owns them**, like:

```text
api/
    .git/
    worktrees/
        feature-x/
```

That tends to create:

- accidental discovery by tooling
- IDE indexing weirdness
- file watcher noise
- `.gitignore` headaches
- recursion problems in workspace scanning

Keep them as siblings outside all repositories:

```text
workspace/
    repos/
    worktrees/
```

or, if you don't want to change the current workspace structure:

```text
C:\Workspace\
    aurorareview-api\
    aurorareview-web\

    .graymoon-worktrees\
        AuroraReview\
            feature-search\
                aurorareview-api\
                aurorareview-web\
```

That would be a very reasonable backward-compatible design.

---

## For GrayMoon specifically, I'd build it in three stages

**V1 — Worktree awareness**

GM detects existing Git worktrees and displays:

```text
Repo
Branch
Path
Dirty
HEAD
```

No creation yet.

This makes sure your existing Git infrastructure doesn't accidentally treat worktrees as strange repositories.

**V2 — Workspace Worktree Sets**

Add:

```text
Create Worktree
Add Repository
Remove Repository
Open Worktree
Git Changes for Worktree
Delete Worktree
```

This is where it becomes genuinely useful for normal development.

**V3 — Agent Workspaces**

Allow Cursor/Codex/etc. jobs to say:

```text
Create isolated worktree
```

and GM handles:

```text
creation
repository selection
branch naming
tracking
status
PR creation
cleanup
```

This last part could become a major GrayMoon differentiator.

### The model I'd use

The important conceptual change I'd make is this:

```text
Today:

Workspace
    └── Repository
         └── Path
```

becomes:

```text
Workspace
    │
    ├── Repository
    │
    └── Working Context
           │
           ├── Primary
           ├── feature/search
           ├── fix/export
           └── agent/codex-123
```

and then:

```text
Working Context
      +
Repository
      ↓
Physical repository path
```

Once that abstraction exists, **Git Changes, commits, diffs, builds, tests, PR creation, agents and even your Workspace Repository page can all operate against a worktree without caring that it's a worktree**.

That's how I'd design it rather than bolting `git worktree` commands onto the existing repository UI.
