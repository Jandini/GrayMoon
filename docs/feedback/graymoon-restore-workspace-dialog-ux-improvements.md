# GrayMoon - Restore Workspace Dialog UX Improvements

Date: 2026-10-08  
Scope: **Restore Workspace dialog UX and restore flow**  
Reference implementation pattern: **Add Workspace -> Workspace repository selector**

## Objective

The current Restore Workspace dialog should be redesigned so it feels like an existing GrayMoon workflow rather than a separate one-off form.

The most important UX requirement is:

> **Repository lookup and selection in Restore Workspace must use the same searchable repository picker pattern already used by the Workspace repository field in Add Workspace.**

Do not keep the current two-control pattern:

```text
Search repositories
[________________]

[ Select a repository v ]
```

That UX is clumsy, visually inconsistent with Add Workspace, and makes the user search in one control and select in another.

Instead, reuse the existing Add Workspace repository picker interaction.

---

# 1. Exact repository picker pattern to reuse

The existing pattern lives in:

```text
src/GrayMoon.App/Components/Modals/WorkspaceModal.razor
```

under:

```text
Workspace repository (optional)
```

It already provides the desired behavior:

- one input control
- selected repository shown directly in the input
- focus/click opens the repository list
- typing immediately filters the open list
- keyboard navigation
- Arrow Up / Arrow Down
- Enter selects
- Escape closes
- highlighted row
- active/current selection
- `No matching repositories` state
- dropdown directly attached to the input
- `org/repository` display names
- no separate search box and `<select>`

Restore Workspace must use this **same interaction model and visual language**.

Do not build a second repository-picker UX.

---

# 2. Prefer extracting a shared component

The current Add Workspace picker logic is embedded directly in `WorkspaceModal.razor`.

Restore Workspace now needs the same control.

This is a good point to extract the picker into a small shared component rather than copy/paste the entire implementation.

Suggested direction:

```text
Components/Shared/RepositoryPicker.razor
```

or a more specific name such as:

```text
WorkspaceRepositoryPicker.razor
```

The component should remain simple.

Possible API:

```csharp
[Parameter] public int? SelectedRepositoryId { get; set; }
[Parameter] public EventCallback<int?> SelectedRepositoryIdChanged { get; set; }
[Parameter] public IReadOnlyList<RepositoryPickerChoice> Choices { get; set; }
[Parameter] public string Placeholder { get; set; } = "Select a repository";
[Parameter] public string SearchPlaceholder { get; set; } = "Search repositories";
[Parameter] public bool AllowNone { get; set; }
[Parameter] public string NoneLabel { get; set; } = "None";
[Parameter] public bool Disabled { get; set; }
```

Choice model:

```csharp
public sealed record RepositoryPickerChoice(
    int RepositoryId,
    string DisplayName);
```

Do not over-engineer this into a generic combobox framework.

The purpose is only to reuse GrayMoon's existing repository selector behavior.

---

# 3. Add Workspace must not regress

The extracted component must preserve the exact Add Workspace behavior:

- `None` remains available there
- current Workspace repository remains selected correctly
- disabled state with Features remains unchanged
- existing keyboard behavior stays unchanged
- existing filtering stays case-insensitive
- existing styling stays visually identical

This is a refactor for reuse, not a redesign of Add Workspace.

Add characterization tests around the existing picker behavior before extracting it if needed.

---

# 4. Restore Workspace picker behavior

In Restore Workspace, the same picker is used with different configuration.

Label:

```text
Workspace repository
```

When nothing is selected, input displays:

```text
Select a repository
```

When focused/clicked, the input becomes a search field:

```text
Search repositories
```

Typing filters the repository list immediately.

Example choices:

```text
Jandini/GrayMoon.Workspace
Jandini/GrayMoon.Release
example/Platform.Workspace
```

Unlike Add Workspace:

```text
AllowNone = false
```

There should be no `None` row because Restore requires a Workspace repository.

---

# 5. Repository identity

Use:

```text
org/repository
```

as the normal display value.

If the same `org/repository` exists through multiple connectors, disambiguate only then:

```text
Jandini/GrayMoon.Workspace (Personal GitHub)
Jandini/GrayMoon.Workspace (Work GitHub)
```

Do not always clutter the picker with connector names.

Repository ordering should remain case-insensitive and stable.

---

# 6. Large repository catalogs

The picker must remain comfortable when the user has hundreds of imported repositories.

Requirements:

- filtering must be local and immediate when choices are already loaded
- opening the picker must not make a remote GitHub request
- keystrokes must not query the database one by one
- repository choices should be loaded once when the dialog opens
- filtering should happen in memory
- use the same bounded dropdown/list styling as Add Workspace
- the dropdown must scroll independently when results exceed its visible height

If the existing Add Workspace picker already behaves correctly here, preserve that exact implementation.

---

# 7. Empty state

If there are no imported GitHub repositories, do not render a useless empty picker.

Show:

```text
No GitHub repositories are available.

Import the Workspace repository through Connectors first, then return here.
```

Optional secondary action:

```text
Open Connectors
```

Restore stays disabled.

---

# 8. Preflight starts immediately after selection

Selecting a repository should immediately begin validating whether it is actually a GrayMoon Workspace repository.

Show:

```text
Checking Workspace definition...
```

directly below the picker.

Do not require another button.

The preflight should validate `.graymoon.json` without creating a Workspace.

---

# 9. Invalid repository state

If the selected repository does not contain a valid Workspace definition, show the failure directly below the picker.

Examples:

```text
This repository does not contain .graymoon.json.
```

```text
The Workspace definition is invalid.
```

```text
This Workspace definition was created by a newer GrayMoon version.
Update GrayMoon before restoring it.
```

Keep the selected repository visible in the picker.

The user can click the same picker, type another name, and immediately choose another repository.

Do not reset the entire dialog.

Restore stays disabled.

---

# 10. Valid repository preview

Once preflight succeeds, show a compact Workspace preview below the picker.

Example:

```text
GrayMoon Workspace

Profile
.NET Dependency

Versioning
GitVersion

CI
GitHub Actions

Repositories
24

Connectors
1 configured locally
0 missing
```

Do not show all profile values in one unstructured text block.

Use a small, clean summary matching GrayMoon's existing modal styling.

---

# 11. Missing repositories and connectors

A valid Workspace manifest may refer to repositories/connectors not yet configured on the current computer.

This should not block Restore.

Example:

```text
2 repositories are not currently available in GrayMoon.
1 connector is not configured on this computer.

The Workspace can still be restored.
```

List unresolved entries underneath in a compact scrollable list.

This is important for cross-machine Workspace portability.

Block only when the Workspace definition itself is invalid.

---

# 12. Workspace name

After repository selection, default the Workspace name from the repository name.

Example:

```text
Jandini/GrayMoon.Workspace
```

becomes:

```text
GrayMoon.Workspace
```

Preserve the current good behavior:

> The generated name follows the selected repository only until the user manually edits it.

Once the user types a custom name, further preflight updates must never overwrite it.

Inline validation:

- name required
- invalid filesystem characters
- `.` / `..`
- duplicate Workspace name

---

# 13. Workspace location

Show the resolved folder below the name:

```text
Location
C:\...\Workspaces\GrayMoon.Workspace
```

This is informational and read-only.

Do not make the user guess where Restore will clone the Workspace repository.

---

# 14. Folder validation

The destination folder must be validated live.

## Does not exist

Restore allowed.

## Exists and is empty

Show informational callout:

```text
The folder already exists and is empty. GrayMoon will use it.
```

Restore allowed.

## Exists and is not empty

Show error:

```text
This folder already contains files.
Choose another Workspace name or move the existing files.
```

Restore disabled.

Do not offer Force.

---

# 15. Worker state

If Worker is unavailable:

```text
Worker is not available.
Start the GrayMoon Worker to restore a Workspace.
```

If App/Worker versions differ:

```text
Update the GrayMoon Worker before restoring this Workspace.

App:    x.y.z
Worker: x.y.z
```

Action:

```text
Update Worker
```

Do not use the old feature-capability wording:

```text
The connected Worker does not support Workspace repositories.
```

The mismatch is global, not a Workspace feature-specific problem.

---

# 16. Recommended dialog layout

The dialog should look approximately like:

```text
+------------------------------------------------------+
| Restore Workspace                                  X |
+------------------------------------------------------+
| Restore a GrayMoon Workspace from a repository       |
| containing .graymoon.json.                           |
|                                                      |
| Workspace repository                                 |
| [ Jandini/GrayMoon.Workspace                    v ]   |
|   +----------------------------------------------+   |
|   | Search repositories...                      |   |
|   | Jandini/GrayMoon.Workspace                  |   |
|   | Jandini/GrayMoon.Release                    |   |
|   | example/Platform.Workspace                  |   |
|   +----------------------------------------------+   |
|                                                      |
| Checking Workspace definition...                     |
|                                                      |
| GrayMoon Workspace                                   |
| Profile        .NET Dependency                       |
| Versioning     GitVersion                            |
| CI             GitHub Actions                        |
| Repositories   24                                    |
|                                                      |
| Workspace name                                       |
| [ GrayMoon.Workspace                             ]    |
| Location C:\...\Workspaces\GrayMoon.Workspace        |
|                                                      |
+------------------------------------------------------+
|                              Cancel     Restore       |
+------------------------------------------------------+
```

The key point is that repository selection is **one integrated searchable picker**, exactly like Add Workspace.

---

# 17. Remove the current Restore selector UX

The following current Restore Workspace pattern should be removed:

```razor
<input
    placeholder="Search repositories"
    ... />

<select ...>
    <option>Select a repository</option>
    ...
</select>
```

There must not be a search field followed by a separate native `<select>`.

This is specifically part of the requested UX fix.

---

# 18. Shared picker behavior tests

Add tests for the extracted/shared repository picker logic:

- opens with all choices
- case-insensitive filtering
- selected display name shown while closed
- focus opens picker
- Arrow Down moves highlight
- Arrow Up moves highlight
- Enter selects highlighted repository
- Escape closes without changing selection
- filter reset behavior
- no matches state
- Add Workspace supports `None`
- Restore Workspace does not support `None`
- duplicate repository identity can be disambiguated with connector text

Do not duplicate these tests separately for both dialogs if the logic is shared.

---

# 19. Restore-specific tests

Add tests for:

- selecting repository starts preflight
- stale preflight result cannot overwrite a newer selection
- invalid repository keeps picker usable
- valid repository shows preview
- missing Source repositories do not disable Restore
- missing connector does not disable Restore
- missing/invalid manifest disables Restore
- manually edited Workspace name survives repository/preflight changes
- existing empty folder permits Restore
- non-empty folder disables Restore
- Worker unavailable disables Restore
- Worker version mismatch disables Restore

---

# 20. Acceptance criteria

The Restore Workspace UX is complete when:

- repository lookup uses the same picker interaction as Add Workspace
- there is no separate Search box + `<select>`
- the picker supports keyboard navigation
- the picker displays `org/repository`
- repository validation starts immediately after selection
- valid manifests show a Workspace preview
- invalid manifests block Restore before any mutation
- missing repositories/connectors are clearly shown but do not block a valid restore
- Workspace name is automatically suggested but respects manual edits
- destination path is visible
- non-empty destination is blocked
- Worker problems are shown clearly
- Restore only enables when the entire dialog is ready
- Add Workspace picker behavior is unchanged after sharing/extracting the component

---

# 21. Implementation instruction for AI

Before implementing:

1. Read the current `feedback` branch.
2. Inspect:
   - `WorkspaceModal.razor`
   - its `workspace-repository-picker` CSS
   - `RestoreWorkspaceModal.razor`
   - `RestoreWorkspaceModal.razor.cs`
   - existing picker-related tests
3. Treat the Add Workspace picker as the UX specification.
4. Prefer extracting/reusing that control rather than reproducing it in Restore Workspace.
5. Produce a short code-review/implementation plan before changing files.
6. Keep changes focused.
7. Add tests preserving Add Workspace behavior before or during extraction.
8. Update the living feedback implementation document after implementation.

Do not redesign unrelated Workspace modal controls.  
Do not create a generic UI framework.  
Do not replace the established GrayMoon picker with a third-party component.
