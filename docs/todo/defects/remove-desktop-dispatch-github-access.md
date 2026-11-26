# Remove the GitHub access left over from the Desktop notification

**Status:** owner action in GitHub settings. No code change.

## Why

GrayMoon had a workflow, `notify-desktop.yml`, that started a Desktop build with `repository_dispatch`. The workflow and the Desktop trigger are deleted, because a stable release is now two separate steps: publish GrayMoon, then publish Desktop. The credentials and the access that only the dispatch needed are still configured. Unused write access should not stay.

## Before you start

- [ ] The commit that deletes `notify-desktop.yml` is merged into GrayMoon `main`.
- [ ] The commit that removes the `repository_dispatch` trigger from Desktop's `build.yml` is merged into GrayMoon.Desktop `main`.

A workflow runs from the file in the commit that started it. A tag on an older commit, or a rerun of an old `Notify GrayMoon.Desktop` run, still runs the old workflow and fails once the access is gone. That failure publishes nothing and blocks nothing.

## Do not delete the GitHub App

The header of the deleted workflow said to use **the same GitHub App** for GrayMoon's `DESKTOP_APP_*` secrets and Desktop's `GRAYMOON_APP_*` secrets. Desktop's `build.yml`, `release.yml` and `cleanup-branch-releases.yml` still use that app. Deleting or uninstalling it stops Desktop builds, releases and release cleanup.

Check which setup you really have: GitHub > Settings > Developer settings > GitHub Apps. The secret values cannot be read back, so go by the apps listed there and the repositories each one is installed on.

## 1. Remove two GrayMoon secrets

`Jandini/GrayMoon` > Settings > Secrets and variables > Actions > Repository secrets.

- [ ] Delete `DESKTOP_APP_ID`
- [ ] Delete `DESKTOP_APP_PRIVATE_KEY`

Keep `DOCKERHUB_USERNAME` and `DOCKERHUB_PASSWORD`. `docker-build.yml` uses them.

## 2. Remove the app's access to GrayMoon.Desktop

GitHub > Settings > Applications > Installed GitHub Apps > the app > Configure > Repository access.

- [ ] Remove `GrayMoon.Desktop` from the selected repositories. If the app is set to "All repositories", change it to "Only select repositories" and select `GrayMoon` and `GrayMoon.Release`. First check that nothing else relies on the app reaching your other repositories.

No remaining workflow in GrayMoon or GrayMoon.Desktop asks for an app token for GrayMoon.Desktop. The `backup-stable-binaries` job writes to GrayMoon.Desktop with the built-in `github.token`, not with the app. This was checked in those two repositories only, so check any other use of the app you know of.

Keep the app installed on:

| Repository | Used for |
|---|---|
| `GrayMoon` | Check out the bundled commit; find, and when needed start, the `Test` run |
| `GrayMoon.Release` | Publish releases and remove old prereleases |

## 3. Leave the app's permissions as they are

An app's permissions apply to every repository it is installed on; they cannot differ per repository. The remaining uses still need:

| Permission | Level | Needed by |
|---|---|---|
| Contents | Read and write | Write: `GrayMoon.Release`. Read: `GrayMoon` |
| Actions | Read and write | `GrayMoon`: the test gate |

Removing `GrayMoon.Desktop` from the installation (step 2) is what removes the write access to it.

## 4. Private keys

- [ ] Do **not** revoke a private key unless you know Desktop's `GRAYMOON_APP_PRIVATE_KEY` uses a different one. GitHub does not show which secret holds which key.

If you cannot tell and want to retire the old key: generate a new key, update `GRAYMOON_APP_PRIVATE_KEY` in GrayMoon.Desktop, run step 5, then revoke the older keys.

## If there is a separate app for the dispatch

Only when step 1's secrets belong to a second app that no `GRAYMOON_APP_*` secret and nothing else uses: delete that app instead of doing steps 2 and 4 on it. Leave the app Desktop uses with access to `GrayMoon` and `GrayMoon.Release`.

## 5. Check

- [ ] GrayMoon.Desktop > Actions > Build > Run workflow from `main`, with GrayMoon source `main`. It must pass. This checks the app's access to `GrayMoon`; it publishes nothing.
- [ ] At the next release, the Desktop `Release` run must pass, including publishing to `GrayMoon.Release` and `Remove main channel releases`. The Build run above does not check this access.

If a token step fails after step 2, add `GrayMoon.Desktop` back to the installation and record here which step needed it.

When everything is checked, delete this note and remove its rows from `docs/todo/README.md` and `docs/todo/outstanding-defects-fix-plan.md`.
