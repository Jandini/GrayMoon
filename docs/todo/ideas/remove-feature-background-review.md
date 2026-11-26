# Remove Feature: run the review by itself

Status: proposal, nothing built. Written 2026-11-27. The open questions at the end are not decided.

## Problem

The Remove Feature dialog opens from stored facts and can be confirmed at once. The live check of the repositories is a second button, "Review details". That was right when the check was slow. It is not slow any more:

- The Worker runs `InspectWorktree` in its read pool, all repositories in parallel.
- In the logs of 2026-11-26 and 2026-11-27 one repository took 0.3 to 0.9 s, and a Feature with 5 repositories was checked in about 1 s (the slowest 1.3 s).

So the user pays an extra click for something that would be on screen before they have read the dialog. Without that click they never learn that a repository has uncommitted changes, unpushed commits, a lock or an unmerged branch until the result lists it as kept.

## Idea

The dialog starts the check itself when it opens.

1. The dialog opens at once, as today: the stored summary, the safety text and a red **Remove Feature** button. No waiting.
2. The check starts in the background. The footer shows "Checking repositories..." on the left, with the progress text the check already reports.
3. **Remove Feature clicked before the check ends:** the check is cancelled and the removal runs without a reviewed plan, exactly as it does today without "Review details". Nothing is forced: a folder with changes, a lock or an unfinished merge is kept, an unmerged branch is kept, both are listed in the result.
4. **The check ends first:** the dialog changes to the reviewed view it shows today after "Review details": the per-repository findings and the choices each risk needs.
5. The "Review details" button and the hint line that explains it (`RemoveFeatureQuickDialog.ReviewHint`) are removed.

## What stays the same

- The removal itself. `WorkspaceFeatureOperations.Removal.cs` never inspects on its own, and force, unlock and forced branch delete apply only to the repositories of `RemoveFeatureOptions.AnalyzedPlan` that showed that risk. No destructive permission is widened.
- The check is read-only (`AnalyzeRemoveFeatureAsync`).
- The path where the page already ran the check and passes `InitialPlan` to the dialog.
- Cancel closes the dialog and stops the check.

## Things the design has to settle

These are consequences of the idea, found in the current dialog (`Components/Features/RemoveFeatureModal.razor`):

- **The button changes meaning while the user aims at it.** Before the check, Remove Feature is always enabled and safe. In the reviewed view today it is disabled until every shown consent is ticked (`CanConfirm` / `CanRemove`), and it turns green when nothing is risky. With an automatic check, a button that worked a moment ago can go dead, or change colour, under the cursor. See question 1.
- **The check overwrites the branch choices.** `ApplyPlan` sets "Delete local branches" and "Delete remote branches" from what it found. Today the checkboxes are disabled while the check runs; with an automatic check they would either be disabled for the first second of every dialog, or a tick made early would be overwritten. See question 3.
- **The layout jumps.** The reviewed view is taller than the summary. The dialog should not move the Remove Feature button when it grows (fixed footer, body grows upward or scrolls).
- **A failed check.** Today a failure of "Review details" shows an error in place of the content. As an automatic step it must not take away the safe removal the dialog offered a second before. See question 4.
- **Cost of every opening.** Each opening runs one `InspectWorktree` per repository, and the analysis also reads pull request state, also when the user cancels. On a Workspace with 40 repositories that is 40 Worker commands per opening.

## Code involved

- `src/GrayMoon.App/Components/Features/RemoveFeatureModal.razor` - start the check from `OnParametersSetAsync` after the summary, footer status, cancel on confirm, `CanConfirm`.
- `src/GrayMoon.App/Components/Features/RemoveFeatureQuickDialog.cs` - `ReviewHint` goes, `BranchNote` is reworded.
- `src/GrayMoon.App.Tests/RemoveFeatureSummaryTests.cs` - `Confirming_from_the_summary_removes_without_a_single_inspection` describes today's rule ("no inspection unless asked") and has to be replaced by: confirming before the check ends removes without a plan and stops the check.
- `docs/architecture/05-user-capability-reference.md` and `01-product-model-and-user-workflows.md` (Remove Feature), `CLAUDE.md` ("Worktree Features": "InspectWorktree ... for the Remove dialog's Review details").
- No Worker change, no schema change.

## Open questions

1. **Remove Feature in the reviewed view when a risk is shown and its consent is not ticked.** Today the button is disabled. Options: (a) keep that, so the button can go dead when the check ends; (b) the button stays enabled and removes safely, keeping the risky repositories, and each consent only adds its repositories. With (b) a click means the same before and after the check, which is what makes the automatic switch safe. (b) changes what the reviewed view does today.
2. **Nothing risky found.** Does the dialog stay as it is and only say "Checked: nothing to review", or does it still switch to the per-repository list? And does the button turn green then, as it does today?
3. **The branch checkboxes during the check.** Disabled until the check ends, or enabled with the user's choice kept over what the check would set?
4. **The check fails or the Worker is busy.** Keep the stored summary with a note "Could not check the repositories" and the safe removal available, or show the error as today?
5. **Large Features.** Run the check automatically for every size, or only up to a number of repositories and keep the button above it?
6. **A way to check again.** The reviewed view is a snapshot. Does it need a "Check again" link once the button is gone?
7. **The Feature status panel's "Continue removal" and the selector's remove entry** open the same dialog: same behavior in all of them?
