# CSS optimization report

Status: Phase 0 (evidence) complete. No CSS, markup or build file has been changed yet.

## Phase 0 - Evidence

### Summary

- The failing regex is `^::deep\s*` with a hard-coded 1 second timeout. It cannot be slow for any selector: all 2,976 calls a build makes take 0.027 ms in total.
- The "expensive selector / large file" hypothesis is **disproven** (high confidence).
- The .NET regex timeout is a wall-clock deadline. A thread that is not scheduled for 1 second in the middle of a match throws `RegexMatchTimeoutException`, however trivial the match. This mechanism is **proven** with a test harness.
- Root cause (high confidence): a build thread on the CI runner was stalled for at least 1 second during one of the regex calls. What stalled it (CPU oversubscription, a GC pause, antivirus, a VM hiccup) is unknown.
- The failure was not reproduced by building: 13 clean Release builds and 1,510 in-process passes of the task ran without a timeout.
- Shrinking the scoped CSS reduces the number of regex calls, so it lowers the chance of the failure roughly in proportion. It cannot remove it. The CI retry (Phase 5) is the fix for robustness; the CSS work is justified by size and consistency only.

### The SDK code

Source: `Microsoft.NET.Sdk.StaticWebAssets.Tasks.dll` from SDK 10.0.401, decompiled with `ilspycmd` (type `Microsoft.AspNetCore.StaticWebAssets.Tasks.RewriteCss`). The `main` branch of dotnet/sdk (`src/StaticWebAssetsSdk/Tasks/ScopedCss/RewriteCss.cs`) has the same declarations. CI used 10.0.404, which was not inspected directly; it lies between the two, so the code is very likely identical.

```csharp
private static readonly TimeSpan s_regexTimeout = TimeSpan.FromSeconds(1L);
private static readonly Regex s_deepCombinatorRegex = new Regex("^::deep\\s*", RegexOptions.None, s_regexTimeout);

// Execute(): Parallel.For(0, FilesToTransform.Length, i => { ... AddScopeToSelectors(...) ... });

// FindScopeInsertionEdits.VisitSelector:
SimpleSelector firstDeepCombinator = source.FirstOrDefault(s => s_deepCombinatorRegex.IsMatch(s.Text));
```

| Fact | Value | Confidence |
|---|---|---|
| Regex | `^::deep\s*`, `RegexOptions.None` (interpreted) | High (read from the binary) |
| Timeout | 1 second, `static readonly`, not configurable | High |
| MSBuild property to change the timeout or disable parallelism | None. The task has only `FilesToTransform` and `SkipIfOutputIsNewer` | High |
| Fixed in a newer SDK | No. `main` is unchanged | High for `main` as fetched on 2026-11-13 |
| Known upstream issue | None found. `gh search issues` in dotnet/sdk, dotnet/aspnetcore and dotnet/razor for "RewriteCss RegexMatchTimeoutException", "RewriteCss Regex timed out" and "RewriteCss task failed unexpectedly" returned nothing | Low to medium (a search can miss an issue) |

The regex is called once per simple selector (each compound part of a selector). It is anchored at the start, so it does a fixed amount of work and has nothing to backtrack over.

How the timeout works (dotnet/runtime v10.0.0, `RegexRunner.cs`): `InitializeTimeout` sets `_timeoutOccursAt = Environment.TickCount64 + _timeout` at the start of each match, and `CheckTimeout` throws when `Environment.TickCount64 >= _timeoutOccursAt`. It measures elapsed wall-clock time, not CPU time used by the match.

### Measurements

Machine: Windows 11, 8 logical processors, SDK 10.0.401. The harness is a console program that references the SDK's own task DLL and calls the public `RewriteCss.AddScopeToSelectors`.

Regex cost against our files:

```
== regex ^::deep\s* : 2976 IsMatch calls per build (one per simple selector)
   total (best-of-200 each): 0.027 ms; slowest single call: 0.100 us  [AddFilesModal.razor.css] '.repository-grid'
   longest simple-selector text: 96 chars
```

Regex cost against hostile input (1 MB strings). The worst case is linear and 350 times under the timeout:

```
1 MB of 'a'                  match=False      150.0 us
'::deep' + 1 MB spaces       match=True      2808.6 us
1 MB of ':'                  match=False        0.9 us
'::dee' x 200k               match=False        0.3 us
1 MB spaces + '::deep'       match=False        0.3 us
```

Whole task per file (parse, find edits, rewrite), single thread, warm, 200 iterations:

```
WorkspaceFeatureSelector.razor.css                17923 B  min   0.937  med   2.940  max  15.603
WorkspaceRepositories.razor.css                   33885 B  min   1.373  med   1.564  max   4.213
MergePullRequestModal.razor.css                   18065 B  min   0.737  med   0.931  max   5.933
BulkMergePullRequestsModal.razor.css              15024 B  min   0.696  med   0.897  max  19.942
MainLayout.razor.css                              11203 B  min   0.687  med   0.814  max   2.256
NewPullRequestModal.razor.css                     16067 B  min   0.721  med   0.798  max   2.187
SUM of medians over 49 files: 15.56 ms
```

No file or selector stands out. The slowest file takes 3 ms.

Mechanism test. One thread runs the regex against the 12-character input `.modal-body ` in a loop. A second thread freezes it for 1.2 seconds at random moments (`SuspendThread`), which simulates the operating system not scheduling it:

```
freezes of 1.2 s: 10; IsMatch calls completed: 895,228; RegexMatchTimeoutException thrown: 5
```

Five of ten freezes produced the exact exception from the CI log, on an input that takes under a microsecond to reject.

Clean Release builds (`dotnet build src\GrayMoon.App\GrayMoon.App.csproj -c Release --no-restore --no-incremental`), `RewriteCss` time from the MSBuild performance summary:

```
default #1 exit=0 total=17.5s RewriteCss=123ms regexErrors=0
default #2 exit=0 total=17.2s RewriteCss=122ms regexErrors=0
default #3 exit=0 total=18.3s RewriteCss=180ms regexErrors=0
default #4 exit=0 total=22.6s RewriteCss=185ms regexErrors=0
default #5 exit=0 total=22.4s RewriteCss=165ms regexErrors=0
m1 #1 exit=0 total=19.8s RewriteCss=127ms regexErrors=0
m1 #2 exit=0 total=21.7s RewriteCss=183ms regexErrors=0
m1 #3 exit=0 total=20.9s RewriteCss=185ms regexErrors=0
m1 #4 exit=0 total=22.7s RewriteCss=167ms regexErrors=0
m1 #5 exit=0 total=20.9s RewriteCss=194ms regexErrors=0
```

The first build with a binlog measured `RewriteCss` at 157 ms (`_ProcessScopedCssFiles` 159 ms, `ConcatenateCssFiles` 77 ms, `Csc` 33,884 ms). `-m:1` makes no difference, which is expected: the parallelism is `Parallel.For` inside the task, not MSBuild nodes.

In-process repetition of the task's loop (`Parallel.For` over the 49 files):

```
--- harness stress, NO load, 30 s
rounds (each = one full RewriteCss pass over 49 files): 1,510; timeouts: 0; slowest round: 94.9 ms; avg round: 19.88 ms
--- harness stress, 24 AboveNormal spinners on 8 cores, 60 s
rounds (each = one full RewriteCss pass over 49 files): 7; timeouts: 0; slowest round: 25121.9 ms; avg round: 9285.27 ms
```

The loaded run is a small sample and was cut short because it froze the desktop. The three builds planned under load did not run under load and are not counted. What the run does show: under heavy oversubscription one pass went from 20 ms to 25 seconds, so stalls of more than a second inside the task do happen under load. A timeout additionally needs the stall to fall inside the microsecond window of a regex call, which is why the failure is rare.

### Root cause

| Statement | Status | Confidence |
|---|---|---|
| A selector or a large file makes the regex slow | Disproven | High |
| The exception means a thread was not running for at least 1 second between the start of a match and its timeout check | Follows from the SDK and runtime code, and from the mechanism test | High |
| The stall came from CPU oversubscription on the 4-core runner (`Parallel.For` in the task, several MSBuild nodes, the compiler server) | Hypothesis. A GC pause, antivirus scanning or a VM pause would look the same | Medium |
| Smaller scoped CSS prevents the failure | False. It reduces the number of regex calls (2,976 today), so the chance drops in proportion, but any remaining call can still hit a stall | High |

Why it cannot be reproduced on demand: the failure needs a stall of 1 second or more to land inside one of 2,976 windows that are each well under a microsecond wide. It is a race against the scheduler of a loaded machine.

### What this means for the plan

- Phase 5 (one retry of the build step) is the measure that addresses the failure. It does not depend on Phases 1 to 4.
- `<ScopedCssEnabled>false</ScopedCssEnabled>` would remove the task, and with it the failure, but needs all 240 KB of scoped CSS converted to global CSS without the scope attribute. To be evaluated in Phase 5.
- Phases 1 to 4 are worth doing for bundle size and consistent styling. They lower the exposure but are not a fix.
- The bug is worth reporting to dotnet/sdk: a wall-clock regex timeout on a trivial anchored pattern can only produce false failures.

### Inventory of scoped CSS

49 files, 239,844 bytes, 1,358 rules, 1,618 selectors, 2,976 simple selectors (regex calls), 437 `::deep`, 7 `@media`, 9 `@keyframes`. No `:is()` or `:where()` anywhere. The generated bundle `GrayMoon.App.styles.css` is 263,125 bytes.

Counts come from the SDK's own CSS parser (`Microsoft.Css.Parser`). "Depth" is the largest number of compound parts in one selector.

| File | Bytes | Rules | Selectors | Simple selectors | Longest selector (chars) | Depth | `::deep` | `:not` | `:has` | `@media` | `@keyframes` |
|---|---|---|---|---|---|---|---|---|---|---|---|
| `Components/Pages/WorkspaceRepositories.razor.css` | 33887 | 168 | 217 | 636 | 136 | 5 | 168 | 5 | 1 | 1 | 0 |
| `Components/Modals/MergePullRequestModal.razor.css` | 18065 | 83 | 99 | 219 | 114 | 4 | 98 | 21 | 0 | 0 | 1 |
| `Components/Features/WorkspaceFeatureSelector.razor.css` | 17934 | 78 | 109 | 143 | 134 | 2 | 0 | 5 | 8 | 0 | 0 |
| `Components/Pages/WorkspaceActions.razor.css` | 16686 | 81 | 105 | 228 | 110 | 6 | 0 | 0 | 0 | 0 | 0 |
| `Components/Modals/NewPullRequestModal.razor.css` | 16069 | 90 | 103 | 145 | 79 | 4 | 11 | 9 | 0 | 0 | 0 |
| `Components/Modals/BulkMergePullRequestsModal.razor.css` | 15024 | 74 | 86 | 193 | 86 | 4 | 85 | 14 | 0 | 0 | 1 |
| `Components/Layout/MainLayout.razor.css` | 11203 | 59 | 73 | 184 | 99 | 5 | 34 | 0 | 0 | 3 | 0 |
| `Components/Modals/SwitchBranchModal.razor.css` | 6967 | 45 | 51 | 68 | 64 | 2 | 0 | 0 | 0 | 0 | 0 |
| `Components/Modals/GhaLogsModal.razor.css` | 6317 | 53 | 55 | 61 | 53 | 2 | 2 | 0 | 0 | 0 | 0 |
| `Components/Pages/Settings.razor.css` | 6265 | 25 | 26 | 34 | 88 | 3 | 0 | 0 | 2 | 1 | 0 |
| `Components/Pages/WorkspacePackages.razor.css` | 5677 | 30 | 43 | 87 | 81 | 4 | 0 | 0 | 0 | 0 | 0 |
| `Components/Layout/NavMenu.razor.css` | 5494 | 33 | 36 | 80 | 50 | 5 | 15 | 0 | 0 | 1 | 0 |
| `Components/Modals/AddFilesModal.razor.css` | 5465 | 44 | 51 | 79 | 53 | 3 | 3 | 0 | 1 | 0 | 0 |
| `Components/Pages/WorkspaceDependencies.razor.css` | 5046 | 30 | 31 | 39 | 89 | 2 | 0 | 0 | 0 | 0 | 0 |
| `Components/Modals/BranchModal.razor.css` | 5027 | 38 | 41 | 53 | 64 | 2 | 0 | 0 | 0 | 0 | 0 |
| `Components/Modals/WorkspaceRepositoriesModal.razor.css` | 4783 | 37 | 46 | 86 | 53 | 3 | 3 | 0 | 1 | 0 | 0 |
| `Components/Shared/WorkspaceActionNotificationPanel.razor.css` | 4601 | 25 | 29 | 37 | 72 | 3 | 0 | 0 | 0 | 0 | 2 |
| `Components/Layout/ReconnectModal.razor.css` | 4284 | 14 | 27 | 40 | 104 | 2 | 0 | 0 | 0 | 0 | 4 |
| `Components/Shared/GhaWorkflowLiveTerminal.razor.css` | 4162 | 15 | 15 | 16 | 80 | 2 | 0 | 0 | 0 | 0 | 0 |
| `Components/Modals/VersionConfigModal.razor.css` | 3341 | 22 | 22 | 23 | 62 | 2 | 0 | 0 | 0 | 0 | 0 |
| `Components/Modals/CustomDependenciesModal.razor.css` | 3301 | 26 | 29 | 56 | 54 | 3 | 2 | 0 | 0 | 0 | 0 |
| `Components/Pages/Repositories.razor.css` | 2691 | 21 | 28 | 53 | 79 | 4 | 0 | 0 | 0 | 0 | 0 |
| `Components/Shared/RepositoryPicker.razor.css` | 2501 | 16 | 17 | 18 | 54 | 2 | 0 | 0 | 0 | 0 | 0 |
| `Components/Modals/ConfirmModal.razor.css` | 2482 | 16 | 17 | 20 | 61 | 2 | 0 | 0 | 0 | 0 | 0 |
| `Components/Shared/FilterSearchInput.razor.css` | 2448 | 20 | 21 | 24 | 70 | 2 | 0 | 0 | 1 | 0 | 0 |
| `Components/Pages/Worker.razor.css` | 2430 | 23 | 23 | 46 | 59 | 4 | 0 | 0 | 0 | 0 | 0 |
| `Components/Shared/WorkspacePageGate.razor.css` | 2193 | 9 | 13 | 15 | 44 | 2 | 0 | 0 | 0 | 0 | 0 |
| `Components/Pages/Connectors.razor.css` | 2082 | 12 | 18 | 32 | 85 | 3 | 0 | 0 | 0 | 0 | 0 |
| `Components/Modals/PushWithDependenciesModal.razor.css` | 2050 | 15 | 15 | 19 | 63 | 2 | 0 | 0 | 0 | 0 | 0 |
| `Components/Shared/WorkspaceRepositoriesHeader.razor.css` | 1887 | 13 | 19 | 31 | 72 | 3 | 0 | 0 | 0 | 0 | 0 |
| `Components/Modals/PrepareWorkspaceModal.razor.css` | 1848 | 14 | 14 | 16 | 64 | 2 | 0 | 1 | 0 | 0 | 0 |
| `Components/Modals/UpdateSingleRepositoryDependenciesModal.razor.css` | 1813 | 13 | 13 | 18 | 65 | 3 | 0 | 0 | 0 | 0 | 0 |
| `Components/Modals/RestoreWorkspaceModal.razor.css` | 1512 | 13 | 13 | 15 | 35 | 2 | 0 | 0 | 0 | 0 | 0 |
| `Components/Pages/WorkspaceProjects.razor.css` | 1457 | 12 | 14 | 23 | 81 | 3 | 0 | 0 | 0 | 1 | 0 |
| `Components/Pages/Workspaces.razor.css` | 1438 | 14 | 17 | 24 | 85 | 3 | 0 | 1 | 0 | 0 | 0 |
| `Components/Features/WorkspaceContextBar.razor.css` | 1381 | 6 | 8 | 20 | 68 | 3 | 6 | 0 | 0 | 0 | 0 |
| `Components/Modals/ReturnToDefaultOptionsModal.razor.css` | 1331 | 10 | 10 | 12 | 64 | 2 | 0 | 0 | 0 | 0 | 0 |
| `Components/Shared/Toast.razor.css` | 1276 | 4 | 4 | 4 | 22 | 1 | 0 | 0 | 0 | 0 | 1 |
| `Components/Shared/LoadingOverlay.razor.css` | 1217 | 7 | 8 | 10 | 40 | 2 | 0 | 0 | 0 | 0 | 0 |
| `Components/Modals/DefaultBranchWarningModal.razor.css` | 1045 | 8 | 8 | 10 | 61 | 2 | 0 | 0 | 0 | 0 | 0 |
| `Components/Features/RemoveFeatureModal.razor.css` | 1041 | 6 | 7 | 12 | 60 | 2 | 5 | 4 | 0 | 0 | 0 |
| `Components/Modals/ViewFileModal.razor.css` | 1022 | 9 | 9 | 10 | 50 | 2 | 1 | 0 | 0 | 0 | 0 |
| `Components/Modals/VersionFilesCommitModal.razor.css` | 917 | 7 | 7 | 7 | 56 | 1 | 0 | 0 | 0 | 0 | 0 |
| `Components/Pages/WorkspaceFiles.razor.css` | 659 | 6 | 7 | 10 | 75 | 2 | 0 | 0 | 0 | 0 | 0 |
| `Components/Features/CreateFeatureModal.razor.css` | 554 | 5 | 5 | 7 | 41 | 2 | 0 | 0 | 0 | 0 | 0 |
| `Components/Modals/WorkspaceModal.razor.css` | 407 | 4 | 4 | 8 | 42 | 2 | 4 | 1 | 0 | 0 | 0 |
| `Components/Shared/MergePullRequestIcon.razor.css` | 257 | 2 | 2 | 2 | 30 | 1 | 0 | 0 | 0 | 0 | 0 |
| `Components/Shared/PullRequestIcon.razor.css` | 245 | 2 | 2 | 2 | 24 | 1 | 0 | 0 | 0 | 0 | 0 |
| `Components/Pages/Home.razor.css` | 62 | 1 | 1 | 1 | 15 | 1 | 0 | 0 | 0 | 0 | 0 |

The five longest selectors:

- `WorkspaceRepositories.razor.css` (136): `::deep .repositories-table .dependency-level-header td.dependency-level-header-cell .dependency-level-title.dependency-level-no-projects`
- `WorkspaceFeatureSelector.razor.css` (134): `.workspace-feature-selector__launch-row:not(:has(.workspace-feature-selector__repo-flyout)) > .workspace-feature-selector__launch-item`
- `MergePullRequestModal.razor.css` (114): `::deep .merge-pr-method-menu .dropdown-item.merge-pr-method-item--close:focus-visible .merge-pr-method-item__title`
- `WorkspaceActions.razor.css` (110): `.workspace-actions-header .workspace-actions__refresh-status.is-short .workspace-actions__refresh-status-short`
- `ReconnectModal.razor.css` (104): `#components-reconnect-modal.components-reconnect-retrying .components-reconnect-repeated-attempt-visible`

Global CSS for comparison (bytes): `app.css` 52,353; `css/git-changes.css` 42,787; `css/loading-overlay.css` 12,667; `css/ai-generation.css` 10,616; `css/workspace-grid-search.css` 1,420; `bootstrap.min.css` 162,726; `bootstrap-icons.css` 100,297.

## Phase 1 - Duplication and dead CSS

Status: analysis complete, nothing changed. Waiting for approval of the proposal below.

### Summary

- The scoped CSS is mostly unique rules. Duplication is real but small: about 12.4 KB of identical rules across files, out of about 159 KB of actual CSS.
- The 240 KB of source is 38 KB comments, about 43 KB indentation and line breaks, and about 159 KB of CSS.
- On the wire the bundle is already small: 263,125 bytes raw, 44,773 gzip, 35,750 brotli. `MapStaticAssets` serves it compressed and fingerprinted, so a browser downloads it once per version.
- A realistic cleanup removes about 20 to 27 KB of CSS (13 to 17 percent), which is 3 to 4 KB on the wire and a similar share of the regex calls.
- The "15 modal files duplicate the same rules" assumption holds only for three pairs of modals. The three largest modal files after `MergePullRequestModal` share almost nothing with any other file.

### Method

A script parsed all 49 scoped files and the 5 global files (`app.css`, `css/*.css`) into 1,618 scoped and 763 global selector-level rules, with whitespace normalized and declarations sorted. Sizes below are normalized bytes (no comments or indentation), so they understate source bytes by roughly a third.

Class usage was checked against every `.razor`, `.cs`, `.js`, `.cshtml` and `.html` file under `src/` (excluding `bin`, `obj`, `wwwroot/monaco`, `wwwroot/lib`), as whole tokens. A class is "possibly dynamic" when a prefix of it appears directly before an interpolation or a string concatenation.

### Finding 1 - identical rules in more than one scoped file

106 rules appear in 2 or more files with the same selector (ignoring `::deep`), the same `@media` context and the same declarations. 242 copies in total; removing the extra copies saves about 12.4 KB.

Share of each file that is identical to a rule elsewhere: `PrepareWorkspaceModal` 96 percent, `WorkspaceRepositoriesHeader` 79, `BranchModal` 58, `SwitchBranchModal` 48, `WorkspacePageGate` 40, `WorkspaceRepositoriesModal` 37, `BulkMergePullRequestsModal` 33, `AddFilesModal` 32, `Repositories` 31, `MergePullRequestModal` 26, `WorkspaceActions` 9, `WorkspaceRepositories` 5. `NewPullRequestModal`, `WorkspaceFeatureSelector`, `MainLayout`, `GhaLogsModal`, `Settings` and `NavMenu`: 0 to 1 percent.

Risk in the tables: "low" means no rule outside the group mentions the subject class; "medium" means another file styles the same class, so order and specificity must be checked rule by rule; "high" means another scoped file has the same selector with different declarations, so a global copy would change that component.

Totals: 52 low, 31 medium, 23 high.

A scoped rule has one more attribute selector than its source text, shown as "Specificity scoped". A global copy loses it unless the selector is prefixed with a class of the component root, which restores the same specificity.

**BulkMergePullRequestsModal + MergePullRequestModal** - 27 rules, about 3131 bytes removable

| Selector | `::deep` | Specificity scoped | Specificity global | Competing rules elsewhere | Risk |
|---|---|---|---|---|---|
| `.merge-pr-status-action` | yes | 0,2,0 | 0,1,0 | none | low |
| `.merge-pr-status-icon` | yes | 0,2,0 | 0,1,0 | none | low |
| `.merge-pr-method-menu .dropdown-item` | yes | 0,3,0 | 0,2,0 | WorkspaceFeatureSelector, WorkspaceActions, G:app.css, G:workspace-grid-search.css | medium |
| `.merge-pr-return-default-check .form-check-input:not(:checked)` | yes | 0,4,0 | 0,3,0 | RemoveFeatureModal, NewPullRequestModal, PrepareWorkspaceModal, WorkspaceModal | medium |
| `.merge-pr-btn-github:disabled` | yes | 0,3,0 | 0,2,0 | none | low |
| `.merge-pr-btn-close--muted` | yes | 0,2,0 | 0,1,0 | none | low |
| `.merge-pr-method-item` | yes | 0,2,0 | 0,1,0 | none | low |
| `.merge-pr-method-item__check` | yes | 0,2,0 | 0,1,0 | none | low |
| `.merge-pr-method-item__subtitle` | yes | 0,2,0 | 0,1,0 | none | low |
| `.merge-pr-btn-close--muted:focus-visible:not(:disabled)` | yes | 0,4,0 | 0,3,0 | none | low |
| `.merge-pr-method-item__text` | yes | 0,2,0 | 0,1,0 | none | low |
| `.merge-pr-btn-github:focus-visible:not(:disabled)` | yes | 0,4,0 | 0,3,0 | none | low |
| `.merge-pr-btn-close--muted:hover:not(:disabled)` | yes | 0,4,0 | 0,3,0 | none | low |
| `.merge-pr-method-menu .dropdown-item:focus-visible .merge-pr-method-item__title` | yes | 0,5,0 | 0,4,0 | none | low |
| `.merge-pr-header-link` | yes | 0,2,0 | 0,1,0 | none | low |
| `.merge-pr-method-item__title` | yes | 0,2,0 | 0,1,0 | none | low |
| `.merge-pr-btn-github:hover:not(:disabled)` | yes | 0,4,0 | 0,3,0 | none | low |
| `.merge-pr-status-title` | yes | 0,2,0 | 0,1,0 | none | low |
| `.merge-pr-method-menu .dropdown-item:focus-visible` | yes | 0,4,0 | 0,3,0 | WorkspaceFeatureSelector, WorkspaceActions, G:app.css, G:workspace-grid-search.css | medium |
| `.merge-pr-method-item + .merge-pr-method-item` | yes | 0,3,0 | 0,2,0 | none | low |
| `.merge-pr-method-menu .dropdown-item:hover .merge-pr-method-item__title` | yes | 0,5,0 | 0,4,0 | none | low |
| `.merge-pr-method-menu .dropdown-item:hover` | yes | 0,4,0 | 0,3,0 | WorkspaceFeatureSelector, WorkspaceActions, G:app.css, G:workspace-grid-search.css | medium |
| `.merge-pr-status-icon--success` | yes | 0,2,0 | 0,1,0 | none | low |
| `.merge-pr-status-icon--pending` | yes | 0,2,0 | 0,1,0 | none | low |
| `.merge-pr-status-icon--neutral` | yes | 0,2,0 | 0,1,0 | none | low |
| `.merge-pr-status-icon--danger` | yes | 0,2,0 | 0,1,0 | none | low |
| `.merge-pr-status-text` | yes | 0,2,0 | 0,1,0 | none | low |

**BranchModal + PrepareWorkspaceModal + SwitchBranchModal** - 12 rules, about 2666 bytes removable

| Selector | `::deep` | Specificity scoped | Specificity global | Competing rules elsewhere | Risk |
|---|---|---|---|---|---|
| `.new-branch-based-on-list` | no | 0,2,0 | 0,1,0 | G:app.css | medium |
| `.new-branch-based-on-trigger` | no | 0,2,0 | 0,1,0 | G:app.css | medium |
| `.new-branch-based-on-list.new-branch-based-on-list--scroll` | no | 0,3,0 | 0,2,0 | none | low |
| `.new-branch-based-on-list--scroll::-webkit-scrollbar-thumb` | no | 0,2,1 | 0,1,1 | none | low |
| `.new-branch-based-on-list--scroll::-webkit-scrollbar-track` | no | 0,2,1 | 0,1,1 | none | low |
| `.new-branch-based-on-list--scroll::-webkit-scrollbar-thumb:hover` | no | 0,3,1 | 0,2,1 | none | low |
| `.new-branch-based-on-trigger .text-truncate` | no | 0,3,0 | 0,2,0 | none | low |
| `.new-branch-based-on-item.selected` | no | 0,3,0 | 0,2,0 | none | low |
| `.new-branch-based-on-item:hover` | no | 0,3,0 | 0,2,0 | none | low |
| `.new-branch-based-on-list--scroll::-webkit-scrollbar` | no | 0,2,1 | 0,1,1 | none | low |
| `.new-branch-based-on-item` | no | 0,2,0 | 0,1,0 | none | low |
| `.modal-dialog > .modal-content` | no | 0,3,0 | 0,2,0 | AddFilesModal, BulkMergePullRequestsModal, ConfirmModal, CustomDependenciesModal | medium |

**AddFilesModal + WorkspaceRepositoriesModal** - 16 rules, about 1216 bytes removable

| Selector | `::deep` | Specificity scoped | Specificity global | Competing rules elsewhere | Risk |
|---|---|---|---|---|---|
| `.workspace-repo-checkbox:checked` | no | 0,3,0 | 0,2,0 | CustomDependenciesModal | medium |
| `.workspace-repo-checkbox` | no | 0,2,0 | 0,1,0 | CustomDependenciesModal | medium |
| `.repository-list .repository-grid` | no | 0,3,0 | 0,2,0 | G:app.css | medium |
| `.repository-grid tbody` | no | 0,2,1 | 0,1,1 | G:app.css | high |
| `.repository-grid tbody::-webkit-scrollbar-thumb` | no | 0,2,2 | 0,1,2 | G:app.css | medium |
| `.repository-grid tbody::-webkit-scrollbar-track` | no | 0,2,2 | 0,1,2 | G:app.css | medium |
| `.repository-grid` | no | 0,2,0 | 0,1,0 | G:app.css | high |
| `.repository-list.border` | no | 0,3,0 | 0,2,0 | G:app.css | medium |
| `.repository-grid tbody::-webkit-scrollbar-thumb:hover` | no | 0,3,2 | 0,2,2 | G:app.css | medium |
| `.repository-grid th` | no | 0,2,1 | 0,1,1 | G:app.css | high |
| `.repository-grid td` | no | 0,2,1 | 0,1,1 | G:app.css | high |
| `.repository-grid thead` | no | 0,2,1 | 0,1,1 | G:app.css | high |
| `.repository-grid thead` | no | 0,2,1 | 0,1,1 | G:app.css | high |
| `.repository-grid th` | no | 0,2,1 | 0,1,1 | G:app.css | high |
| `.repository-grid td` | no | 0,2,1 | 0,1,1 | G:app.css | high |
| `.repository-grid` | no | 0,2,0 | 0,1,0 | G:app.css | high |

**Repositories + WorkspaceActions + WorkspacePackages + WorkspaceProjects + WorkspaceRepositories** - 4 rules, about 1116 bytes removable

| Selector | `::deep` | Specificity scoped | Specificity global | Competing rules elsewhere | Risk |
|---|---|---|---|---|---|
| `.page-container` | no | 0,2,0 | 0,1,0 | Home, WorkspaceDependencies, G:app.css, G:git-changes.css | high |
| `.page-card .card-body` | no | 0,3,0 | 0,2,0 | Settings, Worker, G:app.css | high |
| `.page-card .table-responsive` | no | 0,3,0 | 0,2,0 | G:app.css | medium |
| `.page-card` | no | 0,2,0 | 0,1,0 | Settings, Worker, G:app.css | medium |

**BranchModal + SwitchBranchModal** - 14 rules, about 1071 bytes removable

| Selector | `::deep` | Specificity scoped | Specificity global | Competing rules elsewhere | Risk |
|---|---|---|---|---|---|
| `.branch-list` | no | 0,2,0 | 0,1,0 | none | low |
| `.branch-item` | no | 0,2,0 | 0,1,0 | none | low |
| `.branch-list::-webkit-scrollbar-thumb` | no | 0,2,1 | 0,1,1 | none | low |
| `.branch-list::-webkit-scrollbar-track` | no | 0,2,1 | 0,1,1 | none | low |
| `.branch-item.bg-light` | no | 0,3,0 | 0,2,0 | none | low |
| `.branch-list::-webkit-scrollbar-thumb:hover` | no | 0,3,1 | 0,2,1 | none | low |
| `.branch-item:hover` | no | 0,3,0 | 0,2,0 | none | low |
| `.switch-branch-field-block .form-label` | no | 0,3,0 | 0,2,0 | NewPullRequestModal, G:app.css | medium |
| `.switch-branch-tab-count` | no | 0,2,0 | 0,1,0 | none | low |
| `.switch-branch-label-row` | no | 0,2,0 | 0,1,0 | none | low |
| `.branch-item:last-child` | no | 0,3,0 | 0,2,0 | none | low |
| `.branch-list::-webkit-scrollbar` | no | 0,2,1 | 0,1,1 | none | low |
| `.branch-delete-link:hover i` | no | 0,3,1 | 0,2,1 | none | low |
| `.branch-delete-link:hover` | no | 0,3,0 | 0,2,0 | none | low |

**WorkspacePageGate + WorkspaceRepositories** - 4 rules, about 708 bytes removable

| Selector | `::deep` | Specificity scoped | Specificity global | Competing rules elsewhere | Risk |
|---|---|---|---|---|---|
| `.page-warning-callout` | no | 0,2,0 | 0,1,0 | none | low |
| `.page-callout-with-actions > span` | no | 0,2,1 | 0,1,1 | none | low |
| `.grid-page-header > .page-callout` | no | 0,3,0 | 0,2,0 | none | low |
| `.page-callout-with-actions` | no | 0,2,0 | 0,1,0 | none | low |

**WorkspaceActions + WorkspaceRepositoriesHeader** - 6 rules, about 594 bytes removable

| Selector | `::deep` | Specificity scoped | Specificity global | Competing rules elsewhere | Risk |
|---|---|---|---|---|---|
| `.workspace-repository-actions-menu` | no | 0,2,0 | 0,1,0 | none | low |
| `.branch-menu-divider` | no | 0,2,0 | 0,1,0 | WorkspaceFeatureSelector | medium |
| `.btn-group > .btn-danger + .btn-danger.dropdown-toggle-split` | no | 0,5,0 | 0,4,0 | WorkspaceActionNotificationPanel | medium |
| `.dropdown-toggle-split` | no | 0,2,0 | 0,1,0 | WorkspaceActionNotificationPanel | medium |
| `.dropdown-toggle-split::after` | no | 0,2,1 | 0,1,1 | WorkspaceActionNotificationPanel | medium |
| `.branch-menu-list` | no | 0,2,0 | 0,1,0 | WorkspaceActionNotificationPanel | high |

**WorkspaceActionNotificationPanel + WorkspaceRepositoriesHeader** - 8 rules, about 455 bytes removable

| Selector | `::deep` | Specificity scoped | Specificity global | Competing rules elsewhere | Risk |
|---|---|---|---|---|---|
| `.btn-group > .btn-warning + .btn-outline-secondary.dropdown-toggle-split` | no | 0,5,0 | 0,4,0 | WorkspaceActions | medium |
| `.btn-group > .btn-danger + .btn-outline-secondary.dropdown-toggle-split` | no | 0,5,0 | 0,4,0 | WorkspaceActions | medium |
| `.dropdown-item--level-only` | no | 0,2,0 | 0,1,0 | none | high |
| `.dropdown-item--level-only:hover` | no | 0,3,0 | 0,2,0 | none | high |
| `.dropdown-item--level-only:focus` | no | 0,3,0 | 0,2,0 | none | high |
| `.dropdown-item--level-only:hover` | no | 0,3,0 | 0,2,0 | none | high |
| `.dropdown-item--level-only:focus` | no | 0,3,0 | 0,2,0 | none | high |
| `.dropdown-item--level-only` | no | 0,2,0 | 0,1,0 | none | high |

**Repositories + WorkspaceRepositories** - 6 rules, about 402 bytes removable

| Selector | `::deep` | Specificity scoped | Specificity global | Competing rules elsewhere | Risk |
|---|---|---|---|---|---|
| `.repositories-table td a.repo-link:hover` | mixed | 0,4,2 | 0,3,2 | WorkspaceActions, WorkspacePackages | medium |
| `.repositories-table td .repo-link:hover` | mixed | 0,4,1 | 0,3,1 | WorkspaceActions, WorkspacePackages | medium |
| `.repositories-table th` | mixed | 0,2,1 | 0,1,1 | G:app.css | medium |
| `.repositories-table td` | mixed | 0,2,1 | 0,1,1 | G:app.css | medium |
| `.repositories-table tbody tr` | no | 0,2,2 | 0,1,2 | G:app.css | medium |
| `.repositories-table thead` | no | 0,2,1 | 0,1,1 | G:app.css | medium |

**NewPullRequestModal + PrepareWorkspaceModal + RemoveFeatureModal + WorkspaceModal** - 1 rules, about 262 bytes removable

| Selector | `::deep` | Specificity scoped | Specificity global | Competing rules elsewhere | Risk |
|---|---|---|---|---|---|
| `.form-check-input:not(:checked)` | mixed | 0,3,0 | 0,2,0 | BulkMergePullRequestsModal, MergePullRequestModal, Settings, G:app.css | medium |

**RestoreWorkspaceModal + WorkspaceModal** - 3 rules, about 252 bytes removable

| Selector | `::deep` | Specificity scoped | Specificity global | Competing rules elsewhere | Risk |
|---|---|---|---|---|---|
| `.modal-dialog.modal-dialog-centered` | mixed | 0,3,0 | 0,2,0 | G:app.css | medium |
| `.modal-content` | mixed | 0,2,0 | 0,1,0 | AddFilesModal, BranchModal, BulkMergePullRequestsModal, ConfirmModal | high |
| `.modal-body` | mixed | 0,2,0 | 0,1,0 | AddFilesModal, ConfirmModal, DefaultBranchWarningModal, GhaLogsModal | high |

**AddFilesModal + WorkspaceRepositoriesModal + Workspaces** - 2 rules, about 220 bytes removable

| Selector | `::deep` | Specificity scoped | Specificity global | Competing rules elsewhere | Risk |
|---|---|---|---|---|---|
| `.workspaces-table th` | no | 0,2,1 | 0,1,1 | WorkspaceFiles | high |
| `.workspaces-table td` | no | 0,2,1 | 0,1,1 | WorkspaceFiles | high |

**WorkspaceActionNotificationPanel + WorkspaceActions + WorkspaceRepositoriesHeader** - 1 rules, about 184 bytes removable

| Selector | `::deep` | Specificity scoped | Specificity global | Competing rules elsewhere | Risk |
|---|---|---|---|---|---|
| `.branch-menu-backdrop` | no | 0,2,0 | 0,1,0 | none | low |

**AddFilesModal + ViewFileModal** - 1 rules, about 67 bytes removable

| Selector | `::deep` | Specificity scoped | Specificity global | Competing rules elsewhere | Risk |
|---|---|---|---|---|---|
| `.modal-content` | yes | 0,2,0 | 0,1,0 | BranchModal, BulkMergePullRequestsModal, ConfirmModal, CustomDependenciesModal | high |

**WorkspacePackages + WorkspaceProjects** - 1 rules, about 53 bytes removable

| Selector | `::deep` | Specificity scoped | Specificity global | Competing rules elsewhere | Risk |
|---|---|---|---|---|---|
| `.col-framework` | no | 0,2,0 | 0,1,0 | none | low |

### Finding 2 - same declarations under different selectors

11 declaration blocks of 3 or more declarations repeat in 3 or more files under different selectors, about 8 KB in total. The useful ones:

| Declarations | Where | Bytes |
|---|---|---|
| `flex: 0 0 auto; min-width: 0; overflow: visible; text-overflow: clip; white-space: normal` | page subtitle in 7 pages (`Connectors`, `Repositories`, `WorkspaceDependencies`, `WorkspaceFiles`, `WorkspacePackages`, `WorkspaceProjects`, `Workspaces`) | 1,240 |
| `overflow: hidden; text-overflow: ellipsis; white-space: nowrap` | `th` and `td` of the grid table in 9 pages and modals | 1,114 |
| `display: table; table-layout: fixed; width: 100%` | `thead` and `tbody tr` of the grid table in 9 pages and modals | 912 |
| GitHub-style green button, hover and disabled states | `RemoveFeatureModal`, `MergePullRequestModal`, `BulkMergePullRequestsModal` | 1,792 |
| scroll box for package lists | `DefaultBranchWarningModal`, `PushWithDependenciesModal`, `UpdateSingleRepositoryDependenciesModal` | 573 |
| `branch-menu-backdrop`, `branch-menu-divider` | `WorkspaceActions`, `WorkspaceRepositoriesHeader`, `WorkspaceActionNotificationPanel`, `WorkspaceFeatureSelector` | 792 |

These can be merged without markup changes by listing the existing selectors in one global rule. A shared class (for example one `gm-grid-table` class on every grid table) would be cleaner but needs markup changes in 9 components.

### Finding 3 - scoped rules that repeat global CSS

21 scoped rules have a selector that also exists in global CSS. 4 are fully redundant (about 300 bytes): three `.workspace-feature-selector__menu .dropdown-item.active` rules that repeat `css/workspace-grid-search.css`, and `.repositories-count-link:hover` that repeats `app.css`. The other 17 deliberately override the global rule (modal height, `overflow: visible`) and stay.

### Finding 4 - unused rules (candidates, each to be verified before removal)

39 classes have no reference at all and 35 more look dynamically built. Of the 39, 17 are in scoped files. The others are in global files, where several are Bootstrap or framework classes (`alert-info`, `form-select`, `page-link`, `validation-message`, `blazor-error-boundary`, `monaco-diff-editor`) and must stay.

Scoped candidates:

| File | Classes | Note |
|---|---|---|
| `WorkspaceActions` | `action-link`, `actions-buttons`, `col-event` | no reference |
| `WorkspaceRepositories` | `col-commits`, `col-deps`, `col-sync`, `dependency-badge-tooltip-file`, `dependency-badge-tooltip-file-count`, `dependency-badge-tooltip-versions--after-file`, `workspaces-breadcrumb-link` | no reference |
| `BranchModal` | `branch-delete-link-disabled`; also `branch-delete-link`, used only in `SwitchBranchModal` | no reference in this component |
| `GhaLogsModal` | `gha-job-anchor` | no reference |
| `VersionFilesCommitModal` | `version-files-commit-label` | no reference |
| `Settings` | `worker-info-row` | uncertain: `"worker-" +` exists in code |

Kept, not dead: `ansi-30` to `ansi-97` and `gha-log-line--*` (built in `ghaLogs.js` and Razor), `deps-graph-legend__swatch--*` (concatenated), `components-reconnect-*` (set by the Blazor framework), `is-collapsed` and `is-ellipsis` (set by the header-fit scripts).

A second kind of dead rule: a scoped selector without `::deep` only matches elements written in that component's own markup. 15 selectors in `WorkspaceRepositories.razor.css` (`.workspace-repos-header`, `.workspace-repos-title-row`, `.workspace-repos-title`, `.workspace-repos-subtitle`, `.workspace-repos-title-spacer`, `.workspace-repos-worker-tasks`, `.workspace-repos-header-actions`, `.repositories-count-link`) style classes that appear only in other components (`WorkspaceRepositoriesHeader.razor` and others), so they can never match. Similar: `MergePullRequestIcon` and `PullRequestIcon` `--menu` rules, two `.filter-search-clear` rules in `WorkspaceActions`, two header rules in `WorkspaceDependencies`. About 1.5 to 2 KB. Each needs a check in the running app (computed style before and after) because a class can reach an element through a parameter.

Total dead-rule estimate for scoped files: 3 to 5 KB.

### Finding 5 - repeated selectors inside one file

Several files repeat a selector and override an earlier declaration. Listed by the script; small, and merging them changes nothing visible. To be done only inside files that are touched anyway.

### Keyframes

9 scoped `@keyframes`, each referenced only in its own file: `bulk-merge-pr-placeholder-pulse`, `merge-pr-placeholder-pulse`, four `components-reconnect-*`, `notif-slide-in`, `notif-tooltip-fade-in`, `toast-in`. The two placeholder pulses are candidates for one shared global keyframe. Any rule moved to global CSS must take its keyframe with it, because the scoped keyframe name gets the scope id as a suffix.

### Proposal

Three groups, in order of value for risk. Each is independent.

| Group | What | Markup changes | Saving (normalized) | Risk |
|---|---|---|---|---|
| A | Remove verified dead rules (Finding 4) and the 4 rules redundant with global CSS (Finding 3) | None | 3 to 5 KB | Low |
| B | Move identical rules to shared global files: `css/merge-pull-request.css` (27 rules from the two merge modals, all `::deep`), `css/branch-picker.css` (26 rules from `BranchModal`, `SwitchBranchModal`, `PrepareWorkspaceModal`), `css/repository-picker-grid.css` (18 rules from `AddFilesModal`, `WorkspaceRepositoriesModal`), `css/page-header.css` (page header and branch menu rules) | None. Each moved selector is prefixed with an existing root class of the component, which keeps the scoped specificity | About 10 KB | Medium. Rule order changes: global files load before the scoped bundle |
| C | Merge the grid-table and subtitle patterns (Finding 2) into `css/grid-table.css` using selector lists | None | About 4 KB | Medium |

Not proposed:

- Splitting `WorkspaceRepositories.razor.css` (34 KB). 95 percent of it is unique, and splitting changes neither the bundle size nor the number of regex calls. 168 of its selectors use `::deep`, so it could move to a global file as a whole, but that is a 34 KB move with a real regression risk for no size gain.
- Minifying the scoped sources. Comments and whitespace are a third of the source, but brotli already removes that cost on the wire.
- Rewriting the 23 high-risk rules. They stay scoped.

Expected result of A, B and C together: scoped source from about 240 KB to about 210 to 215 KB, bundle from 263 KB to about 235 KB, brotli from 35.7 KB to about 32 KB, regex calls from 2,976 to about 2,550.

