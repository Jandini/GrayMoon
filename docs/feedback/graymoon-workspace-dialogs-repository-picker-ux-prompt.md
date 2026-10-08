# GrayMoon - Add Workspace + Restore Workspace UX Polish Prompt

Date: 2026-10-08  
Target: `feedback` branch  
Primary repo: `Jandini/GrayMoon`

## Goal

Polish the **Add Workspace** and **Restore Workspace** dialogs so they use one consistent repository picker experience and feel intentionally designed.

This task combines all feedback from the recent Workspace repository / Restore Workspace review.

The implementation must be focused, consistent with existing GrayMoon UI patterns, and must not introduce a parallel repository-picker implementation.

## Shared repository picker

The repository selector in **Restore Workspace** must use the same interaction pattern as the **Workspace repository** selector in **Add Workspace**.

The desired interaction is already represented by the current `RepositoryPicker` pattern:

- one integrated input
- closed state shows current selection
- focus/click opens the result list
- while open, the same input becomes the search field
- local case-insensitive filtering
- Arrow Up / Arrow Down navigation
- Enter selects
- Escape closes
- active/current selection is visually distinct
- highlighted keyboard row is visually distinct
- `No matching repositories` empty state
- no separate search box followed by a native `<select>`

Do **not** reintroduce this pattern:

```text
Search repositories
[________________]

[ Select a repository ▼ ]
```

There should be one shared picker component used by both Add Workspace and Restore Workspace.

## Shared picker component ownership

Prefer one shared component, for example:

```text
Components/Shared/RepositoryPicker.razor
```

with shared styling owned by the component or a shared stylesheet.

Do not keep picker styling scoped only to `WorkspaceModal.razor.css` if the Restore dialog also uses the same picker.

The picker should be visually and behaviorally identical in both dialogs.

## Picker colors

The current repository dropdown coloring needs improvement.

Requirements:

- use GrayMoon's neutral modal/dropdown palette
- avoid browser-native bright blue selection
- avoid overly saturated Bootstrap primary colors for hover/highlight
- selected row and keyboard-highlighted row should be distinguishable but subtle
- use neutral gray background/surface tones
- text must remain high-contrast and readable
- muted text should remain muted, not washed out
- borders should match GrayMoon form controls
- hover should be subtle
- active selection should not look like a destructive or primary action

The dropdown should feel like part of GrayMoon, not like a browser-native listbox.

If dark/light appearance support exists in the app, use existing variables/tokens rather than hard-coded one-off colors where possible.

## Dropdown must not expand modal height

This requirement applies to **both Add Workspace and Restore Workspace**.

Opening the repository picker must **not push the fields below it downward** and must **not increase the modal height**.

The result list must behave as a real dropdown/popover:

- picker container: positioned relative
- result list: positioned absolute
- overlay content below
- own `max-height`
- own vertical scrollbar
- suitable z-index above modal content
- width aligned to the input
- no reflow of the form when opened

Conceptually:

```text
[ Workspace repository                         ]
┌───────────────────────────────────────────────┐
│ Search repositories...                        │
│ Jandini/GrayMoon                              │
│ Jandini/GrayMoon.Desktop                      │
│ Jandini/GrayMoon.Release                      │
│ ...                                           │
└───────────────────────────────────────────────┘
```

The dropdown overlays the form below it.

The modal dimensions must remain unchanged.

If there is not enough room below the input, either constrain the list height to the visible modal viewport, or flip/open upward if there is already a simple existing pattern for this. Do not solve this by making the whole modal body taller.

## Remove `(optional)` from Add Workspace label

Current:

```text
Workspace repository (optional)
```

Change to:

```text
Workspace repository
```

The optional nature is already communicated by the `None` selection.

Do not put `(optional)` in the label.

If helper text is needed, keep it subtle and below the control.

Restore Workspace is required, so it must never use optional wording.

## Add Fetch button attached to repository picker

Add a **Fetch** button directly attached to the repository picker in both dialogs.

Desired structure:

```text
[ repository search / selection field      ][ Fetch ]
```

Use a Bootstrap input-group style or equivalent GrayMoon-aligned attached control.

The Fetch button should:

- be visually attached to the picker
- not look like a separate unrelated action
- trigger repository catalog refresh/fetch
- use the standard GrayMoon page/modal loading overlay
- show repository-fetch progress through the existing loading overlay/terminal mechanism
- refresh the available picker choices after completion
- preserve the currently selected repository if it still exists after refresh
- if the selected repository was renamed, use GrayMoon's existing repository identity/rename reconciliation so the correct repository remains selected where possible
- if the selected repository disappears, clear selection and show a sensible state
- not put an inline spinner inside the Fetch button if the standard loading overlay is already active

Do not invent a new fetch implementation. Reuse the existing repository fetch/import-refresh path already used elsewhere in GrayMoon.

## Restore Workspace dialog must be wider

The Restore Workspace dialog is currently too narrow for the repository picker, attached Fetch button, profile preview, missing repository/connector summaries, filesystem path, and error/warning callouts.

Make Restore Workspace wider.

Prefer the same Bootstrap large-modal pattern already used by Add Workspace:

```html
modal-dialog modal-lg
```

or an equivalent width if current modal CSS makes `modal-lg` insufficient.

The dialog should remain responsive and bounded by the viewport. Do not make it full-screen.

## Restore Workspace preview frame must be gray

The preview currently shows information similar to:

```text
GrayMoon Workspace

Profile
Basic

Versioning
GitVersion

CI
GitHub Actions

Repositories
2

Connectors
1 configured locally
0 missing
```

The enclosing preview panel/frame must use a **gray neutral background**.

It should look like an informational summary card, not a white fieldset.

Desired visual direction:

- soft light-gray background
- subtle gray border
- small border radius consistent with GrayMoon
- no strong accent color
- no blue/green success appearance
- compact padding
- labels muted
- values normal/darker weight

Example structure:

```text
┌─────────────────────────────────────────────┐
│ GrayMoon Workspace                          │
│                                             │
│ Profile          Basic                      │
│ Versioning       GitVersion                 │
│ CI               GitHub Actions             │
│ Repositories     2                          │
│ Connectors       1 configured locally       │
│                  0 missing                  │
└─────────────────────────────────────────────┘
```

This applies specifically to the **Restore Workspace** preview card.

## Restore Workspace error red is too aggressive

The current red error callout is too visually strong.

Do not remove error semantics, but soften the presentation.

Desired direction:

- muted/desaturated red
- lighter red/pink background
- darker readable error text
- subtle border
- retain `role="alert"`
- still clearly an error, but not visually alarming for normal validation such as invalid manifest, non-empty folder, repository cannot be restored, or Worker unavailable

Use a shared GrayMoon error callout style if one already exists that is softer.

If `gm-callout--error` is globally too aggressive, do **not** blindly change every error across the product without review. Prefer either a softer modal-validation variant, or adjust only the relevant dialog styles unless code review confirms the global style should be updated.

## Do not show spinner in Restore button while typing Workspace name

Current behavior can show a spinner inside the **Restore** button while the user types the Workspace name because folder validation is running.

This feels noisy and makes typing look like a long-running action.

Change this behavior.

Requirements:

- typing Workspace name may temporarily disable Restore while folder validation is pending
- do **not** show a spinner inside the Restore button for this lightweight debounce/check
- keep button text simply `Restore`
- if feedback is useful, show subtle inline text near the Workspace location/folder validation area, not inside the primary action button
- only use a spinner/loading state for the actual restore operation, and even then the page-level loading overlay should remain the primary progress mechanism

The button should not flicker between `Restore` and `[spinner] Restore` on every keystroke.

## Restore Workspace repository preflight

After a repository is selected, immediately preflight it.

Show subtle inline state:

```text
Checking Workspace definition...
```

This should be near the repository picker/preview area.

Do not use the primary Restore button spinner for this.

The preflight must verify:

- `.graymoon.json` exists
- definition parses
- supported schema/version
- profile values are understood
- repository/connector references can be resolved locally where possible

No Workspace should be created during preflight.

## Invalid repository behavior

If the repository is not a valid Workspace repository:

- keep the selected repository visible
- show a softened error callout below it
- Restore disabled
- picker remains fully usable so the user can immediately choose another repo

Examples:

```text
This repository does not contain .graymoon.json.
```

```text
The Workspace definition is invalid.
```

```text
This Workspace definition was created by a newer GrayMoon version.
```

Do not reset the dialog.

## Valid repository preview

When preflight succeeds, show the gray summary frame.

Include:

- Workspace type
- versioning
- CI provider
- number of repositories
- connector resolution summary

Do not dump raw JSON or raw manifest values. Use user-facing labels.

## Missing repositories/connectors

A valid Workspace definition may reference repositories/connectors that are not configured locally.

This does **not** block restore.

Show a subdued warning:

```text
2 repositories are not currently available in GrayMoon.
1 connector is not configured on this computer.

The Workspace can still be restored.
```

List unresolved items compactly.

Do not use strong red for this. This is warning/informational state, not invalid input.

## Workspace name behavior

Keep existing good behavior:

- default Workspace name from selected repository
- once user manually edits the name, later repository/preflight updates do not overwrite it
- validate duplicate name
- validate filesystem-invalid characters
- validate `.` and `..`

Show resolved destination path beneath the name.

Example:

```text
Location
C:\Users\...\Workspaces\GrayMoon.Workspace
```

## Folder validation

The destination folder behavior must remain clear.

### Missing folder

Restore allowed.

### Existing empty folder

Show subtle informational callout:

```text
The folder already exists and is empty. GrayMoon will use it.
```

Restore allowed.

### Existing non-empty folder

Show softened error callout:

```text
This folder already contains files.
Choose another Workspace name or move the existing files.
```

Restore disabled.

Do not add Force.

## Worker state

If Worker is unavailable:

```text
Worker is not available.
Start the GrayMoon Worker to restore a Workspace.
```

If Worker version differs:

```text
Update the GrayMoon Worker before restoring this Workspace.

App:    x.y.z
Worker: x.y.z
```

Action:

```text
Update Worker
```

Do not use feature-capability wording such as:

```text
The connected Worker does not support Workspace repositories.
```

Worker compatibility is version-based, not feature-capability based.

## Add Workspace specific requirements

Add Workspace must receive the shared picker improvements too:

- shared picker
- improved neutral colors
- overlay dropdown that does not expand modal height
- remove `(optional)` from label
- attached Fetch button
- preserve `None`
- preserve current repository selection
- preserve current Add Workspace behavior otherwise

Do not redesign unrelated fields in Add Workspace.

## Restore Workspace specific requirements

Restore Workspace must additionally receive:

- wider modal
- same shared picker
- attached Fetch button
- preflight
- gray Workspace summary frame
- softer error presentation
- no Restore-button spinner while typing Workspace name
- destination path visibility
- missing repo/connector summary
- clean success / partial success behavior

## Fetch behavior details

The Fetch button must use the established GrayMoon repository-fetch workflow.

Before implementing:

1. Find the existing Fetch Repositories command/path.
2. Reuse its service/orchestration layer.
3. Reuse the standard loading overlay.
4. Reuse progress/log streaming if already available.
5. Refresh picker choices after fetch.

Do not call GitHub directly from the Razor component, duplicate connector refresh logic, create a new background-job mechanism, or create an inline-only loading model if the normal overlay already exists.

Expected UX:

```text
user clicks Fetch
    ↓
standard GrayMoon loading overlay opens
    ↓
repository catalog refresh/fetch runs
    ↓
overlay closes
    ↓
picker choices refresh
    ↓
selection preserved if possible
```

## Shared picker styling requirements

The shared component should own or consume shared styles for:

```text
repository-picker
repository-picker-input
repository-picker-list
repository-picker-option
repository-picker-option.active
repository-picker-option.highlighted
repository-picker-empty
```

Important CSS behavior:

```css
.repository-picker {
    position: relative;
}

.repository-picker-list {
    position: absolute;
    top: calc(100% + ...);
    left: 0;
    right: 0;
    z-index: ...;
    max-height: ...;
    overflow-y: auto;
}
```

Exact values should follow GrayMoon spacing/z-index conventions.

Do not hard-code excessive z-index values without checking the existing modal stack.

## Accessibility

Preserve or improve:

- `role="listbox"`
- `role="option"`
- keyboard operation
- focused/highlighted state
- visible selected state
- Escape closes picker
- Enter selects
- disabled Fetch button where appropriate
- accessible label association

If practical, include `aria-selected`, `aria-expanded`, and `aria-controls`.

Do not break keyboard use while moving the picker into a shared component.

## Tests

### Shared repository picker

Test:

- opens with all choices
- filters case-insensitively
- selected value shown while closed
- Arrow Down
- Arrow Up
- Enter
- Escape
- no-match state
- active selection
- Add Workspace `None`
- Restore Workspace no `None`
- selection preserved after choice list refresh

### Fetch integration

Test:

- Fetch action invokes existing repository refresh path
- loading overlay/job is used
- choices reload afterward
- current selection remains when repository still exists
- disappeared repository clears selection safely
- renamed repository follows existing stable identity behavior where available

### Add Workspace

Test:

- label is `Workspace repository`
- no `(optional)`
- picker behavior unchanged otherwise
- `None` still valid
- dropdown opening does not alter modal layout state

### Restore Workspace

Test:

- wider modal class/presentation
- selected repository starts preflight
- valid preflight shows gray summary panel
- invalid preflight shows softened error state
- unresolved Source repos do not block Restore
- manually edited Workspace name is preserved
- folder check does not cause Restore button spinner
- Restore stays disabled while folder check is pending
- non-empty folder blocks
- Worker mismatch blocks

## Manual UX test checklist

### Add Workspace

1. Open Add Workspace.
2. Confirm label reads `Workspace repository`.
3. Open repository picker.
4. Confirm modal height does not change.
5. Confirm list overlays content below.
6. Confirm colors are neutral GrayMoon colors.
7. Search by typing.
8. Use arrows + Enter.
9. Select None.
10. Click Fetch and confirm normal loading overlay.
11. Confirm picker refreshes afterward.

### Restore Workspace

1. Open Restore Workspace.
2. Confirm dialog is visibly wider than before.
3. Confirm picker matches Add Workspace exactly.
4. Confirm attached Fetch button.
5. Open picker and confirm modal height does not change.
6. Select a valid Workspace repo.
7. Confirm `Checking Workspace definition...`.
8. Confirm gray Workspace summary frame.
9. Confirm missing repos/connectors use warning styling, not red.
10. Type Workspace name quickly.
11. Confirm Restore button does not show a spinner while typing.
12. Confirm destination path updates.
13. Enter a name mapping to a non-empty folder and verify softened error styling.
14. Restore successfully.
15. Confirm normal loading overlay handles long-running restore work.

## Acceptance criteria

This work is complete when:

- Add Workspace and Restore Workspace use the same shared repository picker.
- Picker colors are neutral and consistent with GrayMoon.
- Add Workspace no longer says `(optional)`.
- Picker dropdown overlays form content and never expands modal height.
- Both pickers have an attached Fetch button.
- Fetch uses GrayMoon's standard loading overlay and existing repository-fetch workflow.
- Restore Workspace is wider.
- Restore preview uses a gray neutral frame.
- Restore validation errors use a softer red treatment.
- Typing Workspace name never produces a spinner inside the Restore button.
- Restore preflight stays inline and lightweight.
- Missing local repositories/connectors remain warnings, not hard errors.
- Existing Add Workspace behavior outside the picker is unchanged.
- All keyboard and accessibility behaviors remain intact.

## AI implementation prompt

Review the current `feedback` branch before changing code.

Inspect at minimum:

```text
src/GrayMoon.App/Components/Modals/WorkspaceModal.razor
src/GrayMoon.App/Components/Shared/RepositoryPicker.razor
src/GrayMoon.App/Components/Modals/WorkspaceModal.razor.css
src/GrayMoon.App/Components/Modals/RestoreWorkspaceModal.razor
src/GrayMoon.App/Components/Modals/RestoreWorkspaceModal.razor.cs
```

Also locate:

- existing repository Fetch workflow
- loading overlay/background job pattern
- picker-related tests
- modal/callout styling
- repository rename/stable-identity reconciliation

Then produce a concise implementation plan before editing.

Important product requirements are fixed:

1. Add and Restore use the same repository picker.
2. The dropdown must overlay and must not increase modal height.
3. Add Workspace label must not contain `(optional)`.
4. Both pickers have an attached Fetch button using the standard loading overlay.
5. Restore Workspace must be wider.
6. Restore Workspace preview frame must be gray.
7. Restore Workspace error red must be softened.
8. Typing Workspace name must not show a spinner in the Restore button.
9. Preserve existing keyboard picker behavior.
10. Keep changes focused and do not redesign unrelated Workspace fields.

Add regression tests and update the living feedback implementation document with:

- files changed
- behavior changed
- tests added
- test counts
- manual test checklist
- any deviations discovered during implementation
