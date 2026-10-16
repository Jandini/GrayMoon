# Worker install race: "The type initializer for 'LibGit2Sharp.Core.NativeMethods' threw an exception"

Investigation date: 2026-10-17. Status: root cause identified, fix implemented in the same change (section 6).

## 1. Symptom

After clicking Remove Feature (`feature-push-notification`), every repository failed with
`The type initializer for 'LibGit2Sharp.Core.NativeMethods' threw an exception.` Remove Feature finished with
`Outcome=Failed ... Workspace repository worktree kept until all repository worktrees are removed.`

## 2. What the logs show

Sources: `%LOCALAPPDATA%\GrayMoon\Logs\desktop-20261017.log` (App + Desktop) and
`C:\ProgramData\GrayMoon\logs\graymoon-worker-20261017.log` (Worker).

The App log only carries the outer message. The Worker log carries the real inner exception:

```
System.TypeInitializationException: The type initializer for 'LibGit2Sharp.Core.NativeMethods' threw an exception.
 ---> System.DllNotFoundException: Unable to load DLL 'git2-5853918' or one of its dependencies: The specified module could not be found. (0x8007007E)
```

So this is not a git problem and not a repository problem. The Worker process cannot find libgit2's native library.

Facts established:

- The first failure is `SyncRepository` at 04:34:28, not Remove Feature. Remove Feature was only the next libgit2 call (`RemoveGitWorktree` at 04:34:30).
- No failure exists in any earlier Worker log (2026-10-11 to 2026-10-16). The previous install worked.
- The installed Worker folder `C:\Program Files\GrayMoon` has 56 files. The zip it was installed from has 59 entries. The three missing files are exactly the first three entries of the archive:
  `appsettings.json`, `git2-5853918.dll`, `graymoon-worker.deps.json`.
- The zip itself (`...\GrayMoon.Desktop\current\app\worker\graymoon-worker-windows.zip`) is intact and contains `git2-5853918.dll`.

## 3. Root cause

Two Worker installs ran at the same time against the same folder.

Timeline (2026-10-17, local time):

| Time | Event |
|---|---|
| 04:33:30 | Desktop restarted with a different branch's App (`push-notification.70` -> `main.71`) |
| 04:33:37 | Desktop: "Worker upgrade required at startup" |
| 04:33:38 | App sends `SelfUpdate`; Worker starts a hidden `irm .../api/worker/install \| iex` (install A) |
| 04:33:40 | Install A stops the service ("Application is shutting down") |
| 04:33:46 | User clicks Install Worker in the UI; Desktop starts an elevated visible install of the same script (install B) |
| 04:33:51 | Service is running again on a partially populated folder |
| 04:34:28 | First `git_libgit2_init` -> `DllNotFoundException` |

Both installs run the same script, `install-worker.ps1`, which does, unguarded:

1. stop service, 2. delete everything in `C:\Program Files\GrayMoon`, 3. download the zip to a fixed path
`%TEMP%\graymoon-worker-windows-install.zip` (the service runs as `.\matt`, so both runs share that path),
4. `Expand-Archive -Force`, 5. delete the zip, 6. `graymoon-worker.exe install`.

When one run's delete step (2) or zip delete/overwrite (3, 5) lands in the middle of the other run's extraction (4),
files already written are removed while later ones are still written. Expand-Archive extracts in archive order, so the result is
"the first N entries are gone, the rest are present" - which is exactly what is on disk (N = 3).

Evidence level: the missing-entries pattern, the overlapping timestamps and the shared script/zip path are all
confirmed. The elevated install B logged no result line (neither success nor failure), so its exact
interleaving was not directly observed. No other mechanism (antivirus quarantine, a bad zip, a disk problem) matches the evidence:
Defender logged no detections, and the zip is complete.

### Why it is "every repository"

A static type initializer that throws is cached by the .NET runtime for the life of the process. The first LibGit2Sharp call that touches
`NativeMethods` fails, and every later call from any thread rethrows the same `TypeInitializationException`. Sync, status,
worktree removal and gitignore checks all go through LibGit2Sharp, so all repositories fail at once.

### Why "I've been clicking a lot" matters

It is a real trigger, but not a race between clicks inside the Worker. It is two installers started by two different paths: the automatic
version-mismatch self-update and the manual Install Worker button. Switching branches changes the App version, which makes the
Worker "need an update" immediately, which is when a click on Install tends to happen.

## 4. Did Remove Feature cause damage?

No. Remove Feature failed before changing any worktree. Verified: `git worktree list` in `GrayMoon.Desktop` still lists
`...\features\feature-push-notification\GrayMoon.Desktop` on branch `feature-push-notification` (commit `daf5569`), and the branch exists
locally and on `origin`. The one commit that is not in the default branch is intact. The CodeGraph index for that Feature root was removed
(`UninitCodeGraph` runs first by design); it can be rebuilt.

## 5. Avoidable? Recoverable?

- Avoidable: yes. The install script needs mutual exclusion and a post-extract completeness check. Neither existed.
- Recoverable: yes, with no data loss. The Worker folder is only program files. Run one Worker install and let it finish
  (do not click Install or switch branches while it runs). The service restart is required: restoring the DLL under a
  running Worker is not enough, because the failed type initializer is cached in that process. Then retry Remove Feature.

## 6. Fix

In `src/GrayMoon.App/Resources/install-worker.ps1`:

1. A machine-wide named mutex (`Global\GrayMoonWorkerInstall`) is held from before the service is stopped until the script finishes. A second
   install waits for the first, then performs its own full clean install. It is released on every exit path and an abandoned mutex counts as acquired.
2. The downloaded zip gets a unique name per run, so two runs never share or delete each other's archive.
3. After extraction every archive entry must exist on disk; otherwise the script fails before `graymoon-worker.exe install` and reports the missing files.

Tests in `GrayMoon.App.Tests` pin the three guards in the template.

Not done (optional follow-ups): App/Desktop single-flight (do not send `SelfUpdate` while the Worker is already "installing", disable the Install button);
a Worker startup probe that reports "install is damaged, reinstall" once instead of failing per repository.
