# 05 - General Code, Security, Reliability and UX Review (outside worktree Features)

Date: 2026-10-01
Scope: `GrayMoon` (App, Agent/Worker, Application, Abstractions, Common, tests, CI, Dockerfile) and `GrayMoon.Desktop`, **excluding** the in-depth review of the worktree / Features feature (covered by a separate review). Where a general finding affects the worktree release, it is called out in [Section 0](#0-items-that-affect-the-worktree-feature-release).

Method:

- Read `README.md`, `AGENTS.md`, `CLAUDE.md`, `docs/architecture/*.md`, `GrayMoon.Desktop/docs/*.md`, then the code.
- Built and tested the solution locally (Windows, .NET 10 SDK). Results are in [Section 4](#4-tests-and-tooling).
- Looked at the running GrayMoon Desktop instance (`http://127.0.0.1:8384`) read-only: Repositories grid, Workspaces list, Settings. No actions were triggered.
- Parts of the Desktop, security and UX passes ran in parallel; every High finding below was re-verified by reading the cited lines. One claim from the parallel security pass was corrected (see S6).

Severity scale: **High** = data loss, credential exposure, remote/cross-site control, or a silently wrong result in a core workflow. **Med** = real defect or risk with a workaround or narrower blast radius. **Low** = hygiene, polish, maintainability.

Line references are `path:line` relative to the repository root (`GrayMoon/` or `GrayMoon.Desktop/`).

---

## Executive summary

GrayMoon has an unusually thoughtful core: a strict App/Worker split, persistence-first UI, process-wide operation locks, careful process execution (UTF-8, timeouts, process-tree kill, `GIT_TERMINAL_PROMPT=0`), path-traversal validation for Git Changes, and a mature Desktop release pipeline. The docs (`AGENTS.md`, `CLAUDE.md`) capture hard-won lessons well.

The biggest gaps are not in the multi-repo logic. They are in **trust boundaries** and **failure reporting**:

1. The App has **no authentication at all**, binds `127.0.0.1:8384` (Desktop) or `0.0.0.0:8384` (Docker), and exposes an endpoint that returns **decrypted GitHub/NuGet tokens**. Body-less `POST` endpoints such as `/api/workspaces/{id}/push` are cross-site "simple requests", so any web page the user visits can trigger a push.
2. Token "encryption" falls back to a **key derived from a constant string in the public source**, and the Desktop host never configures a key, so every Desktop install shares it.
3. Several core workflows **report success when they failed**: `dotnet restore` failures and timeouts, schema migrations (bare `catch {}`), Feature remote-branch deletes.
4. Managed git hooks **overwrite users' existing hooks** (including `pre-push`) and ignore `core.hooksPath`.
5. Deleting a connector **cascade-deletes its repositories from every workspace** behind a one-line confirm, and pressing Enter confirms.

### Top 10 findings

| # | Severity | Finding | Section |
|---|---|---|---|
| 1 | High | Unauthenticated `GET /repos/{id}/connector` returns the decrypted PAT | S1 |
| 2 | High | Token encryption key defaults to a public constant; Desktop never sets one | S2 |
| 3 | High | No auth plus CSRF: body-less `POST /api/workspaces/{id}/push`, `/undo-push`, `/sync`, `/restore-packages` can be triggered by any web page; `AllowedHosts: *` also allows DNS rebinding | S3 |
| 4 | High | Managed git hooks overwrite existing `post-commit`/`pre-push`/... hooks and ignore `core.hooksPath` (husky, lefthook) | A1 |
| 5 | High | `dotnet restore` failures and timeouts are reported as success (Worker always returns `Success = true`; App ignores the response; 60s default timeout) | A2 |
| 6 | High | Schema migration framework: `EnsureCreated` plus hand-rolled `ALTER`s inside bare `catch {}`, no transaction, no version stamp, no backup, no test against a real shipped (0.1.0) database | A3 |
| 7 | High | Connector delete cascades to all repositories and workspace links (files, custom dependencies, PR state); the confirm is one line and Enter deletes | U1 |
| 8 | Med | Blazor components open a server-side SignalR **client** to their own hub using the browser-facing URL; this breaks behind reverse proxies or remapped Docker ports, and every event goes to `Clients.All` | A4 |
| 9 | Med | Desktop sends an unconditional, Base64-obfuscated telemetry ping to a third-party domain with no opt-out | D1 |
| 10 | Med | Git argv for branch/tag names is built by string interpolation with no ref-name validation (option injection for names starting with `-`); the PAT travels in argv | S5, S6 |

---

## 0. Items that affect the worktree Feature release

These are general findings, not worktree-design findings. They should be resolved or explicitly accepted before the worktree release ships, because the release adds schema, hooks and remote operations on top of them.

| Item | Severity | Blocker? | Why it matters for the release |
|---|---|---|---|
| **R1. Feature schema migration is silent and all-or-nothing.** `Migrations.Features.cs:14-35` runs the table creates, column adds and backfill inside one `try { ... } catch { }` with the comment "Fresh DB: EnsureCreated already created the Feature tables". Any failure part-way (locked DB, constraint violation during `BackfillSpecialWorkspaceContextsAsync`, disk full) is swallowed, startup continues, and users later get `no such table/column` at runtime with nothing in the log. There is no transaction, no `PRAGMA user_version`, and no pre-upgrade backup. | High | **Yes, recommended blocker** | 0.1.0 is publicly shipped (`README.md:10`), so this is the first real schema upgrade for end users. `Migrations.cs:6-13` still says "Pre-release ... there is no shipped version whose database needs patching", which is no longer true. |
| **R2. No upgrade test from a real 0.1.0 database.** `WorkspaceFeatureContextMigrationTests.cs:13-17` builds the *current* model with `EnsureCreatedAsync()` and then runs the migration. That never exercises the old-schema to new-schema path. | High | **Yes, recommended blocker** | Check a 0.1.0 `graymoon.db` fixture into the test project and assert that startup migrates it and that every `DbSet` can be queried afterwards. |
| **R3. Feature remote-branch delete sends no token.** `WorkspaceFeatureOperations.cs:544-555` sends `DeleteBranch` with `isRemote = true` and no `bearerToken`. Failure is only `LogWarning`ed (`:556-560`). `GitService.cs:1585-1586` lists this as a known gap. | Med | Fix before release | On a fresh machine without a Git Credential Manager cache, "Remove Feature" with "delete remote branches" silently leaves the remote branches behind. |
| **R4. Hooks overwrite and `core.hooksPath` (A1).** Feature attribution depends on the shared hooks in `--git-common-dir`/hooks (`GitService.cs:1289-1356`). Repos using husky/lefthook (`core.hooksPath`) never run them, and repos with their own hooks lose them. | High | Fix or document before release | With Features, hook-driven state matters more, so silent non-attribution becomes more confusing. |
| **R5. The REST API is not Feature-aware.** Every operation endpoint resolves `SpecialContextAsync` (`WorkspaceOperationsEndpoints.cs:11-15`, used at `:89,115,145,182,208,223,240,253,304,322,341`). | Low | Document | API and automation users (and the planned MCP surface) cannot target a Feature. Document this or add an optional `contextId`. |
| **R6. Security gate (S1-S3).** Not worktree-specific, but the worktree release is the next public build. | High | Strongly recommended to ship with or before it | Fixing S1 and S2 (token endpoint, default key) is small. S3 (CSRF) needs at least an Origin/header check. |
| **R7. Build warning in worktree code.** `Services/Git/WorkspaceGitService.Context.cs:38` raises CS8619 (nullability mismatch `Dictionary<int,string>` to `IReadOnlyDictionary<int,string?>`). It is the only warning in the solution. | Low | No | Trivial fix. Good moment to turn on `TreatWarningsAsErrors` (T2). |
| **R8. Primary toolbar button turns into "Remove" in a Feature context.** `WorkspaceRepositoriesHeader.razor:60`: the same primary button reads Branch, Feature, Create PR or **Remove** depending on hidden state. | Med | UX review before release | A destructive action appearing in the slot users click by habit. See U3. |

---

## 1. Architecture and code quality

### A1. Managed git hooks overwrite user hooks and ignore `core.hooksPath` - High

Evidence:

- `src/GrayMoon.Agent/Services/GitService.cs:1289-1326` (`WriteSyncHooksAsync`) unconditionally writes `post-commit`, `post-checkout`, `post-merge`, `post-update` and `pre-push`.
- `GitService.cs:1556-1562` (`WriteHookFile`) is a plain `File.WriteAllText`, with no check for an existing file and no backup or chaining.
- `GitService.cs:1337-1356` resolves `git rev-parse --git-common-dir`/hooks and ignores a configured `core.hooksPath`.

Impact: a team's `pre-push` hook (tests, lint, secret scanning) is silently replaced by a `curl` call. In repos that use husky or lefthook (`core.hooksPath=.husky`), GrayMoon's hooks never run, so hook-driven sync silently stops.

Recommendation:

- Resolve the effective hooks directory with `git rev-parse --git-path hooks`, which honors `core.hooksPath`.
- If a non-GrayMoon hook exists, chain it: rename it to `<hook>.graymoon-orig` and `exec` it after the curl, or insert a marked block. Preserve `pre-push` exit codes.
- Write a marker comment and only overwrite files that carry it.
- Show "hooks managed / conflicting / bypassed" per repo in the UI.

### A2. `dotnet restore` failures are reported as success - High

Evidence:

- `src/GrayMoon.Agent/Commands/DotnetRestoreCommand.cs:42-53`: a non-zero exit is only logged (`LogWarning`), exceptions are caught per project, and the command always returns `new DotnetRestoreResponse { Success = true }`.
- The command runs through `ICommandLineService` with the **default 60s timeout** (`DotnetRestoreCommand.cs:37-40`; `src/GrayMoon.Agent/appsettings.json:10`). `restore --force --no-cache` on a real solution regularly exceeds 60s. On timeout, `CommandLineService` returns exit code -1 (`src/GrayMoon.Common/CommandLineService.cs:233-254`), which is then swallowed as above.
- App side: `src/GrayMoon.App/Services/Workspaces/WorkspacePushService.cs:749-752` awaits `SendCommandAsync("DotnetRestore", ...)` and **discards the response**. Only exceptions mark a failure (`:755-760`).

Impact: the "safe, synchronized package rollouts" promise (`README.md:33`) is undermined. A restore that failed or timed out looks successful, and the push or commit continues with stale `obj/project.assets.json`.

Recommendation:

- Return `Success = false` with per-project errors from the Worker.
- Check `response.Success` in `WorkspacePushService` and `WorkspaceGitService.Restore.cs:34`.
- Give restore its own configurable timeout (for example 10 minutes).
- Validate `relativePath` with `GitRepositoryPathValidator` (it is currently `Path.Combine`d unchecked, `:34`).
- Pass the arguments via `ArgumentList` instead of `$"restore --force --no-cache \"{fullPath}\""`.

### A3. Schema evolution has no versioning, transactions, logging or backups - High

Evidence:

- `src/GrayMoon.App/Program.cs:276-277` runs `EnsureCreated()` and then `Migrations.RunAllAsync`.
- Every migration in `Migrations.cs` (`:51-54, 82-85, 121-124, 147-151, 177-181, 201-204`) and `Migrations.Features.cs:31-34` ends in a bare `catch { }` with no logging.
- `AddNullableIntegerColumnIfMissingAsync` builds SQL by interpolation (`Migrations.cs:210,215`). The inputs are constants today, so this is a hygiene issue rather than injection.
- `Migrations.cs:117` seeds `C:\Workspace` as the root path regardless of the Worker's OS, so a Linux Worker gets a Windows path.

Recommendation:

- Introduce a `SchemaVersion` (`PRAGMA user_version`) and an ordered list of migrations, each run in a transaction.
- Log and **fail startup** on migration errors, with a clear message instead of a half-migrated database.
- Copy `graymoon.db` to `graymoon.db.bak-<version>` before the first migration of a new version.
- Add a golden-database upgrade test (R2).
- Longer term, consider EF Core migrations with a baseline migration created from the 0.1.0 schema.

### A4. Server-side components connect to their own SignalR hub over HTTP - Med

Evidence:

- `src/GrayMoon.App/Services/Ui/WorkspaceSyncHubConnectionHelper.cs:21-25` builds a `Microsoft.AspNetCore.SignalR.Client.HubConnection` to `navigationManager.ToAbsoluteUri("/hubs/workspace-sync")`. The doc comment calls it the "browser-side connection", but it runs inside the Blazor Server circuit, on the server.
- Used by `WorkspaceRepositories.Realtime.cs`, `WorkspaceGitChanges.Realtime.cs`, `WorkspaceActions.State.cs` and `WorkspaceActionNotificationPanel.razor`.
- Events are broadcast with `Clients.All` from 14 files (for example `Services/Agent/SyncCommandHandler.cs`, `Services/Application/WorkspaceBranchOperations.cs`). No workspace groups are used.

Impact:

- The URL is the *browser's* base URL. With `docker run -p 9000:8384` or a TLS reverse proxy, the container cannot reach `http://localhost:9000` or `https://graymoon.example.com`, so real-time refresh silently stops working.
- Each tab adds 1-4 extra loopback WebSocket connections. Every event is JSON-serialized, sent over the network and deserialized, only to reach the same process.
- Adding authentication later (S3) breaks these connections unless they carry credentials.

Recommendation: replace the hub-client pattern with an in-process event bus (a singleton `IWorkspaceEvents` built on `Channel`s or C# events). Components subscribe directly, and the hub only fans out to real browser clients if that is still needed. Use SignalR groups per workspace for any remaining hub traffic.

### A5. Circuit-scoped `AppDbContext` still reaches pages through repositories - Med

Evidence:

- `AGENTS.md:33-73` bans injecting `AppDbContext` into pages, but most repositories take the scoped context: `Repositories/WorkspaceRepository.cs:9-11`, `WorkspaceProjectRepository.cs:8-10`, `ConnectorRepository.cs:7`, `AppSettingRepository.cs:8`, `WorkspaceFileRepository.cs:7`, `WorkspaceActionRepository.cs:9`, `RepositoryRepository.cs:8-10`, `WorkspaceFileVersionConfigRepository.cs:7-9`, `WorkspaceRepositoryCustomDependencyRepository.cs:7-9`.
- These repositories are injected into pages and the layout: `Components/Layout/MainLayout.razor:8` (awaits four reads in `OnInitializedAsync`, `:75-96`), `Pages/Workspaces.razor:12-15`, `Home.razor:6,12`, `WorkspaceFiles.razor:14-16`, `WorkspaceDependencies.razor:11-12`, `Connectors.razor:8`, `WorkspaceActions.razor.cs:27`.
- The hazard is already showing: `ConnectorRepository.cs:95-100` has to detach "stale Repository/WorkspaceRepositoryLink entities that may have been dirtied by a failed MergeRepositoriesAsync in this circuit-scope DbContext".
- Background loops swallow exactly the exception this produces: `WorkspaceRepositories.PrPolling.cs:100-102` catches `InvalidOperationException` silently.

Why it mostly works today: Microsoft.Data.Sqlite's async APIs complete synchronously, so overlapping awaits rarely interleave. Any future switch of provider, or a `Task.Run` path, exposes it.

Recommendation: move repositories to `IDbContextFactory` (one short-lived context per method, `AsNoTracking` for reads). Then `AGENTS.md` rule 4 can be retired, and the "second operation started on this context" class of bugs goes away for good.

### A6. God components and orchestration logic in pages - Med

Evidence:

- `Components/Pages/WorkspaceRepositories.*`: 27 partial files, about 9,000 lines, 26 injected services.
- `WorkspaceGitChanges.*`: 12 files, about 3,000 lines.
- Single files over 1,000 lines: `Agent/Services/GitService.cs` (1,718), `Modals/SwitchBranchModal.razor` (1,617), `Shared/WorkspaceActionNotificationPanel.razor` (1,137), `Services/Workspaces/WorkspaceFileVersionService.cs` (1,171), `Services/Workspaces/WorkspacePushService.cs` (1,030).
- Pages still contain plan-building logic: `WorkspaceRepositories.Push.cs:185-197` (`BuildPushPlanAsync`), `:12-71` (dependency checks deciding whether to show the modal).

Recommendation:

- Extract per-workflow presenter classes (for example `PushWorkflow`, `MergeWorkflow`) that own state and talk to `IWorkspace*Operations`. The page keeps only rendering.
- Split `GitService` by concern: remote, branches, commit, hooks, worktrees.
- `SwitchBranchModal` and `BranchModal` would benefit from a shared branch-picker component.

### A7. Process execution: unbounded output buffering and two argument styles - Med

Evidence:

- `src/GrayMoon.Common/CommandLineService.cs:331-385` and `:391-444` accumulate the entire stdout and stderr in a `StringBuilder` with no cap. `git diff`/`git show` on large or binary files, and `git log` in big repos, are fully materialized in Worker memory and then sent over SignalR (10 MB limit, `Program.cs:84-87`).
- Two overloads exist: string `Arguments` (`:20-117`) and `ArgumentList` (`:119-223`). Many git calls still use the string form (see S5).
- `ApplyNonInteractiveGitEnvironment` (`:276-280`) sets only `GIT_TERMINAL_PROMPT=0`. Git Credential Manager can still show GUI prompts (no `GCM_INTERACTIVE=never`), so a call can hang until the timeout.

Recommendation:

- Add a max-output option. Truncate with a marker, or stream to a temp file for diffs.
- Deprecate the string overload, or make it internal, and migrate all callers to `ArgumentList`.
- Set `GCM_INTERACTIVE=never` alongside `GIT_TERMINAL_PROMPT=0`.

### A8. Timeouts are inconsistent across App and Worker - Med

Evidence:

- App: a single `AgentBridge.CommandTimeoutSeconds = 240` for every command (`Services/Agent/AgentBridge.cs:23,52`; `appsettings.json:52`).
- Worker: `CloneTimeoutSeconds: 0` (infinite), `NetworkTimeoutSeconds: 180` with Polly retry pipelines (`Agent/Services/GitProcessRunner.cs:48-66`; `Agent/appsettings.json:10-13`).

Impact: a large clone, or a fetch that retries, exceeds 240s. The App reports "Worker command timed out" and sends a cancel, which kills a clone that would have succeeded.

Recommendation: send a per-command deadline from the App (clone/restore long, status short) and let the Worker report progress heartbeats that extend the App-side wait.

### A9. Static mutable state and singletons holding per-user UI state - Low/Med

Evidence:

- `AgentResponseDelivery` is a static registry (`Services/Agent/AgentResponseDelivery.cs:7-19`).
- `ConnectorHelpers.InitializeTokenProtector` is a static setter (`Program.cs:273-274`).
- `ToastService` is a singleton with one `_message` field (`Services/Ui/ToastService.cs:4-38`). Every tab's `Toast` component subscribes (`Components/Shared/Toast.razor:24,59`), so a toast raised by one tab's operation appears in every open tab and Desktop window, and concurrent toasts overwrite each other.

Recommendation: make the toast service scoped per circuit, with a queue. Process-wide notifications can go through the event bus from A4. Wrap the static registries in injectable singletons so tests can isolate them.

### A10. Configuration and logging defaults - Low

Evidence:

- `src/GrayMoon.App/appsettings.json:14,31` sets `GrayMoon.App` to `Verbose`/`Trace` in the shipped configuration.
- `src/GrayMoon.Agent/appsettings.json:16-20` sets `Default: Debug`.
- App logs always go to `%ProgramData%\GrayMoon\Logs` (`Program.cs:54-69`), which is machine-wide rather than per user. The comment at `:50-52` mentions `GRAYMOON_Desktop__LogDirectory`, but nothing reads it.
- Package versions float (`10.0.*`, `5.12.*` in `GrayMoon.App.csproj` and `GrayMoon.Agent.csproj`), and `Serilog.Sinks.File` differs between App (7.0.0) and Agent (6.0.0). GrayMoon has no `Directory.Packages.props` (Desktop does).

Recommendation:

- Ship `Information` by default and allow `Debug` via a Settings toggle.
- Honor a configurable log directory (per-user `%LocalAppData%` for Desktop).
- Pin versions with central package management and a lock file.

### A11. SQLite configuration - Low

Evidence: `Program.cs:91-93` forces `Cache=Shared` together with WAL (`:279-281`). SQLite's documentation discourages shared-cache mode, and with WAL it adds table-level locking for no benefit. The pragmas are applied once at startup; `busy_timeout` is per connection, so pooled connections opened later do not get it.

Recommendation: drop `Cache=Shared`. Set `Default Timeout`/`busy_timeout` through the connection string or a connection interceptor so it applies to every connection.

---

## 2. Security

Context: GrayMoon is a local developer tool. That lowers the bar, but **any web page in any browser on the same machine can reach `127.0.0.1:8384`**, and the Docker image listens on all interfaces. I confirmed both locally: the Desktop App was listening on `127.0.0.1:8384` and the Worker hook listener on `127.0.0.1:9191`.

### S1. Unauthenticated endpoint returns decrypted connector tokens - High

Evidence: `src/GrayMoon.App/Api/Endpoints/ConnectorEndpoints.cs:14` maps `GET /repos/{repoId:int}/connector`. Lines `:41-52` decrypt and return `{ connectorId, token }`. There is no authentication, origin check or caller restriction. The Worker calls it with no credentials (`src/GrayMoon.Agent/Services/AgentTokenProvider.cs:33-36`).

Impact: `curl http://127.0.0.1:8384/repos/1/connector` returns the PAT. Repository IDs are small sequential integers. Any local process can call it; on Docker, anyone on the LAN can; and a web page can read it by combining DNS rebinding with `AllowedHosts: "*"` (`appsettings.json:34`).

Recommendation: remove the HTTP endpoint and deliver tokens over the authenticated Worker hub connection (S4). At minimum, require a Worker pairing secret header and reject requests whose `Host` is not loopback.

### S2. Token encryption uses a public default key; Desktop never sets one - High

Evidence:

- `src/GrayMoon.App/Services/Security/TokenEncryptionKeyProvider.cs:17-24` falls back to `CreateDefaultKeyString()`, which is `SHA256("GrayMoon-Default-Token-Encryption-Key")` (`:61-68`), when `TokenKey` is missing.
- GrayMoon.Desktop only passes `GRAYMOON_ConnectionStrings__DefaultConnection` to the App (`GrayMoon.Desktop/src/GrayMoon.Desktop/Services/AppProcessManager.cs:210`). No `TokenKey` is set anywhere in Desktop, the Dockerfile or the README.
- `GetKeyById` ignores the id (`:55-59`), so the `v2:KEYID:` format suggests rotation support that does not exist.
- `CLAUDE.md:100-102` says the protector is "backed by ASP.NET Core Data Protection". It is not; Data Protection is configured (`Program.cs:237-241`) but not used for tokens.

Impact: every default install encrypts PATs with the same publicly derivable key. Anyone with a copy of `graymoon.db` (backup, volume snapshot, synced folder) can decrypt them.

Recommendation: generate a random 32-byte key on first run and store it next to the Data Protection key ring, or simply use `IDataProtector` (DPAPI-protected on Windows). Re-encrypt tokens on startup when the default key is detected, and implement a real key-id lookup.

### S3. No authentication; CSRF on state-changing endpoints; DNS rebinding - High

Evidence:

- `Program.cs:314-332` has no authentication or authorization middleware. REST, `/hub/agent` and `/hubs/workspace-sync` are all anonymous.
- `WorkspaceOperationsEndpoints.cs:26,31,28,32`: `push`, `undo-push`, `sync` and `restore-packages` accept an **optional** body (`PushWorkspaceApiRequest? body`, `:106-129`; `UndoPush` `:231-243`; `RestorePackages` takes no body, `:245-256`). A cross-origin `fetch(url, {method: 'POST', mode: 'no-cors'})` with no body is a CORS "simple request" and needs no preflight. With no body, Push calls `PushPendingAsync(..., synchronizedPush: true)`, which pushes every pending commit in the workspace (`:126`).
- `AllowedHosts: "*"` (`appsettings.json:34`) disables host-header filtering, which enables DNS rebinding.
- Docker binds `http://+:8384` and runs as root (`Dockerfile:40-44`, no `USER`).

Impact: a malicious or compromised web page can push local commits, including to default branches, undo pushes, start syncs or restores, and (with rebinding) read tokens via S1 and repository data via the GET endpoints.

Recommendation (in order of effort):

1. Reject requests whose `Host` is not `127.0.0.1`, `localhost` or a configured name (set `AllowedHosts`).
2. Require a non-simple header (for example `X-GrayMoon-Client`) or JSON content type on every mutating endpoint, which forces a preflight. Validate `Origin` on POSTs and on hub negotiation.
3. Add a per-install API key for REST and the Worker hub. Desktop can inject it into WebView2.
4. For Docker, default the bind to loopback, document reverse-proxy authentication, and add `USER app`.

### S4. Any client can register as "the Worker" - Med

Evidence: `src/GrayMoon.App/Hubs/AgentHub.cs:18-20` calls `OnAgentConnected` for any connection to `/hub/agent`. `OnDisconnectedAsync` fails every pending request (`:40-47`). Commands, including `bearerToken` payloads for git authentication, go to whichever connection registered last (`AgentBridge.cs:38-58`).

Impact: on a reachable network, a rogue client receives tokens and can forge `ResponseCommand`/`SyncCommand` results (for example a fake "push succeeded"). Locally, any second Worker instance silently hijacks the first.

Recommendation: pair the Worker with a shared secret created at install time and validated in `OnConnectedAsync`. Reject or demote additional connections and surface "another Worker tried to connect" in the UI.

### S5. Git option injection through string-interpolated arguments - Med

Evidence: `src/GrayMoon.Agent/Services/GitService.cs:604` (`merge --no-edit {remoteBranch}`), `:745,756,765,794,800` (`checkout {branchName}`, `checkout -b {newBranchName} --no-track {startPoint}`), `:871,881` (`branch -D/-d {localName}`), `:955` (`checkout refs/tags/{name}`). There is no ref-name validation anywhere (no `check-ref-format` or equivalent in `src/`). Branch names arrive from the UI, REST (`BranchEndpoints.cs:13-20`), and remote branch lists.

Impact: a name starting with `-` becomes a git option. Combined with S3, it can be supplied cross-site.

Recommendation: validate names with `git check-ref-format --branch` (or an equivalent ported rule set) at the Application boundary, reject a leading `-`, use `ArgumentList`, and add `--end-of-options` where git supports it.

### S6. The PAT is visible in the git process command line - Med

Evidence: `GitService.cs:1589-1596` builds `-c "http.extraHeader=Authorization: Basic <base64(x-access-token:PAT)>"` on the git command line. `LogSafe` redacts it in logs, which is good. However, argv is readable by other processes of the same user, and on Linux hosts by other users via `/proc/<pid>/cmdline`. Correction to the parallel security pass: it stated that this approach keeps tokens out of "process argv visible in ps". It does not.

Recommendation: pass the header through environment-based config (`GIT_CONFIG_COUNT`, `GIT_CONFIG_KEY_0=http.extraHeader`, `GIT_CONFIG_VALUE_0=...`, git 2.31 and later) or a short-lived credential helper over stdin.

### S7. Other security items - Med/Low

- **Med - SSRF in the markdown preview.** `Services/GitChanges/MarkdownImageEmbedder.cs:65-71, 220-254` fetches arbitrary `http(s)` image URLs server-side with no private-range block. Reviewing a malicious README in a branch makes the App request internal addresses. Recommendation: block loopback, link-local and RFC 1918 ranges after DNS resolution, or proxy only allow-listed hosts.
- **Med - Worker install script integrity.** `Resources/install-worker.ps1:87-89` downloads the Worker zip over the App's scheme (HTTP by default) with no hash check and then installs an elevated service. Recommendation: embed a SHA-256 in the generated script and Authenticode-sign `graymoon-worker.exe` (Desktop CI already signs it on stable).
- **Low - Hook listener.** `Agent/Hosted/HookListenerHostedService.cs:75-81` reads the whole body with no size cap and requires no secret (`:25` is correctly loopback-only).
- **Low - No rate limiting or request size limits on REST.**

Security strengths:

- Loopback-only hook listener and Desktop handshake validation.
- `GitRepositoryPathValidator` with an anchored prefix check.
- HTML-encoded GitHub Actions log rendering (`GhaLogParser`).
- SVG sanitization before embedding.
- Correct AES-GCM usage; only the key provisioning is weak.
- Token redaction in logs (`LogSafe`).

---

## 3. Performance and reliability

### P1. PR polling every 2s per visible repository, per tab - Med

Evidence: `Components/Pages/WorkspaceRepositories.PrPolling.cs:17-19` (2s active, 5s idle, 30s hidden) and `:76-92` (refresh PRs for every visible repo, then re-query rows, then refresh merge dialogs). The loop lives in the page component, so each tab and the Desktop window polls independently. ETag 304s save primary quota but not request volume or secondary-rate-limit risk. Actions auto-refresh is a second per-page loop (`WorkspaceActions.AutoRefresh.cs:242`).

Recommendation: move polling into one server-side singleton per workspace and connector that serves all circuits from cache and publishes changes through the event bus (A4). Make intervals configurable, and back off further when nothing has changed for N polls.

### P2. Large workspaces: non-virtualized pages and whole-graph work - Med

Evidence: `WorkspaceRepositories` is virtualized (`WorkspaceRepositories.razor:90,136-224`), but `WorkspaceDependencies.razor:81,90` and `WorkspaceFiles.razor:120` render everything with `@foreach`. The local instance has a 330-repository workspace ("TEST"), which is a good benchmark target.

Recommendation: apply the same virtualization and query-service pattern to Dependencies and Files, and add a perf smoke test that seeds 300+ repositories and 2,000+ projects and times first render and level recompute.

### P3. Desktop shutdown hard-kills the App - Low/Med

Evidence: `GrayMoon.Desktop/src/GrayMoon.Desktop/Services/AppProcessManager.cs:135-148` always uses `KillProcessTree`, because the headless App has no main window to close. SQLite in WAL mode survives a process kill. However, in-flight orchestrations (a level-by-level push) stop mid-way without the Worker being told to cancel, and `WorkspaceGitChangesWriteQueue` is not drained.

Recommendation: add a graceful-shutdown message over the existing named pipe (or `/hubs/desktop`) and allow 5-10s for `StopAsync` before falling back to the kill.

### P4. Fixed port 8384 - Low

Evidence: `src/GrayMoon.App/Desktop/DesktopHostingExtensions.cs:15` fixes port 8384 so the installed Worker service's hub URL stays valid. Running Desktop and the Docker image on one machine, or anything else on 8384, makes Desktop fail with exit code 62.

Recommendation: store the chosen port in a well-known per-user file that the Worker reads at connect time, allowing fallback ports.

### P5. Swallowed failures hide reliability problems - Med

Evidence:

- Bare or empty catches remain in production code: `Hubs/AgentHub.cs:32-35`, `Migrations*.cs` (A3), and modals such as `SwitchBranchModal.razor` (5 empty catches), `GhaLogsModal.razor` (5), `BranchModal.razor`, `NewPullRequestModal.razor`, `PrepareWorkspaceModal.razor`.
- Poll loops log at Debug only (`WorkspaceRepositories.PrPolling.cs:103-106`).
- There is no `ErrorBoundary` anywhere, so one throwing row kills the circuit and the user sees the generic "GrayMoon needs to reload" screen.

Recommendation: log every swallowed exception at Warning or above with context, add `ErrorBoundary` around grid rows, modals and panels, and add a health indicator that counts recent background errors.

---

## 4. Tests and tooling

Commands run on Windows, .NET 10:

- `dotnet build GrayMoon.slnx --no-incremental`: **succeeded with 1 warning** (CS8619, `WorkspaceGitService.Context.cs:38`) and 0 errors.
- `dotnet test GrayMoon.slnx`: **787 passed, 1 failed** (App 444/444, Agent 165/165, Common 178/179). About 3 minutes.
  - The failure was `CommandLineServiceTests.RunAsync_ArgumentListOverload_DoesNotDeadlock_WhenChildWritesStdoutWhileReadingStdin`, which got exit code -1 (the 15s timeout). It **passes when re-run on its own** (2/2 in 6s).

### T1. Timing-sensitive tests - Low/Med

Evidence: `src/GrayMoon.Common.Tests/CommandLineServiceTests.cs:127-151, 207-216`. The test spawns PowerShell to fill a 262,144-byte array in a PowerShell `for` loop under a 15s limit. Under CPU load from parallel test assemblies, that is not enough.

Recommendation: use a tiny compiled helper or `cmd /c` with a pre-generated file, and raise the limit. Assert "did not deadlock" (process completed) rather than wall-clock time.

### T2. No analyzers, warnings-as-errors or central package management in GrayMoon - Med

Evidence: GrayMoon has no `Directory.Build.props`, `.editorconfig` or `Directory.Packages.props`. Each `.csproj` sets `Nullable` individually, and versions float (A10). GrayMoon.Desktop already has `TreatWarningsAsErrors=true` (`GrayMoon.Desktop/Directory.Build.props:7`).

Recommendation: copy Desktop's `Directory.Build.props` approach. Add `AnalysisLevel=latest-recommended`, `EnforceCodeStyleInBuild`, and package lock files. With only one warning today, this is cheap.

### T3. CI tests only on Linux - Med

Evidence: `.github/workflows/test.yml:14` uses `runs-on: ubuntu-latest`. The product is Windows-first (Desktop, Windows Service, `C:\Workspace` defaults, CRLF/encoding handling, PowerShell scripts). Windows-only branches, such as the PowerShell test paths above and Windows path handling, are never exercised in CI.

Recommendation: add a `windows-latest` job to the matrix (at least for Agent and Common tests).

### T4. Coverage gaps in the riskiest code - Med

Evidence (from the test file inventory in `src/GrayMoon.App.Tests`):

- No tests reference `PushOrchestrator`, `DependencyUpdateOrchestrator`, `PrepareWorkspaceOrchestrator`, `WorkspaceUndoPushHandler`, `AesGcmTokenProtector` or `TokenEncryptionKeyProvider`.
- No REST endpoint tests (`WebApplicationFactory` is never used).
- No component tests (no bUnit).
- `MigrationsTests.cs` only covers root-path seeding.
- Desktop has no tests for `AppProcessManager`, native bridge launches or the installers (from the Desktop pass, F24).

Recommendation, in priority order:

1. An upgrade test from a 0.1.0 database (R2).
2. Endpoint tests asserting auth, origin rules and the absence of tokens in responses.
3. Orchestrator tests with a fake `IAgentBridge` that returns failures. These would have caught A2.
4. bUnit tests for confirm dialogs on destructive flows.

### T5. Test quality is generally good

Tests are behavior-named and use real SQLite in-memory databases rather than mocks for queries (for example `WorkspaceRepositoryLinkListQueryServiceTests`, `FeatureContextIsolationTests`). Agent tests use real temporary git repositories (`TempGitRepositoryFixture`). This is the right approach for this domain.

---

## 5. Desktop host

Desktop strengths (verified in the parallel pass):

- Named-pipe startup handshake with an early-exit race.
- Loopback-only URL validation.
- Allow-listed native bridge.
- Job Object kill-on-close.
- HSM-backed Authenticode signing on the stable channel, verified with `signtool verify`.
- `Validate-Bundle.ps1` guarding against stray keys and PDBs.
- `TreatWarningsAsErrors`.

### D1. Obfuscated, unconditional telemetry ping - Med

Evidence: `GrayMoon.Desktop/src/GrayMoon.Desktop/App.xaml.cs:109` calls `WarmupAsync()` on every start. `:146-152` Base64-decodes `https://io.mezzorecovery.com/api/ping` and POSTs the version. All failures are swallowed (`:155-157`). The only disclosure is in the About dialog (`AboutDialog.xaml:187-189`). There is no opt-out and no mention in the docs.

Impact: the Base64 wrapper has no technical purpose. It defeats string-based audits and allow/deny tooling, which is a trust problem for a tool that holds GitHub tokens.

Recommendation: use a plain constant, document the endpoint and payload, add an opt-out (setting plus an environment variable), and name the method for what it does.

### D2. Native "open in" launchers use naive quoting - Med

Evidence: `Services/WebMessageBridgeService.cs:185,228,236,500,507,535,542` build `ProcessStartInfo.Arguments` with `$"\"{path}\""`, even though `ProcessArgumentQuoter` exists (`Services/ProcessArgumentQuoter.cs:14-25`). A path ending in `\`, such as `C:\`, escapes the closing quote.

Recommendation: route these calls through `ProcessArgumentQuoter.Join` or `ArgumentList`.

### D3. Update and lifecycle edge cases - Low

- `VelopackUpdateService.cs:100-119` stops the App before `ApplyUpdatesAndRestart`. If the apply step throws, the App is not restarted.
- `GRAYMOON_UPDATE_URL` overrides the feed in production builds (`VelopackUpdateSourceFactory.cs:13-14,29-37`).
- The tray icon is not re-added after Explorer restarts (no `TaskbarCreated` handling, `TrayIconService.cs:55-83`).
- The silent `catch` around kill-tree (`AppProcessManager.cs:338`) leaves no trace of orphaned processes.
- `ExpectedListenPort` and `AddressInUseExitCode` are duplicated across repositories (`AppProcessManager.cs:26-27` vs `DesktopHostingExtensions.cs:15,26`).

### D4. Desktop inherits App security posture - High (see S1-S3)

Desktop launches the App on `127.0.0.1:8384` with no `TokenKey` and no API key. Every browser on the machine can therefore reach it. Desktop is the natural place to generate and inject a per-install secret into both the App (environment variable) and WebView2 (request header or cookie).

### D5. Startup error UX - Low

Raw `MessageBox` text and an error overlay show exit codes and stdout tails (`App.xaml.cs:118-124`; `MainWindow.xaml.cs:447-455`), with no "Copy details" or "Open logs folder" action. The App's logs live in `%ProgramData%\GrayMoon\Logs` while Desktop's live in `%LocalAppData%`, so users have to look in two places.

---

## 6. User perspective

Observed in the running instance and verified in code. Counts come from grep over `src/GrayMoon.App/Components`.

### U1. Connector delete silently removes repositories from every workspace - High

Evidence:

- `Modals/ConnectorModal.razor:140-156` shows only "Are you sure you want to delete <name>?", and **plain Enter triggers Delete** (`:270-279`, `data-default-action` at `:149`).
- `Repositories/ConnectorRepository.cs:111-112` removes the connector.
- `Data/AppDbContext.cs:123-126` cascades `Connector` to `Repository`, `:181-184` cascades `Repository` to `WorkspaceRepositoryLink`, and further cascades remove PR/Actions/Git status rows, workspace files and version configs (`:353-356, 366-369`), and custom dependencies (`:394-402`).

Recommendation:

- Block deletion while repositories are linked, or show "N repositories in M workspaces, X configured files and Y custom dependencies will be removed".
- Require typing the connector name, and remove Enter-to-delete.
- Offer "replace token" as the primary path.

### U2. Terminology overload - Med

Evidence:

- The Repositories toolbar exposes Sync, Sync Commits, Sync Repositories, Fetch Repositories, Update, Update Files, Update and Push, Push, Push Updated, Pull, Restore Packages, Return to Default and Undo Push (`Shared/WorkspaceRepositoriesHeader.razor:60-375`; also seen in the live accessibility tree).
- "Worker" in navigation and on pages vs "Agent" in Settings copy ("Agent host", "Use Agent default"; `Pages/Settings.razor:71-91`, seen live), the route `/agent`, and log names.
- "Update" means three different things: dependency bump, merge default into branch (`UpdateBranchModal`), and version files.

Recommendation: publish a glossary (Fetch = refs only; Sync = fetch + reconcile local state; Pull = fast-forward; Update Dependencies; Update Branch; Push). Rename the actions to match, use "Worker" everywhere users can see, and add tooltips that state what each action will do.

### U3. Mode-changing primary button - Med

Evidence: `Shared/WorkspaceRepositoriesHeader.razor:60` shows "Create PR", "Remove", "Feature" or "Branch" depending on hidden state, with the click logic at `:572-614`.

Recommendation: keep stable labels and positions, show contextual suggestions as a separate highlighted chip, and never put a destructive action in the primary slot.

### U4. Accessibility - Med

Evidence:

- 12 of 29 modal close buttons lack `aria-label`: `Pages/Workspaces.razor:212`, `Modals/WorkspaceRepositoriesModal.razor:15`, `WorkspaceModal.razor:11`, `WorkspaceImportModal.razor:13`, `ViewFileModal.razor:22`, `VersionConfigModal.razor:24`, `SwitchBranchModal.razor:32`, `PrepareWorkspaceModal.razor:13`, `ConnectorModal.razor:13`, `BranchModal.razor:25`, `AddFilesModal.razor:16`, `GhaLogsModal.razor:24`.
- `role="dialog"`/`aria-modal` appears only once (`MainLayout.razor`). Custom modals are plain `div`s with no focus trap.
- 24 clickable `div`/`span` elements have no role or keyboard handler (for example the branch cell at `WorkspaceRepositoriesRow.razor:93-98`). The dependency badges in the same file do this correctly (`:138-159`), so the pattern exists.
- In the live accessibility tree, grid badges are announced as bare numbers ("0", "80", "create"), column headers are icon-only, and the hidden "GrayMoon needs to reload" dialog is always exposed to screen readers.
- Status is conveyed by color alone (green/red badges).

Recommendation: create one shared `Modal` component (role, `aria-modal`, `aria-labelledby`, focus trap, Escape, labelled close button) and migrate all modals to it. Give badges `aria-label`s such as "80 commits behind", add text headers or `aria-label`s to icon columns, and pair colors with icons.

### U5. Feedback, errors and toasts - Med

Evidence:

- Toasts are single-slot and global across tabs (A9), with no dismiss button or history.
- Error policy is inconsistent: some pages show generic "Please try again later." (`Pages/Workspaces.razor:369,649,747`), while `Connectors.razor:401,472,511` shows raw `ex.Message`.
- Import suggestions fail silently (`Pages/Workspaces.razor:822-881`, Debug-level logging only).
- Settings mixes per-field Save buttons with "changes save immediately" sections, and the inline "saved" text never clears (seen live; `Settings.razor:46-65,103-126`).

Recommendation: queue toasts per circuit with a notification drawer. Use one error formatter (user-facing summary plus "details" expander plus log correlation id) and one save model per page.

### U6. Onboarding and empty states - Low/Med

Evidence:

- `Home.razor:135-142` signals missing setup only through button color.
- Empty states are single muted table rows (`Workspaces.razor:99`, `Connectors.razor:97`).
- "Worker is not available" (`Workspaces.razor:399`) does not link to the Worker page.
- The root path default `C:\Workspace` and the Feature storage root `~\.graymoon` are presented side by side without explaining why they differ (seen live).

Recommendation: a first-run checklist (Install Worker, Add Connector, Create Workspace, Clone), deep links inside every blocking error, and empty states with a primary call to action.

### U7. Keyboard support - Low

Evidence: the only global-feeling shortcut is Ctrl+Enter to commit on Git Changes (`WorkspaceGitChanges.razor:83,113-114`). Escape is handled in all modals, which is good. Neither the live page nor the code shows a focus-search shortcut, grid navigation or a shortcut help overlay.

Recommendation: see the command palette in E2.

### U8. Large-workspace UX - Low

Evidence: Dependencies and Files are not virtualized (P2). The Workspaces list puts Remove beside Edit with equal visual weight (seen live). The remove modal does not state how much configuration will be lost (files, version patterns, custom dependencies; `Workspaces.razor:203-231`).

---

## 7. Enhancement ideas (prioritized)

1. **E1. Trust model: local pairing and an API key, plus a hardened Desktop.** This unlocks everything below (MCP, automation, LAN/team use) safely. Without it, every new endpoint widens S3.
2. **E2. Command palette (Ctrl+K) with a verb glossary.** It turns about 15 scattered actions into a searchable surface ("push level 2", "switch all to feature/x"), and is cheap to build on the existing `IWorkspace*Operations` facades.
3. **E3. Plan-then-execute (dry run) for every multi-repo mutation.** Generalize Return to Default's `Analyze*` then `Execute*` pattern (`docs/architecture/02-system-architecture.md:212-220`) to Push, Update, Prepare and Undo Push: show per-repo actions, skips and risks before running. This is the single biggest confidence builder for a tool that pushes to many repos at once, and it doubles as an API and MCP primitive.
4. **E4. Persistent activity log and audit trail.** Store each operation (who, what, per-repo result, terminal output, duration) in SQLite, with a per-workspace Activity tab. Today the terminal buffer (800 lines) and toasts disappear. This also fixes "did that actually work?" (A2, P5).
5. **E5. Event-driven freshness.** One server-side poller per connector, plus optional GitHub webhooks via a relay or `gh` CLI forwarding, feeding the in-process event bus (A4, P1). Fewer API calls, faster UI, and multiple tabs come for free.
6. **E6. Health and diagnostics page.** Worker version/OS/git/dotnet, hook status per repo (A1), migration version (A3), recent background errors, GitHub rate-limit state, and a "copy diagnostics bundle" button. Turns support conversations into one attachment.
7. **E7. MCP and automation surface.** Already on the roadmap. Build it on E1 and E3 so agents get plan/execute semantics and the same safety prompts.
8. **E8. Undo and safety net.** Snapshot refs before every multi-repo mutation (a `refs/graymoon/backup/<op-id>` namespace) to make "undo last operation" possible across repos.
9. **E9. Bulk policy checks.** Before push or PR: protected-branch detection for all paths (today the default-branch warning only covers the single-repo badge path, `WorkspaceRepositories.Push.cs:84-95`, not the toolbar Push at `:12-71`), uncommitted-changes checks, and CI status gates.

---

## 8. Notable strengths

- A clear architectural contract (`docs/architecture/02-system-architecture.md:518-530`) that the code mostly follows: App never touches disk, Application facades, query services for large reads.
- `WorkspaceOperationRunner` (`Services/Jobs/WorkspaceOperationRunner.cs`) gives clean process-wide mutation exclusivity with hierarchical context locks. Background jobs resolve fresh DI scopes (`ScopedExecutor`) instead of reusing circuit services.
- Process execution: UTF-8 everywhere, pipe-deadlock-safe ordering, process-tree kill, and timeouts that return synthetic failures (`CommandLineService.cs:72-117, 225-270`).
- Git Changes design: watcher-driven, coalesced, persisted projections, and a separate read/diff pool on the Worker.
- Good, behavior-named tests against real SQLite and real git.
- Documentation captures real incidents and the rules that prevent them (`AGENTS.md`).

---

## 9. Prioritized improvement backlog

### Quick wins (a day or less each)

| Item | Ref | Severity |
|---|---|---|
| Remove `GET /repos/{id}/connector` or require a Worker secret plus loopback `Host` | S1 | High |
| Generate a per-install token key (or use `IDataProtector`) and re-encrypt on startup; fix `CLAUDE.md:100-102` | S2 | High |
| Set `AllowedHosts` to loopback names; require a custom header on all mutating REST endpoints; check `Origin` | S3 | High |
| `DotnetRestoreCommand`: return real failures with a longer timeout; check `response.Success` in `WorkspacePushService` | A2 | High |
| Connector delete: impact summary, type-to-confirm, no Enter-to-delete | U1 | High |
| Log every swallowed exception in `Migrations*.cs` and `AgentHub`; fail startup on migration error | A3, P5 | High |
| Add `USER app` to the Dockerfile | S3 | Med |
| Plain-string telemetry URL, documentation and opt-out | D1 | Med |
| `aria-label` on 12 close buttons; `aria-label`s on grid badges and icon headers | U4 | Med |
| Set `GCM_INTERACTIVE=never`; ship `Information` log levels | A7, A10 | Low |
| Fix the CS8619 warning and enable `TreatWarningsAsErrors` | R7, T2 | Low |
| De-flake the PowerShell pipe test | T1 | Low |

### Medium (a few days each)

| Item | Ref | Severity |
|---|---|---|
| Hook manager: honor `core.hooksPath`, chain existing hooks, show hook status | A1 | High |
| Versioned, transactional migrations with backup, plus a golden 0.1.0 upgrade test | A3, R1, R2 | High |
| Worker pairing secret for `/hub/agent`; tokens only over the authenticated channel | S4, S1 | Med |
| Ref-name validation at the Application boundary; migrate git calls to `ArgumentList`; tokens via `GIT_CONFIG_*` environment variables | S5, S6 | Med |
| Replace the server-side hub-client pattern with an in-process event bus; use workspace groups | A4 | Med |
| Move repositories to `IDbContextFactory` | A5 | Med |
| Per-command timeouts and progress heartbeats between App and Worker | A8 | Med |
| Per-circuit toast queue and notification drawer; one error formatter | A9, U5 | Med |
| Shared accessible `Modal` component; `ErrorBoundary` around rows and modals | U4, P5 | Med |
| Windows CI job; endpoint, orchestrator and migration tests | T3, T4 | Med |
| Feature remote delete passes `bearerToken` and surfaces failures | R3 | Med |
| Desktop: graceful App shutdown, `ProcessArgumentQuoter` everywhere, restart App after a failed update apply | P3, D2, D3 | Med |
| SSRF guard in `MarkdownImageEmbedder`; hash-verified Worker install | S7 | Med |

### Strategic (multi-week)

| Item | Ref |
|---|---|
| Trust model (API key or pairing) as the base for MCP and automation | E1, E7 |
| Plan-then-execute for all multi-repo mutations, plus a persistent activity log | E3, E4 |
| Server-side shared pollers or webhooks feeding an event bus | E5, P1 |
| Decompose `WorkspaceRepositories` and `GitService` into workflow presenters and focused services | A6 |
| Terminology redesign, command palette and first-run checklist | U2, U3, U6, E2 |
| Diagnostics page and support bundle | E6 |
| Cross-repo ref snapshots for "undo last operation" | E8 |
