# Desktop release: signing secrets are still repository secrets, and the workflow has not been run

Status: workflow text is fixed; the repository settings step and both verification runs are open. This concerns the `GrayMoon.Desktop` repository; the note lives here because Desktop has no `docs/todo`.

## Symptom

Until the settings step is done, a `workflow_dispatch` of `release.yml` from any branch can read the repository-level `ESIGNER_*` signing secrets.

## Cause

`GrayMoon.Desktop/.github/workflows/release.yml` now requires the `production` environment for every job that signs, through a conditional `environment:` expression that yields `production` or an empty string. The secrets themselves were not moved: that is a GitHub settings change only the repository owner can make. The conditional expression has never run, so it is not known whether GitHub accepts an empty `environment` for the branch and `main` channels.

## Fix plan

1. In the `GrayMoon.Desktop` repository settings, add the `ESIGNER_*` secrets to the `production` environment.
2. Delete the same secrets at repository level.
3. Dispatch `release.yml` from a feature branch. Expected: the build passes and no signing secret is read.
4. Push a stable tag (or re-run one). Expected: the `production` environment approval is requested, the build is signed, and the bundled App is the tag in `build/graymoon-app-version.txt`.
5. If step 3 fails on the empty `environment`, split the signing job from the unsigned build job instead of using the conditional expression.

## Tests

The two workflow runs are the test. Record their run links in the pull request that closes this note.

## Rule this falls under

`GrayMoon.Desktop/CLAUDE.md`, "Release pipeline".
