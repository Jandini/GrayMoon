# Workspace unhooking and the Add / Import / Restore split - implementation plan

Status: not started. Written 2026-11-21 against branch `unhook-workspace`.
Design, findings and decisions: [workspace-unhook-design](workspace-unhook-design.md). Section numbers below ("design 4.2") point there.

## How to read this

- Eight phases. Each ends with the solution building and all three test projects green, and can be merged on its own.
- Phases 1-5 are Part 1 (unhooking). Phases 6-7 are Part 2 (entry points). Phase 8 is documentation.
- Part 1 changes no dialog except Remove Workspace. Part 2 changes no Worker hook code.
- Every fix ships with a test that fails on the old code. Failure-path tests seed rows the failure could destroy.
- New code follows `GrayMoon/AGENTS.md`: sealed classes, primary constructors, CRLF, ASCII hyphens, Worker DTOs as sealed classes with `[JsonPropertyName]`, `ArgumentList` for Git, factory-created `AppDbContext` in services.

## Phase 0 - confirm before building

Short checks. Each answer goes into the design note if it differs from what is written there.

| # | Check | How |
|---|---|---|
| 0.1 | Decisions D1-D6 (design 6). | Owner answers. D4 decides whether Phase 5 step 5.3 is built. |
| 0.2 | Is the edit branch of `Workspaces.razor` `SaveWorkspaceAsync` (`:771-793`) still reachable? Edit opens `WorkspaceSettingsHost`. | Search for callers that open `WorkspaceModal` with a non-null `editingWorkspaceId`. If dead, it is deleted in Phase 6, not migrated in Phase 4. |
| 0.3 | Can Remove Workspace start while `FeatureRemovalQueue` still removes a Feature of that Workspace? | Read `FeatureRemovalQueue` and `IFeatureRemovalQueue`. If yes, `RemoveWorkspaceAsync` refuses while a removal for the Workspace is queued or running. |
| 0.4 | Which operations hold which lock while they can write hooks: full Sync, single-repository Sync, Create / Repair Feature, enable Workspace repository. | Read their `TryStartStructural` / `TryStartContext` calls. Remove needs every one of them excluded by the structural lock. |
| 0.5 | Does `File.Move` over the hook name work on Windows when an editor has the replaced file open? | A Worker test with a shared-read handle. Decides the wording of the `Failed` message only. |

## Phase 1 - Worker: give sync hooks one owner (no behavior change)

Goal: design 4.1. A pure move, so the next phase adds removal to a small class instead of to `GitService`.

1. Add `src/GrayMoon.Worker/Abstractions/IGitSyncHooks.cs` with `InstallAsync(repoPath, workspaceId, repositoryId, ct)`.
2. Add `src/GrayMoon.Worker/Services/GitSyncHooks.cs`. Move from `GitService.cs:1299-1562`: `GrayMoonHookMarker`, `ReplacedHookSuffix`, `ReferenceTransactionFilter`, `_hooksLocations`, `WriteSyncHooksAsync` (as `InstallAsync`), `WriteSyncHooksCoreAsync`, `InstallSyncHook`, `MoveForeignHookAside`, `IsGrayMoonHook`, `WithoutMarkerLine`, `ResolveGitHooksLocationAsync`, `IsPathInside`, `WriteHookFile`. Add `internal static readonly string[] HookNames` and make `Marker` and `IsGrayMoonHook` internal.
3. The listen port comes from `IOptions<WorkerOptions>` as it does in `GitService`.
4. Delete `WriteSyncHooksAsync` from `IGitService` and `GitService`. `GetHooksConfigPrefix` / `GetHooksConfigArgs` / `EmptyHooksPath` (the `skipHooks` switch) stay in `GitService`; they are not about GrayMoon's hooks.
5. Callers take `IGitSyncHooks`: `SyncRepositoryCommand`, `AttachWorkspaceRepositoryCommand`, `CreateGitWorktreeCommand`.
6. `HookRepositoryPathPolicy`: delete its own `GrayMoonHookMarker` and `SyncHookNames`; use `GitSyncHooks.Marker` and `GitSyncHooks.HookNames`.
7. Register `IGitSyncHooks` as a singleton in `Cli/Handlers/RunCommandHandler.cs`.
8. Tests: move the fakes' `WriteSyncHooksAsync` stubs (`AttachWorkspaceRepositoryCloneFailureTests`, `DeleteBranchCommandTests`, `ReturnToDefaultBranchCommandTests`, `SyncRepositoryCloneFailureTests`) to a fake `IGitSyncHooks`; retarget `GitServiceSyncHooksTests`, `GitHooksLocationCacheTests`, `ReferenceTransactionHookTests`, `GitServiceFewerProcessesTests` (the process budget must not change), `WorkerInputValidationTests`, `SyncRepositoryHookInstallTests`, `WorktreeCreateHookDeferralTests`.

Done when: no test was weakened, the process-count tests show the same numbers, and a grep for the marker text finds one definition.

## Phase 2 - Worker: removal and the command

Goal: design 4.2-4.4.

1. `GitSyncHooks.RemoveAsync(repoPath, ct)` returning `SyncHookRemovalResult` (outcome enum `Removed` / `NothingToRemove` / `NotFound` / `NotARepository` / `Failed`, the four file lists, `ErrorMessage`). Steps exactly as design 4.2: existence, uncached `git rev-parse --git-common-dir --git-path hooks`, candidate directories, per-name delete and restore, verification, cache entry dropped.
2. Replaced-hook restore as design 4.3: parse `<name>.replaced-by-graymoon[-yyyyMMddHHmmss]`, restore the newest, report the rest.
3. A keyed lock per common Git directory in `GitSyncHooks`, held by `InstallAsync` and `RemoveAsync`.
4. The file work runs under a shared `IRepositoryAccess` lease for the repository path.
5. Command, following the `add-worker-command` skill:
   - `Abstractions/Worker/WorkerHubMethods.cs`: `RemoveSyncHooks`.
   - `Jobs/Requests/RemoveSyncHooksRequest.cs` (`: WorkspaceCommandRequest`; `Repositories` list of a sealed item class; `WorkspaceRemoved`).
   - `Jobs/Response/RemoveSyncHooksResponse.cs`.
   - `Commands/RemoveSyncHooksCommand.cs`: resolves each path with `WorkerRepositoryPaths.Resolve` (a rejected name is a `Failed` result for that repository, not an exception for the request), runs repositories in parallel with the bound `GetWorkspaceRepositoriesCommand` uses, calls `IWorkspaceCapabilityProvider.Invalidate` when `WorkspaceRemoved` and nothing failed.
   - `Services/CommandJobFactory.cs`, `Services/CommandDispatcher.cs`, `RunCommandHandler.cs` registrations. Main pool: no entry in `WorkerCommandPoolRouter`.
6. Tests, new `GitSyncHooksRemovalTests` and `RemoveSyncHooksCommandTests` (scenarios from `GitScenarios`, clean-up with `TestDirectory.Delete`): the Worker list in design 8, minus the 404 case (Phase 5).

Done when: the command can be sent by hand to a test Workspace and leaves the five files gone and the user's hook back.

## Phase 3 - App: the seam and Remove Workspace

Goal: design 4.5-4.6. First phase a user can see.

1. `src/GrayMoon.App/Services/Workspaces/WorkspaceSyncHookRemover.cs` + `SyncHookRemovalSummary` (per repository: name, outcome, files, error; `Success`; `Notes`). Reads links and names on a factory context, gets Worker arguments from `IWorkspaceContextPathResolver` for the special Workspace context, sends one command, maps the answer. An unreadable or failed Worker response is a failure of the whole call. Registered scoped in `GrayMoonServiceRegistration.cs`.
2. `IWorkspaceRepositoryOperations.RemoveWorkspaceAsync(int workspaceId, IProgress<OperationProgress>? progress = null, CancellationToken ct = default)` in `GrayMoon.Application`.
3. Implementation in `WorkspaceRepositoryOperations`, steps 1-6 of design 4.6, using the same `TaskCompletionSource` + `TryStartStructural` shape as `EnableWorkspaceRepositoryAsync`. The row delete is `WorkspaceRepository.DeleteAsync`'s logic moved onto a factory context (Feature self-reference cleared first, then the Workspace row; cascades do the rest). The private `DeleteWorkspaceAsync` used by the restore rollback and this delete become one method.
4. `WorkspaceRepository.DeleteAsync` is removed once nothing calls it.
5. `Workspaces.razor`:
   - `DeleteWorkspaceAsync` calls the facade; the Feature acknowledgement logic stays as it is.
   - new dialog text (design 4.6);
   - Worker unavailable: callout and disabled Remove, driven by `WorkerBridge.GetUnavailableReason()` and refreshed on `WorkerConnectionTracker` state changes (the page already subscribes);
   - failure: `deleteErrorMessage` shown in the dialog with the repository list; the dialog stays open;
   - notes: toasts.
   The CSS for the list reuses `restore-missing-list` only if it is moved to a global file first; otherwise a plain `<ul class="small mb-0">`.
6. Tests, new `RemoveWorkspaceOperationTests` (fake `IWorkerBridge` that records `Sent`, as `WorkspaceRepositoryOperationsTests` does): the first four App items of design 8. The failing-repository test seeds links, a Feature, context state rows and Git Changes rows and asserts all are still there.
7. `GrayMoon.Desktop/README.md`: the "Recent GrayMoon changes" bullet for Part 1.

Done when: after removing a Workspace on a real machine, no file in any `.git/hooks` folder of that Workspace carries the marker, and a commit in one of the folders no longer shows up in the Worker log.

## Phase 4 - App: the other unlink paths

Goal: design 4.7. One step per path, each with its test.

1. **U2 membership.**
   - Extract the "which Source links would be removed" rule from `ReplaceRepositoriesCoreAsync` into one method on `WorkspaceRepository` (`GetSourceLinksToRemoveAsync`), used by both the core method and the facade, so there is one copy of the rule.
   - Facade: `IWorkspaceRepositoryOperations.SaveWorkspaceAsync(...)` (name, root, profile, Source ids) wrapping `WorkspaceRepository.UpdateAsync`. Empty removal set: straight through. Otherwise Worker check, structural lock, `RemoveAsync(those links)`, then the update.
   - Move the call sites: `Workspaces.razor:858` (`SaveRepositoriesAsync`), `WorkspaceSettingsHost.razor:314`, `WorkspaceRepositories.RepositoriesModal.cs:76`, and `Workspaces.razor:774` unless check 0.2 says it is dead.
   - The modals show the facade's error where they show `ex.Message` today.
2. **U3 disable.** `DisableWorkspaceRepositoryAsync`: Worker check, structural lock, `RemoveAsync(the Workspace-role link)`, then the existing transaction.
3. **U4 Source -> Workspace repository.** In `LinkAndAttachAsync`, after a successful attach and before `ReplaceMovedSourceLinkAsync`: remove hooks from the old Source folder with `workspaceRepositoryName: null`. `WorkspaceSyncHookRemover` gets an overload that takes explicit names for this. A failure becomes a warning in `LinkAttachResult`, appended to the notes `EnableCoreAsync` already returns.
4. **U5 restore rollback.** `RollbackRestoreAsync`: `RemoveAsync(all)` first, wrapped like the other two rollback steps; failures go into `residue`.
5. **U6.** `RemoveLinkAsync` and the non-Source branch of `UndoLinkAsync` call `RemoveAsync` for the link before the delete, with `CancellationToken.None` (they run on the cancel path).
6. **Convention test** in `GrayMoon.App.Tests/Conventions`: source files that contain `db.Workspaces` / `WorkspaceRepositories` followed by `Remove(` or `ExecuteDeleteAsync(` must be in an allow-list (`WorkspaceRepository.cs`, `WorkspaceRepositoryOperations.cs`), with the reason next to each entry.
7. `GrayMoon/AGENTS.md`, "Destructive and merge operations": add the rule "hooks are removed through `WorkspaceSyncHookRemover` before a Workspace link row is deleted".
8. Tests: design 8, App items 5-7.

Done when: the six rows of design 1.3 each have a test that fails on the old code.

## Phase 5 - Worker: a removed Workspace stops costing work

Goal: design 4.8 and 4.9.

1. `IWorkspaceCapabilityProvider`: a result that separates "capabilities", "could not ask", and "Workspace does not exist". `FetchAsync` maps 404 to the third. Only capabilities are cached.
2. `HookSyncDispatcher`: resolve capabilities once, before dispatching to the five hook commands (they each call the provider today; they take the resolved value instead). "Does not exist" drops the job with one Information line naming ids and path.
3. *(Only if D4 is accepted.)* On that drop, `GitSyncHooks.RemoveAsync(repoPath, onlyWorkspaceId: deadId, ct)`: the same algorithm, but a GrayMoon hook is deleted only when its text contains that `workspaceId`. The match is on the exact payload fragment the script holds, the one `HookRepositoryPathPolicy.CarriesSyncHook` already searches for. Logged at Information with the files removed.
4. Tests: `HookSyncCapabilitiesTests` gains the 404 case (no probe, no `SyncCommand`) and the network-error case (still enriches); `FakeAppApi` answers 404 on request; reaping removes only hooks of the dead id and restores the replaced file.
5. Optional, after the rest: the per-repository check at the end of design 4.9. Build it only if dropped snapshots from pre-change U2 orphans show up in real logs.

Done when: a commit in a folder of a removed Workspace produces one log line and no Git or GitVersion process.

## Phase 6 - UI: split button and an Add that only adds

Goal: design 5.2-5.3.

1. **Split button CSS.** Search every stylesheet for `.dropdown-toggle-split` rules (`WorkspaceActions.razor.css:35-46`, the merge dialog's own). Move the shared ones once to a global file under `wwwroot/css/` (prefix `:root `, check order and ties, per the CSS rules) and delete the scoped copies in the same change. `ScopedCssBudgetTests` must still pass.
2. **Header** in `Workspaces.razor`: replace the two buttons (`:80-85`) with the `btn-group`. State `_addMenuOpen`; closes on Escape, on outside click (the pattern `WorkspaceActions.razor` uses), and after a choice. `text-nowrap` on the button and the items; `aria-haspopup`, `aria-expanded`, `role="menu"`.
3. **Add Workspace**:
   - `LoadDirectoryInfoAsync` calls `WorkspaceService.GetDirectoryStateAsync` only. A non-empty folder sets the error text of design 5.3 and disables Save. A Worker error is shown, not swallowed.
   - Delete from the page: `existingRepositoryCount`, `folderIsWorkspaceRepository`, `folderWorkspaceRepositoryImported`, `detectedWorkspaceRepositoryId`, `_autoSelectedWorkspaceRepositoryId`, `adoptedExistingWorkspaceRepository`, `existingRepositoryInfos`, `showImportModal`, `importMatchingCount`, `importRepositoryIds`, `ApplyDetectedWorkspaceRepositoryAsync`, `ResetExistingFolderState`, `TrySuggestImportsAsync`, `ConfirmImportAsync`, `CancelImportAsync`, and the `ReferenceImportGuard` injection.
   - `EditorNeedsSetupBranch` loses its third argument.
   - `SaveWorkspaceAsync` re-checks the folder state right before `AddAsync`, so a folder that filled meanwhile is refused.
4. `WorkspaceModal.razor`: remove the four parameters and the warning callout; `NeedsSetupBranch` keeps its optional third parameter for the settings host if that still passes it, otherwise it goes.
5. Delete `WorkspaceImportModal.razor`. `ExistingWorkspaceFolder.Callout` and its tests go; `MatchRepositoryId` stays for Phase 7.
6. Until Phase 7 ships, the **Import from disk** menu item is not rendered. Phases 6 and 7 are merged together or in this order, never with a dead menu item.
7. Tests: page-level tests for the folder rule (existing-folder refused, empty folder accepted, Worker error shown); update tests that used the removed parameters.

Done when: typing the name of an existing, non-empty folder into Add Workspace cannot create a Workspace.

## Phase 7 - Import from disk

Goal: design 5.4.

1. **Worker, read-only commands** (read pool: add both names to `ReadOnlyCommands` in `WorkerCommandPoolRouter`):
   - `ListWorkspaceRootFolders`: directory names directly under the workspace root. Request carries `workspaceRoot`.
   - `InspectWorkspaceFolder`: for `<root>/<name>`: `exists`, root repository (`isGitRepository`, `originUrl`), children with `.git` (`name`, `originUrl`, `hasGrayMoonHooks`), and `definitionContent` (`graymoon.json` text, size-capped like the hook body and the manifest reader). Built on `GetWorkspaceRepositoriesCommand`'s reads and `GitSyncHooks.IsGrayMoonHook`.
   Both validate the name with `WorkerRepositoryPaths`, and neither writes.
2. **Contract**: `src/GrayMoon.Application/Workspaces/IWorkspaceImportOperations.cs` with `ImportableFolder`, `ImportWorkspacePreflight` (shape, profile from the definition or null, matched / unmatched repositories, repositories on disk outside the definition, missing connectors, read only summary, stale-hook count, `Error`), `ImportWorkspaceOptions` (profile for shapes B and C), `ImportWorkspaceResult` (same fields as `RestoreWorkspaceResult` minus `CleanupResidue`, plus `RollbackResidue`).
3. **Implementation**: `src/GrayMoon.App/Services/Application/WorkspaceImportOperations.cs`.
   - `ListImportableFoldersAsync`: Worker list minus the folder names of existing Workspaces.
   - `PreflightImportAsync`: inspect, match origins with `ExistingWorkspaceFolder.MatchRepositoryId`, evaluate a definition with `RestoreDefinitionEvaluator`.
   - `ImportFromDiskAsync`: design 5.4 steps 1-7. The shared pieces of `WorkspaceRepositoryOperations` (`LinkAndAttachAsync`, `ApplyProfileAsync`, `ApplyReferenceDefaultAsync`, `IsDefinitionRewriteRequiredAsync`, the Source link loop) move to an internal `WorkspaceDefinitionApplier` that both Restore and Import call. Restore's tests must pass unchanged after that move; it is done as its own commit-sized step before any import code.
   - Rollback: `WorkspaceSyncHookRemover.RemoveAsync`, then the row delete from Phase 3. No `DiscardWorkspaceRoot`.
4. **Dialog**: `Components/Modals/ImportWorkspaceModal.razor` + `.razor.cs` + `.razor.css`, flow in a static `ImportWorkspaceFlow` (the way `RestoreWorkspaceFlow` sits next to its modal). Runs as a background job through `IBackgroundJobService` with the overlay; the context and ids are captured before the job is queued; page state is applied inside `InvokeAsync`. The Sync after the import is requested the way `RestoreWorkspaceFlow.RunAsync` does it. Styles shared with the Restore dialog (`restore-preview`, `restore-missing-list`, `restore-validation-error`) move to one global file first; they are not copied.
5. `Workspaces.razor`: render the menu item, host the modal, refresh the list on finish.
6. Tests: `ImportWorkspaceFlowTests`; `WorkspaceImportOperationsTests` for shapes A, B, C, name taken, invalid definition, a failure after the row was created (rows gone, hooks removed, folder content untouched, seeded unrelated Workspace untouched); Worker tests for the two commands.
7. `GrayMoon.Desktop/README.md`: the bullet for Part 2.

Done when: a folder left behind by Remove Workspace can be brought back with Import from disk, ends in sync, and its hooks carry the new Workspace id.

## Phase 8 - documentation

The list in design 10, in the same change as the last code phase that makes each statement true:

- Phase 3-5 changes update `01` section 19, `02`, `04` section 7 and `05` (Remove Workspace, removing a repository).
- Phase 6-7 changes update `01` (Workspace life cycle), `05` (header menu, Add, Import), the wiki pages and their screenshots.
- `GrayMoon/CLAUDE.md`: the command name list and the "Worker to App sync" paragraph.
- When the last phase ships: delete both notes and their row in `docs/todo/README.md`; move what is still open (design 9) into `ideas/` or `defects/` notes.

## Order and size

```
Phase 0 --> 1 --> 2 --> 3 --> 4 --> 5        Part 1
                        |
                        +--> 6 --> 7         Part 2 (needs the seam from Phase 3)
                                    \
                                     8       with each phase
```

| Phase | Mostly touches | Rough size |
|---|---|---|
| 1 | Worker `GitService`, new `GitSyncHooks`, test fakes | medium, mechanical |
| 2 | Worker new command + tests | medium |
| 3 | App facade, one page, tests | medium |
| 4 | App: four call sites, three operations, convention test | medium |
| 5 | Worker capability provider, dispatcher | small |
| 6 | One page, one modal, CSS move | medium, mostly deletion |
| 7 | Two Worker commands, new facade, new dialog, shared applier | large |
| 8 | Documents | small |

## Risks

| Risk | Guard |
|---|---|
| Phase 1 changes behavior while moving code. | No test edits beyond retargeting; process-count tests unchanged. |
| A user's hook is deleted. | Removal deletes only files that pass `IsGrayMoonHook`; a test with a foreign hook next to GrayMoon's in every phase that touches the algorithm. |
| Remove Workspace becomes impossible on a broken machine. | Missing folders and non-repositories are successes; only a real file error blocks, and the message names the file. |
| A new unlink path is added later without the seam. | The convention test from Phase 4 and the `AGENTS.md` rule. |
| Reaping (D4) removes hooks of a live Workspace. | Gated on an authenticated 404 for that exact Workspace id, and id-matched in the file. Network errors never trigger it. |
| The Restore refactor in Phase 7 breaks Restore. | The extraction is its own step with Restore's existing tests as the gate. |
| CSS moves change how the existing split buttons look. | Follow the scoped-to-global checklist; compare the Actions page and the merge dialog before and after. |
