# NEW FEATURE - Show Processes Blocking Feature Removal

> **Superseded (2026-10-09):** this feature was removed. Remove Feature no longer inspects or ends processes; a folder still in use is marked pending deletion and cleaned up later in the background. See `docs/worktree/GrayMoon-Worktree-Features-Remove-Pending-Cleanup.md`.

## Classification

**NEW FEATURE**

## User observation

Removing a Feature/worktree will often fail because another process still has something open inside the Feature directory.

Typical examples:

- Claude still running in the Feature
- another AI coding tool
- terminal/shell opened in the Feature
- `dotnet` process
- test runner
- language server
- Visual Studio / VS Code
- Explorer or shell extension
- antivirus/indexer temporarily holding a file

A generic Git or filesystem error is not enough. The user needs to know **what is blocking removal**.

## Goal

When Feature removal fails because the worktree directory is in use, GrayMoon should identify the blocking processes and show them directly in the Remove Feature UX.

Example:

```text
GrayMoon could not remove this Feature.

The following processes are using files in the Feature:

Claude
PID 18472
C:\...\claude.exe

dotnet
PID 22140
C:\Program Files\dotnet\dotnet.exe

Close the processes and try again.
```

## Recommended Windows mechanism

Use the native **Windows Restart Manager API**.

Restart Manager is designed to identify applications and services using files/resources that prevent update/replacement/removal operations.

It is a much better fit than depending on external tools such as Sysinternals `handle.exe`.

### Useful Restart Manager APIs

The implementation can be based on:

- `RmStartSession`
- `RmRegisterResources`
- `RmGetList`
- `RmEndSession`

The Worker can register relevant paths/resources and retrieve process information for blockers.

## Architectural location

This belongs in the **Worker**.

Reasons:

- Worker owns local filesystem/Git operations.
- Worker knows the real Feature/worktree path.
- App should not perform machine-level process inspection.
- A Worker abstraction keeps future cross-platform support possible.

Suggested abstraction:

```csharp
public interface IFileLockInspector
{
    Task<IReadOnlyList<BlockingProcessInfo>> GetBlockingProcessesAsync(
        string directory,
        CancellationToken cancellationToken = default);
}
```

Possible model:

```csharp
public sealed record BlockingProcessInfo(
    int ProcessId,
    string? ProcessName,
    string? ExecutablePath,
    string? ServiceName,
    BlockingProcessKind Kind);
```

## Important limitation

There is no single perfect Windows API for “all processes locking this directory.”

Restart Manager is the best first implementation for file/resource usage, but some removal failures may come from other conditions.

A particularly important case is a process whose **current working directory** is inside the Feature worktree.

That may not always appear exactly like a standard open-file lock.

Therefore, implement the feature in layers:

### Layer 1 - Restart Manager blockers

This should be the first implementation.

It will catch the most actionable cases and has strong Windows support.

### Layer 2 - Additional diagnostics

If required later, investigate:

- process current-working-directory detection
- child process tree associated with known AI/terminal processes
- transient scanner/indexer conditions
- retry-after-short-delay behavior

Do not block the first version on solving every possible lock type.

## Recommended Remove Feature flow

Do not run lock inspection before every removal.

Use it only when removal fails with a likely sharing/access/in-use failure.

```text
User clicks Remove Feature
        ↓
normal safe worktree removal
        ↓
success → done
        ↓
failure suggests path is in use
        ↓
Worker runs lock inspection
        ↓
App shows blockers
        ↓
user closes blocker(s)
        ↓
Retry Remove
```

This keeps the successful path fast.

## UX design

The failure UI should become actionable.

### Suggested content

Header:

```text
Feature is still in use
```

Body:

```text
GrayMoon could not remove the Feature because one or more processes are using files in its worktree.
```

Then show a compact process list containing:

- process name
- PID
- executable path when available
- service name when relevant

Actions:

- `Retry`
- `Refresh blockers`
- `Cancel`

### Do not terminate processes automatically in the first version

Avoid an immediate `Kill process` action.

Reasons:

- a process may own unrelated work outside this Feature
- IDEs can have several workspaces open
- AI tools may have unsaved state
- terminals may be shared
- killing system/indexer processes is risky

GrayMoon should first diagnose and explain.

A later explicitly confirmed termination feature can be considered separately if there is real demand.

## Git/worktree integration

The process inspection must not change the safety semantics of Feature removal.

The existing removal path should remain responsible for:

- uncommitted-change validation
- worktree removal
- branch deletion rules
- Feature context cleanup
- parent workspace state

Lock diagnostics should only enrich failure reporting.

## Error classification

Add a clear distinction between:

1. logical Git refusal
2. uncommitted changes
3. merge/worktree safety failure
4. path/file in use
5. access denied
6. unknown IO failure

Only invoke blocker detection for the failure classes where it is meaningful.

Do not run Restart Manager for every arbitrary Git error.

## Worker command

A focused Worker command could be added, for example:

```text
InspectPathLocks
```

Input:

```json
{
  "path": "C:\Users\...\.graymoon\...\feature-name"
}
```

Output should contain:

- success/failure
- list of blocking processes
- whether results may be incomplete
- optional diagnostic text

Avoid returning sensitive process command lines unless there is a clear need. Process name, PID, and executable path are enough for the first version.

## Cross-platform design

Keep the abstraction platform-neutral.

Possible implementations:

```text
IFileLockInspector
    WindowsRestartManagerFileLockInspector
    UnsupportedFileLockInspector
```

On unsupported platforms, GrayMoon should simply fall back to the current failure information.

Do not force a Windows-specific type into application/domain layers.

## Security and privacy

Process inspection is local-only.

Do not:

- transmit process lists to GitHub/connectors
- persist them long term
- include them in telemetry by default
- log full command lines unnecessarily

If process paths are logged for diagnostics, use the existing local logging conventions and consider whether user/profile paths should be minimized.

## Tests

### Unit tests

- lock-inspection result maps correctly to UI model
- no inspection on unrelated Git failures
- inspection triggered on path-in-use/access-sharing failures
- empty blocker list still produces useful fallback message
- Worker failure does not hide the original removal error

### Windows integration test

Create a temporary worktree/directory.

Open a file handle from a helper process.

Run the Restart Manager inspector.

Assert that the helper process PID is returned.

Then close the handle and verify it disappears.

### Remove Feature integration test

Simulate/produce a blocked Feature removal and verify:

- normal removal runs first
- lock inspection runs only after relevant failure
- blocker information reaches the modal
- retry can succeed after blocker exits

## Acceptance criteria

- When Feature removal fails because files are in use, GrayMoon attempts to identify the blocking processes.
- The user can see process name and PID, and executable path when available.
- The original removal error is preserved if diagnostics fail.
- Successful removals incur no lock-inspection overhead.
- GrayMoon does not automatically terminate blocking processes.
- Implementation resides in the Worker behind a platform-neutral abstraction.
- Windows implementation uses Restart Manager rather than an external executable dependency.
