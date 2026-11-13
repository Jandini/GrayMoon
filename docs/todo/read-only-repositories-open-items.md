# Read only repositories - open items

Written 2026-11-12, when the feature was wrapped up for the merge to `main`.

## What is left

The feature is built and merged to `main`. This is everything that is still open, and nothing else is.

| # | What | Who | Needed before the feature counts as tested |
|---|---|---|---|
| 1 | Manual smoke M1-M13 and five things to look at | Owner | **Yes** |
| 2 | Show the warning of a manual switch on the Repositories page | Developer | No, a user can notice it |
| 3 | Skip reporting in the pipelined push lane and Return to Default | Developer | No, a user can notice it |
| 4 | Convention test: see tag reads inside query expressions | Developer | No, tests only |
| 5 | Mutation checks that were never run | Developer | No, tests only |
| 6 | Render tests for the Switch Branch dialog and the settings section | Developer | No, tests only |

Not open: the code, the automated tests (build 0 warnings, Common 446, Worker 1034 with 1 known skip, App 4841, both `Long` runs) and the architecture documentation. How the feature works is in `docs/architecture/` (01, 02 section 7B, 03, 04, 05, 06, 07), `AGENTS.md` and `CLAUDE.md`.

The working documents (idea note, implementation plan, inventory, test log, 0B follow-ups) were removed on 2026-11-13. They described work that is already in those architecture documents.

## 1. Manual smoke M1-M13 (owner, after the release)

Not run. Each line needs a running App and Worker and a person. Use a Workspace with at least one read only repository and one writable one.

- [ ] M1 Push / Push Synchronized / Push Updated with one read only repository holding outgoing commits. It is left out and named.
- [ ] M2 Update Dependencies and Update and Push (two-lane and sequential fallback). A read only repository is scanned but never changed.
- [ ] M3 Prepare Workspace from the page and through REST.
- [ ] M4 Create Branch and Switch Branch for the Workspace.
- [ ] M5 Sync Commits on a level and on one row.
- [ ] M6 Return to Default (dialog and unattended) and bulk Merge PRs.
- [ ] M7 Git Changes: stage, unstage, Commit All, Undo. Undo across all repositories leaves a read only repository out; Undo on the repository itself works.
- [ ] M8 Create, Repair, Remove and Roll back a Feature. A read only repository is a detached snapshot in the Feature.
- [ ] M9 Turn read only on with: clean, dirty, unpushed, no upstream, Feature worktree with unpushed commits.
- [ ] M10 Pull a read only repository; check `graymoon.json` changed in the Workspace repository.
- [ ] M11 Restore the committed definition on a second machine or folder; flags and revisions match; first Sync treats no repository as writable by mistake.
- [ ] M12 Toggle while an Update and Push runs (refused), and start a push while a batch toggle runs (refused).
- [ ] M13 An older build opens a version-2 definition and reports "schema 2 is newer".

Look at these too, because no test sees them:

- [ ] Git Changes: the repository row (name, padlock, branch, count). The label moved into `GitChangesRepositoryLabel.razor` on the last day.
- [ ] Hover on a repository name in Workspace repositories, Actions, Repositories and Packages: the underline is not blue.
- [ ] Switch Branch dialog: padlock in the header, Lock / Unlock button.
- [ ] Workspace settings: the "Read only repositories" section (make all read only, make all writable, the default switch, the archived suggestion).
- [ ] An archived repository that was unarchived on GitHub becomes pushable again without a repository fetch. The check is tested only against scripted HTTP answers.

## 2. The manual switch warning is not shown

When a read only repository is switched to another branch by hand, the result carries a warning (`Warning` on the checkout result). The Repositories page does not show it. The switch itself works and the new branch and commit are recorded.

Done when: the page shows the warning as a toast, and a test covers the result that carries it.

## 3. Skip reporting is partial

The list of repositories an operation left out (`Skipped`) is filled by `WorkspacePushOperations.PushAsync` and `DependencyUpdateOrchestrator.RunAsync`. Missing:

- The pipelined push lane (`WorkspacePushService.PushLaneLevelAsync`, result built in `UpdateAndPushOrchestrator.BuildPushResult`). For a pipelined run, `UpdateAndPushResult.Skipped` carries only the skips of the update lane.
- Return to Default (`WorkspaceSyncHandler`, batch and unattended). A repository that is left out has no record.

Steps:

1. Pipelined push: the lane already loads the skipped ids once per run. Keep the `RepositorySkip` objects and pass them to `BuildPushResult`.
2. Return to Default: add `Skipped` to its result type (init-only), filled from the plan entries the policy left out.
3. One test per filled place in `RepositorySkipReportingTests`.

## 4. The convention test does not see tag reads inside query expressions

`RepositoryPolicyConventionTests` scans compiled method calls. A lambda that becomes an expression tree (every Entity Framework `Where`, `Select`, `Count`) refers to the property by a metadata token, so a new `wr.CheckedOutTag == null` or `wr.IsReference` inside a query is not reported.

Steps:

1. Add a source scan in the same test class: every `.cs` and `.razor` line under `src/GrayMoon.App` that compares or tests `CheckedOutTag`, `PinnedTag`, `IsReference` or `IsReferenceSnapshot`.
2. Allow-list by file with a reason and an expected number of lines, so a stale entry fails.
3. A sample class in the test project with one query expression proves the scan finds it.

## 5. Mutation checks that were never run

A mutation check changes one line of the product and expects named tests to fail. The checks for the tag rules were run during the work (rule for Push and Sync on a tag, the row a Feature reads, the update path and the push plan, the query form, the header query, the REST response); two gaps were found and closed with tests. The rows below were never run. For each: apply the change alone, run the named tests, record pass or fail, restore the file.

| # | Mutation | Tests that must fail |
|---|---|---|
| 1 | Remove one rule row | `Every_operation_has_a_rule_row` |
| 2 | `Evaluate` returns `Skipped` for the individual scope | `Individual_scope_never_skips_and_bulk_scope_never_refuses` |
| 3 | Loader: ignore the state row under `ContextStateFirst` | `State_first_prefers_the_Workspace_state_row_over_the_link` |
| 4 | `PushAsync` returns the result without `Skipped` | `A_push_names_the_requested_repository_it_left_out` |
| 5 | Add `link.IsOnTag` to a service not in the allow-list | `The_tag_of_a_checkout_is_read_only_by_the_policy_and_the_listed_types` |
| 6 | Add `[JsonIgnore]` to `OperationResult.Skipped` | `Skips_are_part_of_the_REST_response_with_the_reason_by_name` |
| 7 | Rule table: `Reference` allowed for `Push` | `A_reference_repository_answers_as_the_design_says`, `A_push_leaves_the_read_only_repository_out_and_pushes_the_writable_one` |
| 8 | `Evaluate`: check read only before the Workspace repository | the matrix test that the Workspace repository is always allowed |
| 9 | Rule table: `ReferenceSnapshot` allowed for `Pull` | `RepositoryOperationPolicyMatrixTests`, `A_Feature_refuses_every_checkout_in_a_read_only_snapshot` |
| 10 | Activation rule: an ahead count above zero passes | `Unpushed_commits_block_and_are_counted` |
| 11 | Page: `IsRepoAllowed` returns false for an unknown repository | No test today. The helper needs a test, or moves to a static class that can be tested. |

A mutation that does not fail its tests is a finding: add the missing test.

## 6. Components no test renders

`ReferenceMarkupRenderTests` renders the padlock, the Workspace repositories row and the Git Changes repository label. Not rendered: `SwitchBranchModal` and `WorkspaceReferenceSection`. Both contain the loading overlay, which injects six services and the script runtime.

Done when: both render in a test with stubbed services, and the tests assert the padlock in the dialog header, the Lock / Unlock label for each state, and the three state texts of the section.
