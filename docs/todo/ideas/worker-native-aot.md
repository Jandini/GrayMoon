# Worker as a Native AOT executable

Status: analysis only, nothing is built. One part is tested: LibGit2Sharp does not work as Native AOT (item 5). Written on 2026-11-27 from a read of the Worker, the shared libraries, the App and the Desktop release scripts, plus trial publishes into a scratch folder (no project file was changed).

## Goal

- The Worker starts on a host that has no .NET runtime installed.
- GrayMoon.App is published self contained, so the installed product does not ask the user for a .NET or ASP.NET Core runtime.
- The total package stays small: the Worker must not carry a second copy of the .NET runtime (a self contained Worker without AOT is about 70 MB on disk).
- The release signs as few files as possible, and the signed files hold the first-party code.

## What the trial publishes showed

Worker, `win-x64`, Release, .NET SDK 10 with ILCompiler 10.0.15:

| Publish | Files | PE files | Size on disk | Zip |
|---|---|---|---|---|
| Today: framework-dependent, ReadyToRun | 59 | 56 | 14.0 MB | 5.9 MB |
| Native AOT (`PublishAot=true`, `InvariantGlobalization=true`, no size options) | 3 | 2 | 19.8 MB | 8.4 MB |

The three AOT files are `graymoon-worker.exe` (17.8 MB), `git2-5853918.dll` (2.0 MB, the native libgit2 that LibGit2Sharp loads) and `appsettings.json`. The 77 MB `graymoon-worker.pdb` is produced next to them and is not shipped.

What this means:

- The AOT Worker is not smaller than today's Worker zip. It is about 2.5 MB larger, because the parts of the runtime it uses are compiled into the exe. What it removes is the .NET 10 runtime requirement on the Worker host. Compared with a self contained Worker (the other way to drop the requirement) it is far smaller.
- The Worker compiles and links as Native AOT today. Nothing in the dependency list blocks the build.
- The compiler reported 75 distinct problems in first-party code (each shows twice in the log, once from the analyzer and once from the AOT compiler) and one in Serilog. They are listed in "Worker code" below. No warning came from LibGit2Sharp, the SignalR client, System.CommandLine, Polly, the hosting packages or the configuration binder.
- The AOT Worker exe was not started. Warnings show what the compiler can see; several items below can only be confirmed by running the exe. LibGit2Sharp was run as AOT in a separate probe exe, and it failed with no compiler warning at all (item 5), which shows how little a clean compile proves.

GrayMoon.App, `win-x64`, Release (worker zip not counted):

| Publish | Files | PE files | Size on disk | Zip |
|---|---|---|---|---|
| Today: framework-dependent, ReadyToRun | 505 | 35 | 72.0 MB | 31.5 MB |
| Self contained, ReadyToRun, many files | 835 | 365 | 182.4 MB | 81.6 MB |
| Self contained, single file (`PublishSingleFile`, native libraries included, no ReadyToRun) | 469 | 1 | 151.1 MB | 66.1 MB |

`wwwroot` is 28.1 MB of each row. The zip column is a plain zip of the folder, as an estimate of the download; the Velopack package compresses differently.

A self contained App is 35 to 50 MB larger to download than today's. The AOT Worker does not pay for that; it only avoids adding a further 70 MB.

## Worker code

Every item here must be done before the switch. Items 1 to 4 are the 76 warnings.

### 1. JSON goes through a source-generated context (70 call sites)

Reflection-based `System.Text.Json` does not work under Native AOT. The call sites:

| Where | Calls | Types |
|---|---|---|
| `Services/CommandJobFactory.cs` | 62 | Every `*Request` type, one `JsonSerializer.Deserialize<T>(json, options)` per command |
| `Hosted/HookListenerHostedService.cs:146` | 1 | `NotifyPayload` |
| `Services/GitService.cs:253` | 1 | `GitVersionResult` (GitVersion output, case-insensitive) |
| `Services/WorkerTokenProvider.cs:51` | 1 | `ConnectorTokenResponse` via `ReadFromJsonAsync` |
| `Services/WorkspaceCapabilityProvider.cs:136` | 1 | `RepositoryOperationCapabilities` via `ReadFromJsonAsync` |
| `Hosted/JobBackgroundService.cs:296` | 1 | `WorkerCommandResponse`, serialized only to log its size |
| `GrayMoon.Common/Manifest/WorkspaceManifestFile.cs` | 2 | `WorkspaceManifest` read and write |
| `Hosted/SignalRConnectionHostedService.cs:122` | hub | Every type that crosses the hub, see item 2 |

Work:

- Add a `JsonSerializerContext` in the Worker that names the 60 request types (`Jobs/Requests`), the 57 response types (`Jobs/Response`), `WorkerCommandResponse`, `NotifyPayload`, `GitVersionResult`, `ConnectorTokenResponse`, `RepositoryOperationCapabilities`, and the hub argument types (`string`, `int`, `JsonElement?`, the `SyncCommand` notification, the `GitChangesSnapshotUpdated` notification, the `ReportQueueStatus` arguments).
- The context carries the options that `WorkerJsonOptions.SerializerOptions` has today (camelCase, case-insensitive). `GitService` has its own options (case-insensitive only), so it needs its own context or a second options instance. The hub options also set `DefaultIgnoreCondition = Never`.
- Add a context in `GrayMoon.Common` for `WorkspaceManifest`. The App uses the same file, so the App changes with it. The custom converters (`LenientBooleanJsonConverter`, `ReferenceBooleanJsonConverter`, the four `JsonStringEnumConverter<T>` attributes in `Common/Ai`) are generic or hand-written and work with source generation.
- Replace each call with the overload that takes a `JsonTypeInfo<T>`.

The request and response types already name their properties with `JsonPropertyName` (625 attributes), which lowers the risk that generated JSON differs from today's. The JSON on the wire must not change: the App is not changed by this work, and an older or newer Worker must keep working with it during an update.

### 2. The hub: responses are typed `object`

`WorkerCommandResponse(bool Success, object? Data, string? Error)` carries each command's response in `Data`, and `ICommandDispatcher.ExecuteAsync` returns `Task<object?>`. With a generated context the serializer finds the type of `Data` at run time in the context. A response type that is missing from the context is not a build error: that one command fails with `NotSupportedException` the first time it runs on the AOT build.

Work:

- Put the context into the hub protocol: `AddJsonProtocol(o => o.PayloadSerializerOptions.TypeInfoResolverChain.Insert(0, WorkerJsonContext.Default))`.
- Add a convention test: every `ICommandHandler<TRequest, TResponse>` registered in `RunCommandHandler` has both types in the context, and every type passed to `HubConnection.InvokeAsync` or received by `HubConnection.On` is in it. The dispatcher's constructor already lists all handlers, so the test can read them from there.
- `JobBackgroundService.cs:128` builds an anonymous object only to log it. Anonymous types cannot be in a context; see item 4.

The SignalR client supports Native AOT with the JSON protocol. The Worker uses only what is supported: `On<T1..T4>` with plain parameters and `InvokeAsync` with plain arguments, no strongly typed hub proxy and no streaming.

### 3. Property lookups by name (4 places)

These read a property through reflection on a runtime type. Under AOT the property can be trimmed away, and then the lookup returns null with no error.

| Where | Reads | If the lookup finds nothing |
|---|---|---|
| `Hosted/JobBackgroundService.cs:254,258` `GetCommandSuccessAndError` | `Success`, `ErrorMessage` of any response | A failed command is reported to the App as successful |
| `Hosted/CommandJobStreamLabel.cs:31` | `ProviderId`, `RepositoryName`, `WorkspaceName` of any request | The overlay output is labeled with the command name instead of the repository |
| `Jobs/JobEnvelopeExtensions.cs:18` | `WorkspaceId` of any request | The job is not counted for its Workspace in the queue status |

The first one is the dangerous one. Work: replace all three with interfaces that the request and response types implement (for example a response interface with `Success` and `ErrorMessage`, a request interface for the label, one for `WorkspaceId`), and a convention test that every type with such a property implements the interface.

### 4. Serilog destructuring (1 warning inside Serilog)

`JobBackgroundService.cs:130` and `:201` log `{@ResponseBody}`. Destructuring walks the object's properties by reflection; under AOT it logs what survived trimming. Work: log the JSON text produced by the context, guarded by `logger.IsEnabled(LogLevel.Trace)`, and drop the anonymous `errorBody`.

No other use of Serilog is affected: the logger is built in code (`RunCommandHandler.cs:92`), not read from configuration.

### 5. LibGit2Sharp

Used in six places: `LibGit2SharpLocalGitSnapshotReader`, `LibGit2SharpGitIgnoreService`, `LibGit2SharpConfigReader`, `RepositoryConfigurationInitializer`, `GitVersionInputFingerprint`, `WorkerInstallProbe`.

It compiles with no warning, and the native `git2-5853918.dll` is copied next to the exe.

**Tested on 2026-11-27: LibGit2Sharp 0.32.0 does not work as Native AOT. It gives wrong answers without an error.**

The test is `dev/Test-LibGit2SharpAot.ps1` with the probe project `src/GrayMoon.Worker.AotProbe` (not in the solution, not shipped). The probe builds fixture repositories and runs 188 checks over the six classes, each against the git CLI: branches with and without an upstream, a divergence base, non-ASCII branch and file names, annotated and lightweight tags, detached and unborn HEAD, a linked worktree, a bare repository, a folder that is no repository, ignore rules, layered config, a locked config file, 8 threads of repeated reads, finalizers, and deleting the repository afterwards. The script runs it on the normal runtime, as an AOT exe, and as an AOT exe in an install layout (only the exe and the dll, started from another folder), and compares the answers line by line.

| Run | Passed | Failed |
|---|---|---|
| Normal runtime | 188 | 0 |
| Native AOT | 179 | 9 |
| Native AOT, install layout | 179 | 9 |

What works under AOT: the native library is found next to the exe, repositories open, refs, tags, commits, history divergence, the index, ignore rules, repository-local config reads and writes, non-ASCII names, the exception types built from native error codes, parallel use and handle release.

What fails, and why: LibGit2Sharp has 24 native methods that return their answer through a libgit2 buffer (`GitBuf`, a class with sequential layout, passed without `[Out]`). The normal runtime passes such a class by pinning it, so the native write is visible. The AOT compiler copies it in and does not copy it back. Each of these calls succeeds and returns nothing. Shown directly by `Repository.Discover`, `Commit.PrettifyMessage` and `Repository.Describe`, which return null or empty under AOT. Among the 24:

| Native method | Effect on the Worker under AOT |
|---|---|
| `git_config_find_global`, `_system`, `_xdg`, `_programdata` | Every `Repository` sees only its repository-local config. `LibGit2SharpConfigReader` reports a globally set `core.hooksPath` as unset, so `GitSyncHooks` would write hooks where git does not run them. The same reduced config is used for ignore evaluation (`core.excludesFile`) and `core.ignorecase`. |
| `git_branch_upstream_name` | `LibGit2SharpLocalGitSnapshotReader` finds no upstream for any branch. The fixture branch that is 1 ahead and 1 behind its upstream is reported as 3 outgoing, no incoming, no upstream. |
| `git_repository_discover`, `git_describe_format`, `git_message_prettify` and the rest | Not called by the Worker today. |

`GitVersionInputFingerprint`, `RepositoryConfigurationInitializer` (local level only) and the ignore rules that come from files in the repository passed every check.

So LibGit2Sharp 0.32.0 cannot stay as it is. See open decision 1.

**Tested fix:** `[In, Out]` on every `GitBuf` parameter in `LibGit2Sharp/Core/NativeMethods.cs` (25 parameters, one file, no other change), built from the commit behind 0.32.0 (`eaa698d`) as `0.32.1-graymoon.1`. With that build the script reports 188 of 188 on the normal runtime, as AOT and in the install layout, with no differing answer. Run it with `-LibGit2SharpVersion <version> -PackageSource <folder>`.

Upstream (checked 2026-11-27): 0.32.0 is the latest release. Issue [#2187](https://github.com/libgit2/libgit2sharp/issues/2187) reports the same cause, and pull request [#2188](https://github.com/libgit2/libgit2sharp/pull/2188) (open since 2026-07-22, no review) fixes it for the four `git_config_find_*` calls and `git_repository_discover` only. It does not change `git_branch_upstream_name`, so the upstream counts would stay wrong with it. Issue #2160 asks for static linking of libgit2 into an AOT exe, which would remove the separate dll; it is open.

**A second finding, not about AOT:** `WorkerInstallProbe` does not detect a missing native library, on today's runtime either. `GlobalSettings.Version` reads assembly attributes and does not load `git2-*.dll`; the script's last step (exe without the dll) gets "no damage". Fixed on 2026-11-27: the probe reads `GlobalSettings.Version.Features`, which the native library answers (`WorkerInstallProbeTests.A_missing_native_library_is_reported_as_damage`).

### 6. Windows service code (15 `DllImport`, `ServiceController`)

`Platform/Windows/WindowsServiceManager.cs`, `WindowsLsaPolicy.cs` and `WindowsCredentialValidator.cs` call `advapi32.dll` and `kernel32.dll`. They compiled with no warning. Under AOT the marshalling code is generated at build time, so each path must be run once on the AOT exe: install with a new account, update of an existing service, the logon-right grant, the password check, stop, start, uninstall. Moving them to `LibraryImport` is optional and not needed for the switch.

`UseWindowsService`, `UseSystemd` and `System.ServiceProcess.ServiceController` are supported under AOT.

### 7. Configuration

`Configure<T>` is called for `WorkerOptions`, `GitChangesOptions`, `GitProcessOptions` and `ProcessExecutionOptions`, and `WorkerCli.cs:54` calls `Bind`. With `PublishAot` the SDK turns on the configuration binding source generator, and it raised no warning for these types. If the generator skips a property, the option keeps its default and nothing fails. Work: a test that binds every options class from a file where each property has a non-default value.

`Extensions.AddEmbeddedJsonFile` reads `appsettings.json` from the assembly's resources through `EmbeddedFileProvider`. Resources are kept in an AOT exe. To verify by running: the embedded file is read, and the `appsettings.json` beside the exe overrides it.

### 8. Version reporting

`SignalRConnectionHostedService.cs:54` and `RunCommandHandler.cs:44` read `AssemblyInformationalVersionAttribute` from the executing assembly. This works under AOT. It must be verified on the real exe because the App compares this value to decide whether to offer a Worker update.

### 9. Regular expressions

15 expressions use `RegexOptions.Compiled`. Under AOT nothing can be compiled at run time, so they run interpreted, which is slower. 20 already use `GeneratedRegex`. Work: convert the 15 to `GeneratedRegex`. This is a performance item, not a correctness item.

### 10. Culture data

The trial used `InvariantGlobalization=true`. No code in the Worker or in `GrayMoon.Common` reads a culture (`CultureInfo`, culture-aware comparison). On Linux an AOT exe without this setting needs `libicu` on the host; with it, it needs nothing. See open decision 5.

### What needs no change

System.CommandLine 2.0.2, Microsoft.Extensions.Hosting, Polly 8, `HttpListener` (the hook listener), `FileSystemWatcher`, starting git and other processes, the job queues and the dispatcher's dictionary of handlers.

## Project files

| File | Change |
|---|---|
| `GrayMoon.Worker.csproj` | Add `PublishAot`. This also turns on the trim and AOT analyzers in every build, so a new violation shows in the IDE. Make the `IL` warnings errors. Remove `PublishSingleFile=false` and `SelfContained=false`. |
| `GrayMoon.Worker.csproj` | `PackAsTool`, `PackageType=DotnetTool` and `ToolCommandName` are still set, but no workflow, script or document packs or installs the Worker as a dotnet tool. The trial turned `PackAsTool` off. See open decision 6. |
| `GrayMoon.Common.csproj`, `GrayMoon.Abstractions.csproj` | Add `IsAotCompatible`, so the analyzers run on the shared code the Worker compiles in. `GrayMoon.Common/Search/FilterSearchEfQueryable.cs` builds expression trees for EF Core; the Worker does not reach it and the compiler raised nothing, but the analyzer may flag it once it runs on the project. |
| `GrayMoon.Worker.Tests.csproj` | No change. The tests keep running on the normal runtime, which means they do not prove AOT behavior; see "Verification". |

Debugging and `dotnet run` are not affected: `PublishAot` changes only `dotnet publish`.

Size options to decide after the first working build (`OptimizationPreference`, `StackTraceSupport`, `UseSystemResourceKeys`, `EventSourceSupport`): the trial used none of them. Stack traces in the log keep method names without the pdb; line numbers are already absent today because the release strips pdb files. Keep the pdb as a build artifact for crash analysis.

## Build and release

### Native AOT cannot be built for another operating system

This is the largest change outside the Worker's code.

`GrayMoon/Dockerfile` publishes the Worker for `linux-x64` and for `win-x64` inside a Linux SDK image and puts both zips into the App image. A Linux machine cannot produce a Windows AOT exe. The Windows zip for the Docker image has to be built on a Windows runner and handed to the image build, or the image stops offering it. See open decision 2.

The Linux zip can still be built in the Dockerfile, with two additions: the build stage needs `clang` and `zlib1g-dev`, and the resulting exe requires a glibc at least as new as the build image's. A framework-dependent Worker has no such floor. See open decision 3.

### Build machines

- Windows: Native AOT needs the MSVC linker (Visual Studio "Desktop development with C++" or the Build Tools). GitHub's `windows-latest` has it.
- On this development machine (Visual Studio 18 Build Tools) the link step failed with `'vswhere.exe' is not recognized` until `C:\Program Files (x86)\Microsoft Visual Studio\Installer` was on `PATH`. `Initialize-DevelopmentWorkspace.ps1` or the build documentation should cover this.
- The AOT compile of the Worker took roughly two minutes here. It runs on every `dotnet publish` of the App on Windows, because of the target below.

### Places that publish the Worker

All of them pass `--self-contained false` and `/p:PublishReadyToRun=true` today.

| Place | Change |
|---|---|
| `GrayMoon.App.csproj`, target `PublishWorkerBundle` | Publish as AOT. The existing step that deletes `*.pdb` before zipping stays and matters more (77 MB). |
| `GrayMoon/Dockerfile`, stage `build-worker` | See above. |
| `GrayMoon.Desktop/build/Publish-GrayMoonBundle.ps1` | The block that publishes the Worker itself when the App did not (kept for older GrayMoon commits) repeats the flags. Change it with the target, or delete it. |
| `GrayMoon/.github/workflows/test.yml` | Add an AOT publish with warnings as errors, and the smoke test from "Verification". |

### Signing

Today the stable release signs three files, plus `Setup.exe`: `GrayMoon.Desktop.exe`, `GrayMoon.App.exe` and `graymoon-worker.exe` taken from the root of the worker zip (`Prepare-FirstPartySigningTargets.ps1`). For the App and the Worker these are small launcher stubs. The first-party code is in unsigned files: `graymoon-worker.dll`, `GrayMoon.App.dll`, `GrayMoon.Application.dll`, `GrayMoon.Common.dll`, `GrayMoon.Abstractions.dll`.

After the switch the count is the same and the Worker's signature covers all of the Worker's code. `Prepare-FirstPartySigningTargets.ps1`, `Apply-SignedFirstPartyBinaries.ps1` and `Verify-AuthenticodeSignatures.ps1` need no change: their contract is one `graymoon-worker.exe` at the zip root, and that stays. `git2-5853918.dll` is third-party and stays outside the allow-list, as today.

`Validate-Bundle.ps1`: add a check that the worker zip holds no `graymoon-worker.dll` and no `*.runtimeconfig.json`, which proves the zip is the AOT build.

`Generate-ThirdPartyNotices.ps1`: the .NET runtime and every package the Worker uses are now compiled into `graymoon-worker.exe` instead of shipped as separate files. Check that the notices still name each of them.

## App

### What changes because of the Worker

| Place | Change |
|---|---|
| `Components/Pages/Worker.razor:72,227` | Remove the "Download the .NET 10 runtime" note and link. |
| `Resources/install-worker.ps1:5,230` | Remove the ".NET 10 runtime" comment and the hint in the "exe not found" error. |
| `docs/architecture/05-user-capability-reference.md:303`, wiki `App-Worker.md`, `Getting-Started.md` | Remove the runtime note. |

Nothing else in the App changes. The download endpoint, the zip names, the install and update flow and the hub contract stay.

The .NET SDK stays a host prerequisite when a Workspace uses GitVersion, package restore or dependency-aware update (`HostPrerequisiteRequirements`). That is a requirement of those tools, not of the Worker, and the Host tab keeps its Dotnet row.

Updating an installed Worker needs no special step. The old Worker only starts PowerShell with the install script (`SelfUpdateCommand`), and the script empties `%ProgramFiles%\GrayMoon` before it extracts, so no file of the old build is left behind. The secret and the logs live in `%ProgramData%\GrayMoon` and are not touched.

### Self contained App

This part does not depend on the Worker work and applies to the Desktop bundle only. The Docker image keeps the framework-dependent App, because the runtime is in its base image.

Today `Publish-GrayMoonBundle.ps1` publishes the App and Desktop with `--self-contained false`, and `New-VelopackPackage.ps1` passes `--framework net10.0-x64-desktop,net10.0-x64-aspnetcore`, so the installer fetches both runtimes when they are missing.

Two forms, measured above:

| Form | Signed file holds the App's code | First-party files left unsigned | Download |
|---|---|---|---|
| Many files | No, `GrayMoon.App.exe` stays a launcher | `GrayMoon.App.dll`, `GrayMoon.Application.dll`, `GrayMoon.Common.dll`, `GrayMoon.Abstractions.dll` | about +50 MB |
| Single file | Yes, one 122 MB `GrayMoon.App.exe` | None | about +35 MB |

Both keep the signing list at three files plus `Setup.exe`. The single file form is the one that meets "the signed files hold the first-party code". Its single file publish raised no warning. What it needs:

- `wwwroot`, `appsettings.json` and `GrayMoon.App.staticwebassets.endpoints.json` stay loose files beside the exe; the worker zip stays under `worker\`.
- `WorkerEndpoints.cs:83` reads the worker zip from `ContentRootPath`, and the install scripts are read from assembly resources. Both work in a single file app; verify by running.
- `AboutEndpoints.cs:17` and `WorkerConnectionTracker.cs:36` read the version from the entry assembly; verify, because the Worker version check compares against it.
- ReadyToRun was off in the single file measurement. Turning it on makes the exe larger and the start faster; measure the App's start time before choosing.
- Trimming the App is not an option: Blazor Server and EF Core are not supported with trimming.

Changes in Desktop for either form: `Publish-GrayMoonBundle.ps1` (App publish flags), `New-VelopackPackage.ps1` (drop `net10.0-x64-aspnetcore` from `$PackFrameworks`), `Validate-Bundle.ps1` (expected files), `Generate-ThirdPartyNotices.ps1`. `BundleLocatorService` keeps finding `app\GrayMoon.App.exe`.

What stays: GrayMoon.Desktop is a WPF application and keeps needing the .NET Desktop runtime, which the installer still fetches. WPF cannot be compiled as Native AOT. Making Desktop self contained as well would put a second copy of the runtime into the bundle. So after this work the product still depends on one .NET runtime on a Desktop machine, and on none on a machine that runs only the Worker. See open decision 4.

## Verification

Unit tests run on the normal runtime and pass whether or not the AOT build works. These are needed in addition:

1. **Build gate** - the AOT publish has zero `IL` warnings, as errors, in the test workflow.
2. **Wire contract test** - for every request and response type, a filled sample serialized through the context equals the same sample serialized by reflection, and reads back equal. This runs as a normal unit test and is what guarantees the App does not see a difference.
3. **Convention tests** - every handler's types and every hub argument type are in the context (item 2); every type with `Success`, `WorkspaceId` or a label property implements the interface (item 3); every options property binds (item 7).
4. **Smoke test of the published exe** - a test that starts the AOT exe against a test hub and runs: `GetHostInfo`, one command per LibGit2Sharp use (item 5), one git CLI command, one hook POST to the listener, and a start with a config override. It checks the reported version. Per the rule "a fix ships with a regression test", the items above that can only fail at run time are covered here.
5. **Manual, on a machine with no .NET** - install from the Worker page; update from an installed framework-dependent Worker; every Windows service path of item 6; on Linux, install as a systemd unit.
6. **Performance** - compare start time, memory, and a status scan of a large Workspace with today's build. AOT starts faster; long-running hot paths can be slower because there is no run-time optimization. The Create Feature and Sync measurements in `feature-create-background-sync-cost.md` give the baseline.

## Open decisions

To be taken one at a time. Each lists the strict option first.

1. **LibGit2Sharp** - keeping 0.32.0 unchanged is ruled out by the test in item 5. What is left:
   - Replace its uses with the git CLI before the migration. The Worker becomes one file and the dependency and its native dll go. The catch: the in-process readers exist because they are faster than starting git, so the Sync and status measurements have to be repeated.
   - Build LibGit2Sharp from a patched source (`[In, Out]` on the `GitBuf` parameters) until upstream releases a complete fix. Tested: it passes every check (item 5), and it keeps the in-process speed. The catch: GrayMoon builds and hosts its own package of the library (a fork repository, a feed, and the same pinned `LibGit2Sharp.NativeBinaries`), and has to rebase the one-file patch on each upstream release until it can be dropped. The upstream pull request as it stands is not enough.
   - Keep the package and avoid the 24 methods (read the upstream from `branch.<name>.remote` and `.merge`, pass the global and system config files to LibGit2Sharp by path). The catch: it is a workaround, and a later call to one of the 24 fails silently again.
   
   In every case `dev/Test-LibGit2SharpAot.ps1` is the gate: it must end with "PROVED". Decided 2026-11-27: the patched build, because speed decides, until upstream releases a complete fix.

   Decided 2026-11-27: the patched package is published to nuget.org under its own id, `GrayMoon.LibGit2Sharp`, from a fork of `libgit2/libgit2sharp`. A private GitHub Packages feed was ruled out: it needs a token for every restore (the test workflow, the Docker build, the Desktop build and release, each developer machine), and nobody else could build this public repository. With nuget.org no build or release step changes; only the Worker's `PackageReference` does.

   - The fork carries `worker-native-aot-libgit2sharp-fork.patch` (next to this note) on top of upstream commit `eaa698d` (0.32.0): `[In, Out]` on the 25 `GitBuf` parameters, the package id, and a `publish` job in `.github/workflows/ci.yml` that pushes the package to nuget.org when a tag is pushed and all test jobs pass. The assembly name, namespaces and the `LibGit2Sharp.NativeBinaries` dependency are unchanged, so no Worker code changes.
   - The version comes from the tag (MinVer). The first one is `0.32.0-aot.1`: the upstream version it is built from, plus a counter.
   - Tested on 2026-11-27 with a locally built `GrayMoon.LibGit2Sharp` 0.32.0-aot.1 and the Worker pointed at it: the script ends with "PROVED" (188 of 188 checks on the normal runtime, as AOT, and in the install layout).
   - After the package is on nuget.org: change the Worker's `PackageReference` to `GrayMoon.LibGit2Sharp`, change the override in `GrayMoon.Worker.AotProbe.csproj` to the same id, run the script, and add it to `test.yml` on a Windows runner.
   - When upstream releases a complete fix: point the reference back to `LibGit2Sharp`, run the script, archive the fork and delete the patch file.
2. **Windows worker zip in the Docker image** - build it on a Windows runner and pass it into the image build, or stop shipping it in the image. Recommendation: build on Windows; the catch is that the image build then depends on a second job.
3. **Linux Worker** - AOT as well (one way to build the Worker; needs `clang` in the build stage and sets a glibc floor for hosts) or leave Linux framework-dependent (no floor; two ways to build, and Linux hosts keep the runtime requirement). Recommendation: AOT for both.
4. **App: one file or many, and Desktop** - single file (the signature covers the App's code, about +35 MB) or many files (about +50 MB, first-party DLLs stay unsigned); and whether Desktop keeps the .NET Desktop runtime prerequisite. Recommendation: single file, and Desktop stays framework-dependent.
5. **`InvariantGlobalization`** - on (no ICU needed on Linux; any future culture-aware code behaves as the invariant culture) or off. Recommendation: on.
6. **dotnet tool packaging** - remove `PackAsTool` and the two related properties, since nothing uses them, or keep a tool package beside the AOT exe. Recommendation: remove.

## Order of work

1. Decisions 1, 3, 5 and 6, which shape the Worker project.
2. Items 1 to 4 and the tests 2 and 3 of "Verification", on the current framework-dependent build. Each is correct and testable without AOT, and the wire contract test protects the change.
3. Turn on `PublishAot` and `IsAotCompatible`; reach zero warnings; add the build gate.
4. Run the exe: items 5 to 8, then the smoke test.
5. Items 9 and 10, size options, performance comparison.
6. Decision 2, then the Dockerfile, the `PublishWorkerBundle` target, the Desktop scripts.
7. App texts, the install script, the documents below.
8. Decision 4 and the self contained App, which does not depend on steps 1 to 7 except for the package size.

## Documents to update with the change

- `GrayMoon/docs/architecture/02-system-architecture.md` (how the Worker is built and what the host needs), `05-user-capability-reference.md` (Worker page), `06-developer-extension-guide.md` (a new command adds its request and response types to the JSON context; the property interfaces).
- `GrayMoon/AGENTS.md`, section "Worker: input, processes and git": the rule that Worker JSON goes through the context and that no code reads a property by name.
- `GrayMoon.Desktop/docs/architecture/03-build-bundle-updates-and-distribution.md` (bundle layout, runtime prerequisites, signing).
- `GrayMoon/README.md`, the wiki pages `App-Worker.md` and `Getting-Started.md`.
- `GrayMoon.Desktop/README.md`, one bullet under "Recent GrayMoon changes" when the switch ships.
