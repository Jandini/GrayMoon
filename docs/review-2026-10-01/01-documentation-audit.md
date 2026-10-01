# 01 - Documentation Audit (2026-10-01)

**Scope:** every Markdown document in `GrayMoon` and `GrayMoon.Desktop` (root files, `docs/architecture`, `docs/worktree`, Desktop `docs/`, `.claude/skills`, `.cursor`, `.github`), checked against the code.
**GrayMoon:** branch `opus-review`, HEAD `a06fe3344b4bfe10f282fd667b39fdcbd56f41fc` ("Do not touch repos on tags"), working tree clean before this audit.
**GrayMoon.Desktop:** branch `opus-review`, HEAD `8c4fb906bbc3e5c5f7f5db5b3899a651180224ac` ("Update readme"), working tree clean before this audit.
**Rules followed:** no source code edited; `docs/worktree/*.md` checked for consistency only, not rewritten; every edited claim was verified against the code cited below. Nothing was committed.

---

## 1. Summary

The current-state docs (`docs/architecture`, `CLAUDE.md`, Desktop `docs/`) were written for the code as it stood just before worktree-backed Features shipped (#368 / #372 / #373 / #374 / #375 / #376). They said outright that Features did not exist (`05` section 31, `architecture/README.md`, Desktop `docs/README.md`). They also described a single-level workspace lock, legacy-only hub events, and a single-checkout data model, none of which is true any more.

The biggest fixes:

1. Feature concepts (Feature, context, `WorkspaceFeatureContextId`, lifecycle, storage paths, Create / Remove flows) were added to the product model, data model, runtime, capability reference, and extension guide.
2. The hierarchical `WorkspaceOperationRunner` lock, the context-aware hub events, hook context attribution, and the `ListGitWorktrees` read-pool routing are now documented correctly.
3. A wrong Desktop README changelog entry was corrected. It claimed Remove Feature "returns Workspace repositories to default and pulls latest"; the code only syncs status and never checks out or pulls.
4. Broken or wrong references were fixed: the job-key example route, a missing `DELETE-MANIFEST.md` link, a missing `docs/CI-CD-Workflows.md` link, `../CLAUDE.md`, and a test-filter example naming a class that does not exist.

---

## 2. Changes made, file by file

### GrayMoon

| File | Change |
| --- | --- |
| `docs/architecture/README.md` | State line now says "with worktree-backed Features" and records the new review SHA. Replaced the "no Features" paragraph with the Workspace + Features model, and replaced the single-checkout diagram with a context diagram. Added `DesktopNotificationHub` to the hub box. Removed the dead `DELETE-MANIFEST.md` link and replaced it with a note that `docs/worktree/` is historical. Added Features to the core workflows. |
| `docs/architecture/01-product-model-and-user-workflows.md` | Added Feature and Context terminology with the lifecycle diagram (section 2). Fixed global navigation: added Home, the global Repositories page and routes, and the Changes nav dot (section 3). Clarified that Prepare Workspace and Return to Default are Workspace-only. Added section 22, "Feature workflow", with its constraints. |
| `docs/architecture/02-system-architecture.md` | Added the `GrayMoon.Application/Features` contracts and a "Feature services" subsection listing `Services/Features/*`. Documented that Feature paths are composed with `\`. Updated hub events, described the lock as hierarchical, and added Desktop launch / install tools and `/hubs/desktop`. REST section now notes that all endpoints act on the special Workspace context and that there is no auth. |
| `docs/architecture/03-data-model-and-persistence.md` | Migrations now point at `Migrations.cs` / `Migrations.Features.cs`. Added: the dual-write rule, the `[NotMapped]` overlay fields, `ManagedFeatureStorageRoot`, the new `WorkspaceProjects` unique key, `WorkspaceFileLineStatuses` context column and key, per-context PR / Actions / Git Changes tables, settings keys, and section 22, "Feature contexts and worktree persistence", covering every Feature table, index and enum. |
| `docs/architecture/04-runtime-communication-and-concurrency.md` | Pool routing now includes `ListGitWorktrees` and the defaults. Section 3 rewritten for the structural / context lock and job-key selection. Added hook "Context attribution". Added context-aware Git Changes broadcasts. Added the full `WorkspaceSyncHub` event list, including the caveat that `WorkspaceSynced` is still sent for Feature contexts by many paths. |
| `docs/architecture/05-user-capability-reference.md` | Worker install from Desktop. Branch / Feature menu, "Create PR" label (was "Create PRs"), and dynamic primary-button labels. Feature Create PR base. Desktop "Open in..." flyout. Section 31 rewritten: the "no Features" statement was removed and the real current Feature limitations listed. New section 32, "Features (worktree contexts)". |
| `docs/architecture/06-developer-extension-guide.md` | Added the App-side filesystem probe rule, lock-scope wording, the full Worker-command touch list, migration checklist items for `Migrations.Features.cs` and the context backfill, and new section 20A, "Feature context scoping", which mirrors `.cursor/rules/feature-context-scoping.mdc`. Section 23 now says `docs/worktree/` is superseded. |
| `CLAUDE.md` | Replaced all 15 em dashes (the file broke its own rule). Read pool now includes `ListGitWorktrees`. `AgentHubMethods` list corrected: hub methods are separated from command-name constants and the four Feature commands added. Hook flow now covers `repositoryPath` attribution and the Context* broadcasts. Corrected the `System.CommandLine` statement. Added a "Worktree Features" section, the hierarchical lock description, context Git Changes tables and events, and the `Migrations.Features.cs` / `AppDbContext.Features.cs` pointers. `BackgroundJobService` key example fixed: `/workspace/123/repositories` became `/workspaces/123` and `/workspaces/123/ctx/{id}`. |
| `README.md` | "Worktrees as Features" moved from "Coming soon" to "Why teams pick GrayMoon". Removed "Native Markdown changes review" from Coming soon, because it has shipped and the same README already describes it. Added a contributor pointer to `docs/architecture`, `CLAUDE.md` and `AGENTS.md`. |
| `.claude/skills/add-agent-command/SKILL.md` | Corrected the `System.CommandLine` claim. Added the missing `CommandJobFactory.DeserializeRequest` step, pool routing, DTO location and attributes, App-side `IAgentBridge.SendCommandAsync` / `AgentCommandResponse.Data`, and a test step. Fixed file paths (`Services/CommandDispatcher.cs`, `Services/CommandJobFactory.cs`). |
| `.cursor/README.md` | Listed all ten rules in `.cursor/rules/`. It used to mention only two. |
| `.github/copilot-instructions.md` | Fixed the "compmlete" typo. Added the hyphen rule and pointers to `CLAUDE.md`, `AGENTS.md` and `docs/architecture`. |
| `src/GrayMoon.App/Data/Migrations/README.md` | Rewrote the stale "add EF Core migrations here" placeholder. It now points at the guarded SQL methods actually used. |

### GrayMoon.Desktop

| File | Change |
| --- | --- |
| `README.md` | Corrected the "Remove Feature refreshes Workspace status" bullet: it now describes analysis, consent checkboxes, and a status-only sync, and says remote branches are kept. Expanded "Feature native launch" with Claude CLI, Visual Studio, per-repository sub-menus, and detection. Removed the superseded first "Private-repo remote branch delete auth" bullet (the second one is correct; `DeleteBranchCommand.cs` line 34 passes `BearerToken`). Moved the "App responsibilities (desktop mode)" bullets, which sat after an unrelated diagram, back under their empty heading. Updated the bridge diagram label. |
| `CLAUDE.md` | Fixed broken references: `../CLAUDE.md` now points to `../GrayMoon/CLAUDE.md`, and `docs/CI-CD-Workflows.md` now points to `docs/03-...`. Added the `docs/` row. The test-filter example used a non-existent `AppProcessManagerTests`; it now uses `NavigationPolicyServiceTests`. Added `ToolAvailabilityService`, `HostPrerequisitePlanner` / `Installer`, `WorkerInstaller`, `WebViewMessageSourceValidator`, `DesktopUiPreferences`, and `ReleaseChannelMapping` to the key services. |
| `docs/README.md` | State line and baseline updated. Native capabilities now include tool launch, tool detection, and prerequisite / Worker install. "Relationship to future worktree Features" rewritten as shipped behavior. |
| `docs/01-product-and-host-behavior.md` | Section 8 bridge command list now includes `OpenInCursor`, `OpenInVsCode`, `OpenInVisualStudio`, `OpenInClaudeCli`, `OpenInTerminal` and `InstallWorker`, with launch commands, tool detection, and which commands are origin-validated. |
| `docs/02-runtime-architecture-and-integration.md` | Section 1 services list completed. Section 14 "Future tool-launch actions" now describes the shipped handlers. |
| `docs/03-build-bundle-updates-and-distribution.md` | Replaced one em dash. |
| `docs/04-developer-extension-guide.md` | Section 14 notes which test classes actually exist. Section 15 "public installer mirror" now matches 03's "GrayMoon.Release only" rule. Section 16 is now current actions plus a new-tool checklist. Section 17 path changed from `docs/architecture` to `docs/`. |

All edited files keep CRLF line endings and contain no em or en dashes.

### Evidence used for the main claims

| Claim | Evidence |
| --- | --- |
| Hierarchical lock | `src/GrayMoon.App/Services/Jobs/WorkspaceOperationRunner.cs` lines 6-9, 53-60 (`TryStart` is structural), 81-131 (`TryStartContext`), 142-154 (structural refused while any context mutation runs); `BackgroundJobService.cs` lines 113-136; `WorkspaceJobKeys.cs` lines 28-43 |
| Read pool | `src/GrayMoon.Agent/Hosted/SignalRConnectionHostedService.cs` line 40; `AgentOptions.cs` lines 11, 18, 26 |
| Hub events | `Services/Agent/SyncCommandHandler.cs` lines 95-109; `Services/GitChanges/GitChangesSnapshotPushHandler.cs` lines 43-56, 74-185; `Services/Workspaces/WorkspaceStateRecomputeScope.cs` lines 31-39 |
| Feature tables / indexes | `src/GrayMoon.App/Data/AppDbContext.Features.cs` lines 10-208; `AppDbContext.cs` lines 226-231, 377-378; `Models/Features/*.cs` |
| Migration entry points | `src/GrayMoon.App/Migrations.cs` lines 16-25; `Migrations.Features.cs` lines 14-35 |
| Settings keys | `src/GrayMoon.App/Repositories/AppSettingRepository.cs` lines 10-22; `WorkspaceService.cs` lines 185-244 |
| Branch / Feature menu | `Components/Shared/WorkspaceRepositoriesHeader.razor` lines 60-133, 459-462, 588-590 |
| Remove Feature modal | `Components/Features/RemoveFeatureModal.razor` lines 39-64 (two checkboxes; either one enables Remove) |
| Remove Feature execution | `Services/Features/WorkspaceFeatureOperations.cs` lines 391-411, 453, 592, 600-624 |
| Create Feature paths | `WorkspaceFeatureOperations.cs` lines 137, 178-212, 267-277, 1088-1089; `WorkspaceContextPathResolver.cs` line 32 |
| Create PR base in a Feature | `Components/Pages/WorkspaceRepositories.PullRequests.cs` lines 35-63 |
| REST uses the special Workspace only | `Api/Endpoints/WorkspaceOperationsEndpoints.cs` lines 11-15; `BranchEndpoints.cs` / `WorkspaceEndpoints.cs` (every handler calls `GetOrCreateSpecialWorkspaceContextIdAsync`) |
| Navigation | `Components/Layout/NavMenu.razor` lines 29-133 |
| Desktop bridge | `GrayMoon.Desktop/src/GrayMoon.Desktop/Services/WebMessageBridgeService.cs` lines 88-139, 446-545; `Models/WebMessage.cs` lines 28-38; `MainWindow.xaml.cs` lines 229-254, 319, 344-345; `WorkerInstaller.cs` lines 12-21, 75 |

---

## 3. Remaining gaps and why they were not filled

| Gap | Why not filled here |
| --- | --- |
| No user guide inside the repo. The README sends users to the GitHub wiki, which is outside both repositories. | The wiki is the established user-facing channel and could not be checked from this workspace. Whether the wiki has a Features page needs checking (see recommendation R4). |
| No troubleshooting / FAQ (for example "Worker offline", "Feature stuck in NeedsRepair", "hooks not firing", "Docker cannot see my paths"). | Writing one requires product decisions about supported recovery steps. Some answers depend on code gaps listed in section 5 (for example NeedsRepair has no "repair" action, only remove). |
| No onboarding doc for contributors beyond `CLAUDE.md` commands. | `CLAUDE.md` plus `docs/architecture/README.md` cover most of it. A separate file would add a fourth copy of the conventions (see section 4.1). |
| No GrayMoon changelog. The Desktop README's "Recent GrayMoon changes" list (now 72 bullets) is the de facto changelog, as required by `.cursor/rules/desktop-readme-on-feature-change.mdc`. | Moving it is a structural decision for the maintainer (see R3). |
| The `docs/worktree/` files were not edited. | That was out of scope by instruction. Consistency findings are in section 4.2. |
| `AGENTS.md` was not changed. | It was checked and is accurate. The referenced test `WorkspaceRepositoryLinkListQueryServiceTests.Feature_context_sort_keyset_and_level_grouping_use_context_state_not_shared_link` exists. |
| A per-page description of how every Workspace page behaves in a Feature context (Projects / Packages / Files / Dependencies / Actions edge cases). | The Feature review agent is covering the Feature feature in depth. The docs now state the general rule (pages read per-context state; generated packages stay global) without per-page claims I could not fully verify. |
| Desktop README "Recent GrayMoon changes" entries other than the three fixed were not re-verified. | There are 72 bullets of historical behavior. Only Feature-related entries were checked against the code. |

---

## 4. Structural problems

### 4.1 Duplication of agent / contributor instructions

The same conventions live in five places with different wording and some conflicts:

| Topic | `CLAUDE.md` | `AGENTS.md` | `.cursor/rules/*.mdc` | `.cursor/README.md` | `.github/copilot-instructions.md` |
| --- | --- | --- | --- | --- | --- |
| CRLF | yes | - | `windows-eol-and-shell` | yes | yes |
| Hyphens only | yes | - | `hyphens-not-em-en-dashes` | - | yes (added now) |
| End-of-task summary | "single sentence" (line 17) | "commit message" block (section "Never commit or push") | `summary-of-changes` | "one or two sentences" | "single sentence" |
| DbContext rule | short (section "DbContext lifetime") | long | `dbcontext-scoped-lifetime` | - | - |
| Feature-context scoping | pointer (added now) | long | `feature-context-scoping` (near-identical copy) | - | - |
| Never commit / push | - | yes | `no-commit-or-push` | - | - |

Problems:

- The summary rule conflicts: one sentence versus one or two sentences, plus AGENTS.md requires a commit message block.
- `CLAUDE.md` violated its own hyphen rule 15 times before this audit. Nothing enforces these rules (no lint), so drift is guaranteed.
- `AGENTS.md` and `.cursor/rules/feature-context-scoping.mdc` are near-verbatim copies; any fix must be made twice.

### 4.2 `docs/worktree/` is outdated design history

| File | Status vs code | Consistency findings |
| --- | --- | --- |
| `GrayMoon-Features-Final-Pre-Design-Decision-Summary-v2.md` | Superseded design input | Storage layout `C:\Workspace\.graymoon\AVR\features\...` (lines 160-257) implies a root next to the Workspace root. The code defaults to `{userprofile}\.graymoon` from the Worker, overridable in Settings (`WorkspaceService.cs` lines 217-244). |
| `GrayMoon-Workspace-Current-Features-Baseline-Appendix.md` | Pre-Features regression baseline (`cd9a17c`) | Its companion reference `GrayMoon-Features-Pre-Design-Decision-Summary-v2.md` does not exist; the file is `GrayMoon-Features-Final-Pre-Design-Decision-Summary-v2.md`. |
| `GrayMoon-Worktree-Features-Detailed-Implementation-Design-v3.md` | Superseded design | It names a required companion `GrayMoon-Features-Final-Pre-Design-Decision-Summary.md` (no `-v2`), which does not exist. Section 6.13 storage root is `<parent of Workspace root>\.graymoon\...` (lines 621-637); the code differs as above. The remove flow "optionally delete remote branch" (lines 1872-1875) exists only as `RemoveFeatureOptions.DeleteRemoteBranches`, which the UI never sets. "Remove empty Feature root directories" (line 1883) has no counterpart in App or Worker code. |
| `GrayMoon-Worktree-Features-Multi-Agent-Implementation-Prompt-v3.md` | Implementation prompt; Features shipped | It should be retired per `docs/architecture/06` section 23. |
| `...-Code-Review-Findings-2026-09-21.md`, `...-Dependency-And-Wiring-Gaps-2026-09-21.md`, `...-Post-Implementation-UX-Gaps-Analysis-2026-09-21.md` | Point-in-time analyses on branch `worktree` (HEAD `2167aeb` / `b3ebfd6`) | They contain 31, 47 and 42 em dashes respectively. They do not say which findings were fixed in later PRs (#374 to #376 and a06fe33). |
| `TODO-Changes-Nav-Notification-Dot.md` | Says "implemented ... manual verification pending" | The feature shipped (#367) and is now documented in `architecture/01` section 3 and `04` section 9. |

None of these files carries a "superseded" banner, so a reader cannot tell them apart from current-state docs. `docs/architecture/06` section 23 already forbids keeping this kind of document once a feature ships.

### 4.3 Missing documents

- **User guide for Features** - nothing in the repo or (as far as the repo shows) the wiki explains Create / Remove Feature, storage location, or the Docker caveat (section 5, item 1).
- **Troubleshooting / FAQ** - no doc covers NeedsRepair recovery, Worker offline behavior, the hook port 9191, or the Linux Worker with Features.
- **GrayMoon changelog** - the history lives in the Desktop repo's README, even though most entries are GrayMoon App changes.
- **Security notes** - the REST API (`Api/Endpoints/*`) has no authentication (no `AddAuthentication` / `RequireAuthorization` anywhere in `src`). The only protection is network placement. This is now stated in `architecture/02` section 15, but there is no deployment / security doc.
- **Root workspace `CLAUDE.md`** - `GrayMoon.Desktop/CLAUDE.md` used to reference `../CLAUDE.md` for the combined layout; no such file exists in this checkout. It now points to `../GrayMoon/CLAUDE.md`.

---

## 5. Code-doc contradictions (source not edited)

These are places where the code contradicts a documented rule. The docs now describe the code as it is; the code should be fixed or the rule restated.

1. **The App touches the filesystem.** `CLAUDE.md` says the App "never touches the local filesystem", but:
   - `WorkspaceFeatureOperations.cs` lines 340 and 732 and `WorkspaceExternalWorktreeOperations.cs` line 79 call `Directory.Exists(worktreePath)`.
   - In Docker these always return false. `ProbeFeatureWorktreeLiveStatusAsync` (lines 664-668) then skips the live probe, so `IsAutomaticallySafe` (lines 737-746) is always false.
   - The result is that Remove Feature in the Docker deployment always demands destructive consent and cannot show real dirty state. It fails safe, but the user is consenting blind.
   - `Services/Git/GitVersionCommandService.cs` also uses `Directory.Exists` / `File.Exists`, but it is never registered in DI (dead code).
2. **The `Migrations.cs` class comment is wrong.** Lines 6-13 say "no schema-patching migrations live here" and "This class otherwise only runs one-time data seeding", but `RunAllAsync` (lines 16-25) runs six schema migrations.
3. **Feature migration swallows every error.** `Migrations.Features.cs` lines 16-34 wrap the whole schema migration and backfill in `catch { }` with the comment "Fresh DB". A real failure on an existing database (for example a locked file or a constraint violation during backfill) is silently ignored, and no log is written.
4. **Dead membership guard.** `WorkspaceFeatureOperations.EnsureNoFeaturesBeforeMembershipChangeAsync` (line 766) is never called. The real guard is in `Repositories/WorkspaceRepository.cs` lines 287 and 322.
5. **Unused options.** `RemoveFeatureOptions.DeleteRemoteBranches` is never set by the UI. `WorkspaceFeatureBaseKind` values other than `CurrentWorkspace` are unused.
6. **Windows-only Feature paths.** Feature paths are built with `\` (`WorkspaceFeatureOperations.cs` lines 1088-1107, `WorkspaceContextPathResolver.cs` lines 32, 116-159), but a Linux Worker is shipped (`Api/Endpoints/AgentEndpoints.cs` lines 7-8). Features on a Linux Worker are untested and undocumented as unsupported.
7. **Legacy events fire for Feature contexts.** Many paths send `WorkspaceSynced` regardless of context (`WorkspaceStateRecomputeScope.cs` line 38, `WorkspaceBranchOperations.cs` lines 191, 254, 291, 526, 592, 687, and others), while `SyncCommandHandler.cs` lines 95-106 comments that legacy events are for the special Workspace only.
8. **Em dashes in source** in 22 `.cs` / `.razor` files, against the repo rule. Examples: `WorkspaceRepositoriesHeader.razor` (3), `WorkspaceFeatureOperations.cs` (2), `WorkspaceBranchOccupancyService.cs` (1).
9. **Bridge origin check is partial.** `MainWindow.xaml.cs` lines 319 and 344-345 apply `WebViewMessageSourceValidator` only to `InstallHostPrerequisites` / `InstallWorker`. `OpenIn*` launches rely only on the path being rooted and existing.
10. **Stray files** in the GrayMoon root: `test-agent.txt`, `test-app.txt`, `test-common.txt`.

---

## 6. Recommendations

| # | Recommendation | Evidence |
| --- | --- | --- |
| R1 | Add a one-line `> Superseded by docs/architecture (Features shipped in #368-#376). Historical design record.` banner to every `docs/worktree/*.md`, or move the folder to `docs/archive/worktree/`. Delete `TODO-Changes-Nav-Notification-Dot.md` and the Multi-Agent prompt. | Section 4.2; `docs/architecture/06` section 23 |
| R2 | Make `CLAUDE.md` the single source for conventions. Reduce `AGENTS.md` to the rules other agents need that are not in `CLAUDE.md`, or reverse that and have `CLAUDE.md` import `AGENTS.md`. Have `.cursor/rules/*.mdc` and `copilot-instructions.md` only point to it. Settle one end-of-task summary rule. | Section 4.1; `CLAUDE.md` line 17, `.cursor/README.md` line 9, `AGENTS.md` section "Never commit or push" |
| R3 | Start a `GrayMoon/CHANGELOG.md` (or GitHub release notes) for App changes. Keep the Desktop README list for Desktop-visible changes only, or link to the changelog. Update `.cursor/rules/desktop-readme-on-feature-change.mdc` to match. | `GrayMoon.Desktop/README.md` "Recent GrayMoon changes" (72 bullets) |
| R4 | Add a Features page to the wiki (or `docs/user/features.md`) covering Create / Remove Feature, the storage root setting, NeedsRepair, branch occupancy, and the Docker / Linux Worker caveats. | `architecture/05` section 32 now has the facts; section 5 items 1 and 6 |
| R5 | Add `docs/troubleshooting.md` covering Worker offline, hook listener port 9191, NeedsRepair Feature, "Cannot change membership while Features exist", and Docker path visibility. | `WorkspaceRepository.cs` lines 287, 322; `AgentOptions.cs` line 10 |
| R6 | Fix code-doc contradictions 1-4 in section 5 (source change, outside this audit): move the `Directory.Exists` probes to the Worker, fix the `Migrations.cs` comment, log in the `Migrations.Features.cs` catch, and remove or wire `EnsureNoFeaturesBeforeMembershipChangeAsync`. | Section 5 |
| R7 | Add a CI check that fails on U+2014 / U+2013 in `*.md`, `*.cs`, `*.razor` (excluding `wwwroot` vendored files), and on LF-only Markdown, so the documented conventions are enforced. | Section 4.1, section 5 item 8 |
| R8 | Document deployment security: the REST API and hubs are unauthenticated, so GrayMoon must not be exposed beyond localhost or a trusted network. | Section 4.3; no `AddAuthentication` in `src` |
| R9 | Decide and document whether Features support a Linux Worker; if not, block Create Feature with a clear message. | Section 5 item 6 |
| R10 | Remove `test-agent.txt`, `test-app.txt`, `test-common.txt` from the repository root, or document their purpose. | Section 5 item 10 |
