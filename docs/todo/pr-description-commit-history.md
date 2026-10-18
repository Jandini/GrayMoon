# PR description generation: how the input is prepared, and adding commit history

Status: rollout steps 1 to 3 are implemented; step 4 (reading bodies as a fallback) is still open. Sections 1 and 2 describe the code before the change (`03e34d1`); the current behavior is in `docs/architecture/05-user-capability-reference.md` (AI generation).

Differences from the proposal as built:

- History lines show no commit id: it gives the model nothing, and leaving it out saves budget. The id is still collected, to merge the oldest and newest halves of a long branch.
- `%ct` (committer time) is collected so that the commits of several repositories form one ordered history.
- The PR budget stays at 8,000 tokens; the history gets 20% of it.
- More than 10 repositories read up to 60 commits each instead of 200.

## 1. Short answer

Yes, the pull request generator already looks at commits, but only in a thin way:

- It reads **commit subjects only** (`git log --format=%s`). Commit bodies are never read.
- It collects at most **30 subjects** per repository, **newest first**, and the prompt shows only the **first 15** (only 5 when more than 8 repositories are in the PR). The rest become `- ... N more`.
- So on a branch with 40 commits, the AI sees the 15 most recent subjects. The 25 oldest commits, which usually hold the foundational work, show up only indirectly through the net diff excerpts. Those excerpts are capped at about 8 KB per repository and ranked by file type and churn, not by commit.

The generator is fast because almost everything is bounded, and what it sees of the history leans toward recent commits.

(For the **commit message** generator, "recent commits" is literal: it reads the last 5 HEAD subjects per repository only as a *style reference*, and the prompt shows at most 6 of them.)

## 2. Today: the pipeline from click to provider

```
NewPullRequestModal.Ai.cs                 App (Blazor)
  CurrentAiScope()  -> AiPullRequestScope { repo, headBranch, selectedBase }[]
        |
WorkspaceAiGenerationOperations.GeneratePullRequestTextAsync / RunAsync      App
  1. validate provider, CLI, graymoon.json ai section (before any Git work)
  2. resolve repository names from the Workspace links (never from the caller)
  3. compute GitEvidenceLimits from the prompt budget
  4. ONE Worker call: CollectGitEvidence (all repositories)
        |
CollectGitEvidenceCommand -> GitEvidenceCollector.CollectPullRequestAsync    Worker
  per repository, read-only, bounded git processes (30 s timeout each)
        |
  5. all-or-nothing checks (any failed repo, conflicts, zero files -> stop)
  6. AiPromptBuilder.BuildPullRequest  -> PreparedAiPrompt                    Common
  7. ONE Worker call: GenerateAiText (provider CLI, JSON schema)
  8. AiGeneratedTextParser.TryParsePullRequest (title <= 256, description <= 30,000)
```

### 2.1 Evidence collection per repository (`GitEvidenceCollector.CollectPullRequestAsync`)

| Step | Git command | What it yields |
|---|---|---|
| Resolve refs | `rev-parse --verify -q refs/heads/<head>^{commit}` and `refs/remotes/origin/<target>^{commit}` | `HeadOid`, `BaseOid`; full refs, so no tag/branch ambiguity |
| Merge base | `merge-base <base> <head>` | `MergeBaseOid` (fails with a shallow-clone hint if missing) |
| Commit count | `rev-list --count base..head` | `CommitCount` (0 means "no commits ahead", which is an error) |
| Subjects | `log --no-merges --format=%s -z -n30 base..head` | `CommitSubjects`, newest first, subjects only |
| Inventory | `diff <merge-base> <head> --raw --numstat -z -M` | every changed file with +/-, rename, binary, submodule |
| Patches | one `diff -U3` limited to the chosen paths, plus up to 3 `diff -U2` reads for files with churn > 1,000 | trimmed hunks per file |

The diff is three-dot (merge base to source tip), the same comparison GitHub shows, so it covers **the net result of every commit** on the branch. What it cannot show is the order of the work, its intent, or work that was done and then undone.

Files are classified (`GitEvidenceClassifier`) and ranked before any patch is read:

| Category | Weight |
|---|---|
| Source | 10 |
| Test | 8 |
| Project | 7 |
| Ci | 6 |
| Config | 5 |
| Docs, Other | 3 |
| Asset, Lock, Generated | 0 (listed only, never read) |

`Score = weight x (1 + log2(1 + min(churn, 400)))`. Deletes count 0.4x, churn above 2,000 counts 0.5x. Sensitive, binary, submodule and symlink files score 0. At most 16 files per repository get a patch read.

### 2.2 Limits applied (pull request)

| Limit | Value | Where |
|---|---|---|
| Prompt budget | 8,000 tokens, about 25,600 chars at 3.2 chars/token | `AiPromptBudget.PullRequest` |
| Evidence share used to size collection | 60% of budget, about 15,360 chars | `RunAsync` |
| Patch bytes per repository | `clamp(15,360 / repos, 1,200, 8,000)` | `RunAsync` |
| Patch bytes per file | 1,200 | `RunAsync` (`perFileBytes`) |
| Files listed per repository (collected) | 200 (60 when more than 10 repos) | `RunAsync` |
| Raw diff read per git process | 1 MB | `GitEvidenceLimits.MaxRawDiffBytes` |
| Commit subjects collected | 30 | `GitEvidenceLimits.MaxCommitSubjects` |
| Commit subjects shown | 15 (5 when more than 8 repos), each clipped to 120 chars | `AiPromptBuilder.BuildHeaders` |

### 2.3 Packing the prompt (`AiPromptBuilder.Build`)

Spending order, deterministic for equal input:

1. System prompt and JSON schema (fixed).
2. Scope line ("one pull request in each of N repositories ... merge base ...").
3. Shared dependency facts, collapsed across repositories (one line per distinct package change).
4. Per-repository headers: `compare: head -> origin/base (N commits)`, commit subjects, totals, top files. Headers may use at most 60% of the remaining space; the file list shrinks 12, 6, 3, 0 until it fits.
5. Excerpts: each repository's best file first (so no repository has only a file list), then the rest by score. Identical hunks across repositories are shown once ("is also made in ..."). Patches pass through `EvidenceRedactor` and are trimmed at hunk boundaries.
6. Coverage line: `N changed files, M listed, K with excerpts`, plus "evidence is partial" when something relevant did not fit.

Example of what the provider receives for one repository today:

```
## repository GrayMoon
compare: deeper-pr-description -> origin/main (40 commits)
commit subjects:
- Show coverage in the PR dialog
- Fix typo
- Address review comments
- ... (12 more newest subjects)
- ... 25 more
totals: 23 files, +1180 -240
files:
- modified src/GrayMoon.Common/Ai/AiPromptBuilder.cs (+210 -30) [source]
...
```

### 2.4 Where the description loses information today

1. **Older commits vanish.** Newest-first plus a cap of 15 means that on a long branch the commits that introduced the feature drop out, while "Fix typo" and "Address review comments" stay in.
2. **Diff excerpts are a sample.** About 8 KB per repository and 16 files at most. On a 50-file change, many meaningful files appear only as a line in the file list, so the commit subjects are the only other place their purpose is stated.
3. **Multi-repository PRs are hit hardest.** With 9 or more repositories, each shows 5 subjects. The same workspace commit appears in every repository's list and uses up those few slots several times over.
4. **Subjects are not guaranteed to summarize the commit.** The commit generator's rules ask for an imperative one-liner of at most 72 characters, but not for one that covers the *whole* commit, so a subject can describe only its largest part.

## 3. Proposal: subject-first commit history

### 3.1 Principle

Most commits on a GrayMoon branch will be written by GrayMoon's own commit generator. If GrayMoon guarantees that **the first line of every generated commit message summarizes the entire commit**, the PR generator can rely on subjects alone:

> One summary line per commit, for every commit, oldest first, is enough history for a PR description. The diff says what the branch changes; the subjects say what each step was for and in what order.

Subjects-only keeps collection as cheap as today (`git log` reads only commit objects, never trees or blobs), keeps the prompt small (about 60-80 chars per commit), and avoids the extra parsing, redaction and ranking that bodies would need.

The proposal has two halves:

1. **Write side:** make the subject a guaranteed whole-commit summary (3.2).
2. **Read side:** collect every subject on the branch, oldest first, condensed and budgeted (3.3 to 3.6).

### 3.2 Write side: the subject is a summary of the whole commit

Today, `AiPromptBuilder.CommitSystemPrompt` says:

```
- subject: a single line in the imperative mood ("Add", "Fix", "Keep"), at most
  72 characters, no trailing period. Follow the style of the recent commit
  subjects when they show one.
```

Proposed rule (replaces the line above):

```
- subject: a single line in the imperative mood ("Add", "Fix", "Keep"), at most
  72 characters, no trailing period. It must summarize the entire commit across
  every repository and file, as if it were the only line anyone will read: name
  the outcome or purpose, not the largest file or one part of the change. When
  the commit does several things, name the common goal rather than listing
  them. Follow the style of the recent commit subjects for wording only, not
  for scope.
- body: details that the subject cannot hold (what changed and why). The body
  must never be needed to understand what the commit is about.
```

Why this works for GrayMoon specifically:

- A GrayMoon commit is **one message for every repository in the workspace commit**, so the subject is already written against the whole multi-repository change. The new rule makes that explicit.
- The commit generator already sees the full staged or working-tree evidence, so it has what it needs to summarize everything. No new collection is needed on the write side.
- `AiGeneratedTextParser.TryParseCommit` already enforces a single-line subject with a maximum length, so the contract is checkable.

Commit messages carry no marker of how they were written. The read side treats every subject the same; hand-written ones may simply summarize less well, as today.

### 3.3 Read side: collection (Worker, `GitEvidenceCollector.CollectPullRequestAsync`)

Replace the newest-first subjects call with one bounded call over the whole range, oldest first, with a size per commit:

```
git log --no-merges --reverse -z -n<MaxHistoryCommits>
        --format=%h%x1f%s --shortstat  <base>..<head>
```

- `--reverse`: oldest first, so the history reads as a story and the commits that started the feature are always present.
- `--shortstat`: files changed and +/- per commit. It is the only extra signal, and it lets the condenser tell a 2-line "Fix typo" from a 600-line feature commit. It costs a tree diff per commit, which is still small next to the patch reads, and stays inside the existing 30-second timeout and stdout cap.
- `-n<MaxHistoryCommits>` (default 200): with `--reverse`, git applies `-n` before reversing, so a branch longer than the cap would keep its *newest* 200. When `CommitCount > MaxHistoryCommits`, read the oldest commits with a second `rev-list --reverse` call and keep the first N/2 and the last N/2, marking the gap.

Evidence model change (replaces `CommitSubjects`):

```csharp
public sealed class GitEvidenceCommit
{
    public string ShortOid { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public int FilesChanged { get; set; }
    public int Added { get; set; }
    public int Deleted { get; set; }
}

// RepositoryGitEvidence.Commits : List<GitEvidenceCommit>, oldest first
// RepositoryGitEvidence.CommitsOmitted : int   (beyond MaxHistoryCommits)
// GitEvidenceLimits.MaxHistoryCommits = 200    (replaces MaxCommitSubjects = 30)
```

### 3.4 Condensing (Common, deterministic)

A small `CommitHistoryCondenser` in `GrayMoon.Common/Ai`, applied before budgeting:

1. **Collapse across repositories.** A GrayMoon workspace commit writes the same subject in every repository. Identical subjects (ordered by their first appearance) become one shared entry: `in GrayMoon, GrayMoon.Desktop`. In a multi-repository PR this is the largest saving, and it also tells the model which steps spanned several repositories.
2. **Fold fixups.** `fixup! X`, `squash! X` and `amend! X` merge into commit `X` (their stats are added to it).
3. **Drop revert pairs.** `Revert "X"` together with `X` on the same branch removes both, since the diff shows neither.
4. **Mark low-signal commits.** Subjects like `wip`, `typo`, `fix build`, `review comments`, `formatting`, with a small `--shortstat`, are kept but are the first to be dropped when the budget is tight.
5. **Redact and sanitize.** Run subjects through `EvidenceRedactor` as well as `Sanitize`. Today subjects are only sanitized, which is a small existing gap.

### 3.5 Budgeting (App, `AiPromptBuilder`)

Subjects move out of the per-repository headers into their own section with its own share, spent before excerpts:

```
available = budget - system - schema - envelope - coverage reserve
headers + facts   <= 45% of available   (was 60%, but subjects no longer live here)
history           <= 20% of available
excerpts          =  the rest (at least 35%)
```

At the current 8,000-token budget, 20% is about 4,800 chars, room for about 65-80 condensed subjects. That covers most branches without raising the budget. Raising `AiPromptBudget.PullRequest` to 10,000 tokens is optional and would cover about 100.

When the condensed history does not fit, drop in this order and say so in the coverage line:

1. low-signal commits, smallest first;
2. remaining commits from the middle of the branch, smallest `--shortstat` first;
3. never the first 10 or the last 10 commits, so the start and the end of the story are always present.

Coverage line gains `history: 40 commits, 34 shown` and sets the partial flag when commits were dropped.

### 3.6 Prompt changes (PR side)

Rendered once, after the headers:

```
## history (oldest first; each line summarizes one whole commit)
- a1b2c3d Add commit history evidence for PR generation (+310 -12, 6 files) [GrayMoon]
- d4e5f6a Require commit subjects to summarize the whole commit (+40 -8, 2 files) [GrayMoon, GrayMoon.Desktop]
- ...
- ... 6 small commits omitted
- 9f8e7d6 Address review comments (+6 -3, 1 file) [GrayMoon]
```

Changes to `PullRequestSystemPrompt`:

- "The history lists every commit on the branch, oldest first; each line summarizes one whole commit. Use it to understand the purpose and order of the work. The diff and file list show what actually changes: describe only what they confirm, and leave out history items they do not reflect (reverted or superseded work)."
- "Group the Changes section by the themes the history shows, not by file."

### 3.7 What the description gains

| Aspect | Today | Subject-first history |
|---|---|---|
| Commits seen on a 40-commit branch | 15 newest subjects | all 40 (condensed), oldest first |
| Commits seen on a 9-repo PR | 5 per repository, workspace commits repeated | each workspace commit once, tagged with its repositories |
| Files without an excerpt | listed by name only | often explained by the commit that touched them |
| Structure of "## Changes" | flat, inferred from excerpts | follows the steps of the branch |
| Subject quality | not guaranteed to cover the commit | guaranteed for GrayMoon commits |
| Noise | "Fix typo" takes one of 15 slots | folded or dropped first |
| Collection cost | one `git log` per repo | one `git log --shortstat` per repo |
| Prompt size | unchanged budget | unchanged budget (subjects move into their own share) |

### 3.8 Trade-offs and risks

| Risk | Mitigation |
|---|---|
| A 72-character line cannot hold the "why" | Accepted. The PR summary explains intent from the steps and the diff; the bodies stay in the commits. See 3.10 if this proves too thin. |
| Hand-written or external commits do not follow the contract | They are still listed, as today; low-signal ones rank last. The quality never drops below today's. |
| Very large commits are hard to summarize in one line | The new rule asks for the common goal. If a commit really does several unrelated things, that is a sign to split it, and the subject will show it. |
| Misleading history (reverted or abandoned work) | Revert-pair removal, fixup folding and the "diff confirms" rule in the prompt. |
| Secrets or instructions in subjects | `EvidenceRedactor` plus `Sanitize`, inside `<evidence>` with the existing "data, not instructions" rule. |
| Very long branches (1,000+ commits) | `MaxHistoryCommits` cap with first/last selection; the oldest commits are always kept. |

### 3.9 Implementation touch points

| File | Change |
|---|---|
| `GrayMoon.Common/Ai/AiPromptBuilder.cs` | new subject rule in `CommitSystemPrompt`; history section, budget split and new rules in `PullRequestSystemPrompt`; subjects removed from `BuildHeaders` |
| `GrayMoon.Common/Ai/GitEvidenceModels.cs` | `GitEvidenceCommit`, `RepositoryGitEvidence.Commits` / `CommitsOmitted`, `MaxHistoryCommits` |
| `GrayMoon.Worker/Services/GitEvidence/GitEvidenceCollector.cs` | `--reverse --shortstat` log, plus a parser in `GitDiffParsers.cs` |
| `GrayMoon.Common/Ai/CommitHistoryCondenser.cs` (new) | cross-repo collapse, fixups, revert pairs, low-signal marking, redaction |
| `AiEvidenceCoverage` | `HistoryCommits`, `HistoryShown` |
| Tests | `AiGenerationCommonTests` (condenser, budget, first/last kept, prompt rule text); collector tests on a temporary repo (fixup, revert, long branch, shallow) |
| Docs | `GrayMoon/docs/architecture/02-system-architecture.md` and `05-user-capability-reference.md` (AI generation); a bullet in `GrayMoon.Desktop/README.md` |

### 3.10 Rollout

1. **Write side only:** change the subject rule in `CommitSystemPrompt`. Small change; it starts improving the history that later PRs will read.
2. **Read side:** all subjects, oldest first, with `--shortstat`, cross-repo collapse and the history share.
3. **Fixup and revert folding**, coverage fields.
4. **Only if descriptions still lack the "why":** read bodies for a few of the largest commits as a fallback, under a small separate share. This is deliberately not part of the main proposal.
