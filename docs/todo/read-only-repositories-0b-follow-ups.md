# Read-only repositories - follow-ups from Phase 0B

Date: 2026-11-12. Code: `C:\Workspace\GrayMoon\GrayMoon`, branch `readonly-repos`. Phase 0B is committed (`1c67c21a`).

Phase 0B moved the tag eligibility checks to one policy without changing behavior. Five things were left open. This document says what each one is, what to do about it, and when. The state of each item is in the next section.

## Status - 2026-11-12

Steps 1 and 2 of the resulting order are done and not committed. Details and commands are in the test log ("0B follow-ups").

| Item | State |
|---|---|
| 5 (required part): mutations 1, 2, 5, 7, 8, 9, 10, 12 | Run. Two were not caught (7 in `DependencyUpdateOrchestrator`, 8 in the push plan); both are closed with new tests in `Policy/RepositoryPolicySubjectSourceTests.cs`. Mutation 2 is caught by the matrix test only: no service asks the policy for Sync, so no characterization test can react. |
| 4: two tests that fail together | Cause found for both, and both fixed. One was a defect in the Workspace operation lock (below). The other was a wrong assumption in the test. |
| 1: Sync on a tag by entry point | Done on 2026-11-12, option A (owner decision). See the plan, B.20. |
| 2, 3 and the rest of 5 | Not started. Placed as written under "Required" and "Can wait". |

What changed in the product:

- **`WorkspaceOperationRunner` releases the lock before the operation reports its final state.** Before, the operation reported "completed" first and released the lock after. Code that reacted to "completed" by starting the next operation could be refused by a lock that was about to go. With the fix undone the test fails in 10 of 10 runs together with the other class; with it, 0 of 20.
- **One constant per path for the row a tag is read from** (`RepositorySubjectSources.UpdatePath`, `RepositorySubjectSources.PushPlan`). The update path had eleven call sites that each named the source; a test could cover only the ones it reached. Now one test covers all of them, and the Feature update fix (F6) is a one-line change.

What changed in tests only:

- `AiAndGitChangesSweepTests.Cancel_racing_the_end_of_a_run_never_throws` starts the run from a pool thread. Started from the test itself, the run resumed through the test runner's synchronization context and not inline, so the run could still be marked as running when the test looked. `AiTextGenerationRuns` is not at fault. With the change undone the test fails in 9 of 10 runs; with it, 0 of 20.

The lock fix and the test change are unrelated to read-only repositories. They are in their own files (`WorkspaceOperationRunner.cs`, `AiAndGitChangesSweepTests.cs`) so they can be committed alone.

Related: `read-only-repositories-implementation-plan.md` (section 6, "0B as built"), `read-only-repositories-test-log.md` ("Phase 0B").

## Summary

| # | Item | Proposed action | When | Owner decision needed |
|---|---|---|---|---|
| 1 | Sync treats a tag checkout differently by entry point | Split the operation into two rules; keep today's behavior | 0C, before Phase 1 | Yes: keep the difference or remove it |
| 2 | Skip reporting is partial | Fill the skipped list in the pipelined push lane and Return to Default | 0C | No |
| 3 | Convention test does not see tag reads inside query expressions | Add a source scan beside the compiled-call scan | 0C | No |
| 4 | Two existing tests fail when run together | Find the cause, fix the tests or the code | Separate change, before the 0C gate | No |
| 5 | No mutation checks for the new tests | Run a fixed list of mutations once and record the result | 0C, before the gate | No |

The table says where each item fits best if all of it is done. The next section says which parts are required to complete the work and which can wait.

## Recommendation: what is required and what can wait

The read-only repositories work is complete when a reference repository cannot be changed through any entry point, every skip is visible to the user, and the tag behavior of today is intact. Each item below is judged against that, using the rule table in section 4.1 of the plan.

Short version: of the five items, parts of 1, 2 and 5 and the diagnosis half of 4 are required. Item 3 and the rest of 1, 4 and 5 can wait until after the reference gate.

### Required

| What | Latest point | Why it cannot wait |
|---|---|---|
| **5 (part): mutations 1, 2, 5, 7, 8, 9, 10, 12** | Before 0B is committed | 0B claims that nothing changed, and the tests are the only evidence. These eight check the tag rules, the three subject sources and the counter queries. Phase 1 changes exactly these places on purpose; if a test does not react to a wrong rule now, it will not react to a wrong reference rule later. About 15 short test runs. |
| **4 (part): find the cause (steps 1 and 2)** | Before the 0C gate | One of the two tests is about the Workspace operation lock. If the lock is released after the job is reported as finished, that is a product defect in the mechanism the reference rules rely on to refuse work at dispatch. It has to be known, not guessed. If the cause is only thread pool starvation in the test, the fix can wait (see below). |
| **1 (part): split the commits badge of a row into pull and push** | Phase 1, with the row work | For a reference repository on a branch, Pull is allowed and Push is refused. One `Disabled` flag cannot show that. Without the split the badge either blocks an allowed pull or offers a refused push. |
| **2 (part): fill `Skipped` in the pipelined push lane and in bulk Return to Default** | Phase 1, before skips are shown to the user | Phase 1 shows "N read-only repositories skipped" and adds the list to the REST bodies. Both operations skip reference repositories. If they do not fill the list, the user is told nothing for the two operations where a silent skip is most surprising. |
| **Convention test covers the reference fields** (new, grows out of item 3) | Phase 1, in the change that adds the fields | The test guards the tag getters only. The day `IsReference` and `IsReferenceSnapshot` exist, their getters must be on the guarded list, or reference checks can be written by hand anywhere and nothing fails. This is the cheap half of item 3: the existing compiled-call scan, two more getters. |
| **Mutation 14: a test for the page helper** | Phase 1, with the page work | The page refusals (toast and disabled action) are what the user sees of the feature, and `IsRepoAllowed` has no test. Move it to a static helper that can be tested, as `UiRaceGuards` is. |

Also required, already in the plan and not new here. They are listed because the five items do not replace them:

- **Review and commit 0B, then the 0C gate** with the manual smoke list M1 to M8. Only the owner can do both.
- **F5: page-only checks get a service check.** Otherwise a REST caller walks past every reference rule.
- **F6: the update path stops reading the Workspace checkout for a Feature** (`LegacySharedLink` removed). A reference snapshot is per Feature; a rule that reads the Workspace row gives the wrong answer for it.
- **Entry points outside the Workspace lock ask the policy at dispatch** (decision already made).

### Can wait until after the reference gate

| What | Why it is safe to wait | Cost of waiting |
|---|---|---|
| **1 (rest): `SyncFromRow` and `SyncLevel` rules, `AreAllOnTag`** | Sync is allowed for a reference repository in every column of the rule table. The buttons that read the tag today therefore give the right answer for a reference repository too. | Two OPEN entries stay in the convention test. A level that holds only reference repositories keeps its action buttons enabled; pressing one skips every repository and reports it (once the required part of item 2 is done). Cosmetic. |
| **2 (rest): commit, project refresh, Restore Packages as separate results** | Their skips are the same repositories the update run already reports. | None for the user. |
| **3 (rest): source scan for tag comparisons inside query expressions** | The four header projections are covered by the parity test and the counter characterization tests. The gap is a new, fifth projection written by hand, and Phase 1 adds terms to the existing four, not a new one. | A hand-written comparison in a new query would not be caught automatically; a reviewer has to see it. |
| **4 (rest): the fix, if the cause is test-only** | A full run that fails at random can be run again and the failure named in the log, as in 0B. | Every gate needs a second look when one of the two tests fails. Annoying, not dangerous. |
| **5 (rest): mutations 3, 4, 6, 13, 15, and 11** | 3, 4 and 15 fail by construction (the test asserts the thing directly). 6 and 13 guard paths Phase 1 does not change. 11 cannot fail until pull and push differ, which Phase 1 brings; rerun it then. | Low. Run them once at the reference gate. |

### Not recommended

- **Option B of item 1** (one answer for Sync on a tag everywhere) inside this work. It changes what users see for tags, which the reference work promised not to do. If wanted, it is a separate change with its own decision.
- **Fixing the two failing tests inside a read-only repositories commit.** They are unrelated code; keep the change separate so it can be reviewed and reverted alone.

### Resulting order

1. Mutations 1, 2, 5, 7, 8, 9, 10, 12. Add a test for any that passes. Then commit 0B.
2. Cause of the two failing tests. Fix now only if the lock is at fault.
3. 0C gate.
4. Phase 1, with the four required parts above placed in the work packages they belong to.
5. After the reference gate: everything in "Can wait".

## 1. Sync treats a tag checkout differently by entry point

**Today.**

- Workspace Sync (header) includes a repository that is on a tag.
- Sync from a row is disabled for a repository on a tag (`WorkspaceRepositoriesRow.razor`, the sync status badge).
- Sync for a level leaves repositories on a tag out (`WorkspaceRepositories.Sync.cs`, `ShowConfirmSyncLevel`).
- The level action buttons are disabled when every repository of the level is on a tag (`AreAllOnTag` in `WorkspaceRepositories.State.cs`).
- The commits badge of a row is disabled on a tag. It stands for pull and push together.

**Why it is open.** The rule table has one `Sync` operation, allowed on a tag. It cannot say "allowed for the Workspace, not from a row". So these places still read the tag themselves. They are listed as OPEN in `RepositoryPolicyConventionTests` (`WorkspaceRepositories`, `WorkspaceRepositoriesRow`).

**Why it matters for Phase 1.** A reference repository must be handled by the same buttons. If they keep reading the tag, each one needs a second hand-written check for reference, which is what the policy was built to prevent.

**Decision for the owner.** Two options:

- **A. Keep the difference (recommended for 0C).** Add operations `SyncFromRow` and `SyncLevel`, both not allowed on a tag; `Sync` stays allowed. No behavior change. The buttons ask the policy.
- **B. Remove the difference.** Decide one answer for Sync on a tag (allow everywhere or nowhere). This changes what the user sees and needs its own characterization test change. Not part of a behavior-neutral phase.

**Steps (option A).**

1. Add `SyncFromRow` and `SyncLevel` to `RepositoryOperation` and to the rule table. The matrix test fails until both have a row and an expected answer.
2. Row: the sync badge asks `SyncFromRow`. The commits badge gets two inputs, one for pull and one for push, so each half asks its own operation. Until Phase 1 both answers are the same.
3. Level: `ShowConfirmSyncLevel` asks `SyncLevel`. Replace `AreAllOnTag` with "no repository of the level allows any level action" and name the operations it checks.
4. Remove the OPEN text from the two convention entries. If a page type no longer reads the tag for a decision, shorten its reason to presentation only.

**Done when.** The 0A characterization tests pass unchanged, the matrix test covers the two new operations, and no OPEN marker is left in `RepositoryPolicyConventionTests`.

## 2. Skip reporting is partial

**Today.** `Skipped` is filled by `WorkspacePushOperations.PushAsync` and by `DependencyUpdateOrchestrator.RunAsync`. It is logged and hidden from the REST response (`[JsonIgnore]`).

**Missing.**

- The pipelined push lane (`WorkspacePushService.PushLaneLevelAsync`, result built in `UpdateAndPushOrchestrator.BuildPushResult`). `UpdateAndPushResult.Skipped` therefore carries only the update lane's skips for a pipelined run.
- Return to Default (`WorkspaceSyncHandler`, batch and unattended). A repository on a tag is left out without a record.
- Commit (`CommitDependencyUpdatesAsync`, `CommitFilePathsAsync`), project refresh and Restore Packages return counts or tuples, not a result with `Skipped`.

**Steps.**

1. Pipelined push: the lane already loads the skipped ids once per run. Keep the `RepositorySkip` objects instead of the ids and pass them to `BuildPushResult`.
2. Return to Default: add `Skipped` to its result type (init-only, `[JsonIgnore]`), filled from the plan entries the policy left out.
3. Commit, refresh and restore: do not change their return types. Their skips are already part of the update run's list, because the run asks the same rule for the same context. State this in the summary comment of `Skipped`.
4. One test per filled place in `RepositorySkipReportingTests`, plus the existing REST test extended to the Return to Default result.

**Not in this item.** Showing skips to the user and adding a REST field. The plan puts both in Phase 1.

**Done when.** Every Workspace-wide operation that has a result object names the repositories it left out, and the REST test still finds no `skipped` field.

## 3. Convention test does not see tag reads inside query expressions

**Today.** `RepositoryPolicyConventionTests` scans compiled method calls. A lambda that the compiler turns into an expression tree (every Entity Framework `Where`, `Select`, `Count`) refers to the property by a metadata token, not by a call. A new `wr.CheckedOutTag == null` inside a query is not reported.

**Why it matters.** The header counters were the largest group of duplicated tag checks (20 terms in four projections). A fifth projection could bring them back unseen.

**Steps.**

1. Add a source scan in the same test class, using `ConventionSource.AppDirectory`: every `.cs` and `.razor` line under `src/GrayMoon.App` that compares or tests `CheckedOutTag` or `PinnedTag` (`== null`, `!= null`, `== string.Empty`, `== ""`, `IsNullOrEmpty(`, `IsNullOrWhiteSpace(`).
2. Allow-list by file with a reason and an expected number of lines. A higher or lower number fails, so a stale entry cannot stay.
3. Expected entries today: `WorkspaceBranchOperations.cs` (pin bookkeeping, `PinnedTag != null` in an update statement), `WorkspaceGitService.Branches.cs` (pin bookkeeping), `WorkspaceRepositoryStateWriter.cs` (state write), `WorkspaceRepositoriesRow.razor` (display).
4. A sample class in the test project with one query expression proves the scan finds it, as the existing samples do for the call scan.

**Alternative considered.** Extend the compiled scan to `ldtoken` instructions. It would catch expression trees without reading source, but it also reports every projection that only copies the column (mappers, list queries), which is most of them. The source scan can tell a comparison from a copy; the token scan cannot.

**Done when.** Adding `g.Count(wr => wr.CheckedOutTag == null)` to any query in the App project fails the test.

## 4. Two existing tests fail when run together

**Facts.**

- `AiAndGitChangesSweepTests.Cancel_racing_the_end_of_a_run_never_throws` fails at `AiAndGitChangesSweepTests.cs:98`: `runs.IsRunning(key)` is still true after the finishing task and the cancelling task have both completed.
- `BackgroundJobRefusedStartTests.Workspace_job_refused_by_the_lock_reports_not_started` fails at `ServiceLifetimeAndConnectionPlumbingTests.cs:294`: a third job for the same key is refused after the first job was awaited as completed.
- Each passes alone. Together they fail in most runs:

  ```
  dotnet test src/GrayMoon.App.Tests/GrayMoon.App.Tests.csproj --filter "FullyQualifiedName~AiAndGitChangesSweepTests|FullyQualifiedName~BackgroundJobRefusedStartTests"
  ```

- The same command fails the same way on an export of commit `ece83ab7`, so it is older than 0B.
- In a full run each one failed once during 0B; the last full run passed.

**Not known.** The cause. The following are guesses from reading the two tests, not findings:

- The first test starts up to 8000 thread pool tasks that block on a `Barrier`. That can starve the thread pool, so continuations in the other test class run late.
- Both assertions check state right after a task is awaited. If the run or the job releases its entry in a continuation that runs after the awaited task completes, the check can come first. That would be a real ordering gap in `AiTextGenerationRuns` and in `BackgroundJobService` or `WorkspaceOperationRunner`, visible only under load.

**Steps.**

1. Reproduce with the filter above, 20 runs, and record the failure rate for each test.
2. For each test, find where the entry is removed (`AiTextGenerationRuns`, `WorkspaceOperationRunner`) and whether that happens before or after the task the test awaits is completed.
3. If the product releases after completion: decide whether a caller can observe it (a user who starts a job the moment the previous one ends). If yes, fix the order in the product and keep the test as it is. If no, make the test wait for the release, not for the task.
4. If it is only starvation: reduce the blocking in the sweep test (the `Barrier` can be replaced by a start signal that does not hold two pool threads per iteration), and do not put the classes in a shared non-parallel collection unless nothing else works.
5. Record cause and fix in the test log. If the product changed, add the entry to `docs/todo/defects`.

**Done when.** The filtered command passes 20 of 20 runs and the full App run passes 5 of 5.

**Scope.** This is not part of the read-only repositories work. It should be its own change so it can be reviewed and reverted alone.

## 5. No mutation checks for the new tests

**Today.** 603 new test cases pass. Only two were seen to fail for the right reason: the convention test (it listed every reader before the allow-list was filled) and the service tests (they failed where the policy was not registered). The F22 tests were checked this way; the 0B tests were not.

**Steps.** Apply each change below alone, run the named tests, record pass or fail, and restore the file. No change stays in the working tree.

| # | Mutation | Tests that must fail |
|---|---|---|
| 1 | In the rule table, set `AllowedOnTag` to true for `Push` | `RepositoryOperationPolicyMatrixTests`, push characterization tests |
| 2 | Set `AllowedOnTag` to false for `Sync` | `RepositoryOperationPolicyMatrixTests`, sync characterization tests |
| 3 | Remove one rule row | `Every_operation_has_a_rule_row` |
| 4 | `Evaluate` returns `Skipped` for the individual scope | `Individual_scope_never_skips_and_bulk_scope_never_refuses` |
| 5 | Loader: read the shared link for a Feature under `Context` | `A_Feature_reads_its_own_state_and_never_the_Workspace_tag`, restore characterization tests |
| 6 | Loader: ignore the state row under `ContextStateFirst` | `State_first_prefers_the_Workspace_state_row_over_the_link` |
| 7 | Update path: change `LegacySharedLink` to `Context` in `GetUpdatePlanAsync` | update characterization tests for a Feature (F6) |
| 8 | Push plan: change `ContextStateFirst` to `Context` | push plan characterization tests |
| 9 | Expressions: `OnTag` compares with null only | `The_database_agrees_with_the_policy` for the empty string |
| 10 | Remove one `.ExpandRepositoryPolicy()` call in the header query | counter characterization tests (the query must fail to translate) |
| 11 | Header query: use `Push` for the `Incoming` term | Expected to pass today: both are forbidden on a tag. Record it as a known gap that Phase 1 closes when pull and push differ. |
| 12 | Remove `[JsonIgnore]` from `OperationResult.Skipped` | `Skips_are_not_part_of_the_REST_response` |
| 13 | `PushAsync` returns the result without `Skipped` | `A_push_names_the_requested_repository_it_left_out` |
| 14 | Page: `IsRepoAllowed` returns false for an unknown repository | No test today. Record the gap; a page helper test is needed or the helper moves to a static class that can be tested. |
| 15 | Add `link.IsOnTag` to a service not in the allow-list | `The_tag_of_a_checkout_is_read_only_by_the_policy_and_the_listed_types` |

A mutation that does not fail the named tests is a finding: add the missing test before the 0C gate, except 11, which is expected.

**Done when.** The table is in the test log with a result per row, and every unexpected pass has a new test or a written reason.

## What this document does not cover

- The fix for F5 (page-only tag checks) and F6 (update judged by the Workspace checkout). Both are planned phases.
- Any reference repository behavior. Items 1 to 3 only prepare the ground for it.
