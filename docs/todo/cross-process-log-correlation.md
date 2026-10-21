# Cross-process log correlation (App ↔ Worker)

## Status

Proposal only. Nothing implemented.

## Short answer

GrayMoon already has a solid hop-level correlation key: the SignalR `requestId` on every `RequestCommand` / `ResponseCommand` / `CommandOutput` / `CancelCommand`. The Worker already pushes it into Serilog as `GrayMoonRequestId` for the duration of a command. The App does not put that id in LogContext, file templates omit LogContext properties, and multi-command user operations (Push, Update, Sync) use separate short `RunId` / per-repo `OperationId` values that never appear in Worker logs. Diagnosis today means matching timestamps across two files and hoping command names line up.

The solid fix is to **make the existing `requestId` the shared search key in both processes**, add an optional **parent operation id** on the App (and, if needed, on the wire in the same App+Worker release), and **stop inventing sibling ids that do not join**. No OpenTelemetry stack, no extra happy-path Information volume, no argument dumps.

---

## 1. What exists today

### 1.1 Wire and queues

```text
UI / service
  → WorkerBridge.SendCommandAsync
      requestId = Guid.NewGuid().ToString("N")
      WorkerResponseDelivery.WaitAsync(requestId, …)
      RequestCommand(requestId, command, args)
  → Worker SignalRConnectionHostedService
      enqueue ICommandJob { RequestId, Command, Request }
  → JobBackgroundService.ProcessCommandAsync
      LogContext.PushProperty("GrayMoonRequestId", requestId)
      execute handler (git / CLI / etc.)
      ResponseCommand(requestId, …)   // Information: completed + timings
  → WorkerHub.ResponseCommand → WorkerResponseDelivery.Complete
```

Architecture already states that a unique request id correlates response and caller (`docs/architecture/04-runtime-communication-and-concurrency.md` §1).

### 1.2 Logging stack

| Process | Files | Enrichment | File template includes properties? |
|---|---|---|---|
| App | `%ProgramData%\GrayMoon\Logs\graymoon-app-*.log` | `FromLogContext` | **No** – message + exception only |
| Worker | `%ProgramData%\GrayMoon\logs\graymoon-worker-*.log` | `FromLogContext`, `MachineName` | **No** – message + exception only |
| Desktop | Desktop log dir | — | Message only |

Worker `MinimumLevel` is hardcoded `Debug`. App reads Serilog from config (`GrayMoon.App` can be `Verbose` / `Trace` in `appsettings.json`).

### 1.3 Where `requestId` already appears in messages

| Side | When | Level |
|---|---|---|
| App `WorkerBridge` | Sent | Debug |
| App `WorkerBridge` | Timeout | Warning (includes id) |
| App `WorkerBridge` | Send failure | Error (command name; id only if Fail path) |
| App `WorkerHub` | `CommandOutput` / `ResponseCommand` handler faults | Error |
| Worker receive | `RequestCommand … received` | Debug |
| Worker complete | `ResponseCommand {RequestId} completed …` | **Information** |
| Worker cancel / fail paths | various | Debug / Information / Error |

Nested Worker work (`CommandLineService`, `BoundedProcessRunner`, handlers) runs under `LogContext` with `GrayMoonRequestId`, but **file lines never print that property**, so a git failure mid-command is not greppable by id unless the message text happens to include it.

`GrayMoonRequestId` is also used by `OverlayStreamSerilogSink` so Error/Fatal events during a command reach the in-app overlay. That path must keep working.

### 1.4 Parallel “correlation” that does not cross the process boundary

| Name | Where | Joins Worker? |
|---|---|---|
| Push / Update `RunId` | Often `Guid.N[..8]` from pages; optional; can be null | No |
| Sync per-repo `OperationId` | App Debug only around `SendCommandAsync` | No – and it is **not** the hub `requestId` |
| Desktop install / Host install | Guid N between App JS bridge and Desktop | Separate channel (fine; keep distinct) |

Push package-wait diagnostics are strong **inside the App** (`[PushOrchestrator {RunId}] …`) but stop at the WorkerBridge edge.

### 1.5 Hook-driven Worker work

`NotifySync`, watcher Git Changes snapshots, etc. are not App `RequestCommand`s. They already log repo / workspace identifiers. They do not need App↔Worker request correlation; they need stable Worker-local identifiers only if a single hook fans out (out of scope for the first cut).

---

## 2. Gaps that hurt diagnosis

1. **App never LogContexts `GrayMoonRequestId`**, so App logs during an await (orchestration, package wait, errors in callers) do not carry the hop id unless each site repeats it.
2. **File templates drop LogContext**, so the Worker’s existing push is invisible in `graymoon-worker-*.log` for nested lines.
3. **Success path on the App does not record the id at a durable level** – only Debug “Sent”. In Production Desktop, Default is Information, so App→Worker hops leave almost no App-side breadcrumb unless they time out.
4. **Parent operations are inconsistent** – short `RunId`, nullable, or a Sync `OperationId` that is a third Guid next to the real `requestId`.
5. **Two log folder spellings** (`Logs` vs `logs`) invite confusion in docs and scripts (Windows usually folds them; other hosts would not).

---

## 3. Goals and non-goals

### Goals

- One greppable id for each App↔Worker command hop in **both** log files.
- One parent id for a user-facing mutation that issues many Worker commands, so App logs group those hops.
- Failures and timeouts remain the loudest signal; happy path stays cheap.
- Same App+Worker product version (already required for normal commands).

### Non-goals / rejected

| Idea | Why not |
|---|---|
| Second hop Guid alongside `requestId` | Duplicate source of truth; drift under cancel/timeout |
| Full OpenTelemetry / Activity / exporters | Heavy; wrong for local single-user product; does not match current Serilog files |
| Logging command args / tokens / stdout at Information | Secrets, size, I/O; already Trace on Worker for args |
| Raising default MinimumLevel or shipping `GrayMoon.App: Verbose` | Slows disks and masks signal |
| Changing command DTO shapes to smuggle ids | Couples every handler; version risk for no gain over LogContext |
| Correlating Desktop install with Worker commands via one id | Different lifetimes and processes; keep Desktop request ids separate |

---

## 4. Proposed design

### 4.1 Canonical names (shared constants)

Put property names in one place (e.g. `GrayMoon.Abstractions` or `GrayMoon.Common`), used by App and Worker:

| Property | Meaning | Lifetime |
|---|---|---|
| `GrayMoonRequestId` | Hub `requestId` (already) | One `SendCommandAsync` / one Worker command job |
| `GrayMoonOperationId` | Parent user/system operation | One workspace mutation / push run / sync batch |

Keep the string `GrayMoonRequestId` unchanged so `OverlayStreamSerilogSink` stays correct.

### 4.2 Hop correlation (must)

**App `WorkerBridge.SendCommandAsync`**

1. Generate `requestId` as today.
2. `using (LogContext.PushProperty(GrayMoonRequestId, requestId))` for the whole send/wait.
3. If an ambient `GrayMoonOperationId` is set, push that too (see §4.3).
4. Lifecycle messages always include `{RequestId}` and `{Command}`:
   - **Debug**: sent; completed success (add elapsed ms – mirror Worker’s timing habit without promoting volume).
   - **Warning / Error**: timeout, cancel notify, transport failure, or `response.Success == false` (log error string; still no args dump).

Callers do not need to know the id. Sync’s hand-rolled per-repo `OperationId` becomes redundant once bridge logs carry `RequestId`; remove or demote that parallel id so operators are not grepping the wrong Guid.

**Worker**

Already pushes `GrayMoonRequestId` for the execute scope. Keep that. Optionally push `GrayMoonOperationId` when present on the job (§4.4).

**File templates (App, Worker, ideally Desktop)**

Append a compact, fixed suffix so properties survive the file sink without `{Properties:j}` noise:

```text
… {Message:lj} {GrayMoonRequestId}{NewLine}{Exception}
```

Or a clearer fixed form, e.g. `req={GrayMoonRequestId}` / `op={GrayMoonOperationId}`, accepting blank when unset. Prefer explicit property tokens over dumping all properties.

This is the change that makes nested Worker DEBUG/Information lines greppable by the same id as `ResponseCommand … completed`.

### 4.3 Parent operation id (App-first)

**Ambient scope** (AsyncLocal + `LogContext.PushProperty`), set once at the edge of a mutation:

- `WorkspaceOperationRunner.RunAsync` – allocate `Guid.NewGuid().ToString("N")` when an operation starts; push for the whole body (covers overlay jobs that call the Worker many times).
- Push / Update – **reuse** that id as today’s `RunId` (or set `RunId` from it). Stop truncating to 8 hex chars at the page; short ids collide under concurrent workspaces and complicate greps. If UI must show a short label, display a prefix; logs keep the full id.
- Ensure a `RunId` is always assigned inside the push pipeline when callers pass null (one place, not every page).

Diagnosis workflow without any wire change:

```text
1. Grep App log for GrayMoonOperationId / [PushOrchestrator {RunId}]
2. Within that window, collect GrayMoonRequestId values from WorkerBridge lines
3. Grep Worker log for those request ids
```

That is enough for almost all support cases and costs only AsyncLocal + existing message fields.

### 4.4 Optional: carry OperationId on the wire (same release)

Only if we want **Worker-only** greps to group a push’s many commands without opening the App log.

In the same App+Worker ship (version lock already blocks mismatched normal commands):

- Extend `RequestCommand` with an optional `string? operationId` (or a tiny enrichment bag). Default null for any leftover edge.
- Worker stores it on `ICommandJob`, pushes `GrayMoonOperationId` beside `GrayMoonRequestId` in `ProcessCommandAsync`.

Do **not** put this inside command JSON args. Do **not** require handlers to forward it.

If this step is deferred, §4.2–4.3 still stand alone.

### 4.5 Log directory and docs

- One canonical directory spelling under `%ProgramData%\GrayMoon\Logs` for App and Worker (align Worker create path with App).
- Document the two ids and the grep workflow in `docs/architecture/04-runtime-communication-and-concurrency.md` (short subsection under §1) and point support notes there.
- Document that Desktop native-bridge request ids are **not** `GrayMoonRequestId`.

### 4.6 Volume and performance rules (hard)

- No new Information logs on the high-frequency read/diff path for success.
- Do not log request/response bodies except existing Trace.
- `LogContext.PushProperty` only; no per-line string concatenation of ids in hot CommandLineService paths (template + property is enough).
- Do not enable OpenTelemetry, Activity listeners, or remote sinks in this work.
- Production Desktop must not ship `GrayMoon.App` at Verbose/Trace by default; keep that for local debugging only.

Expected cost: nanoseconds per command for AsyncLocal, a few dozen bytes per log line for the id suffix, no extra SignalR payloads unless §4.4 is taken (one optional string).

---

## 5. Suggested rollout

| Step | Change | Risk |
|---|---|---|
| 1 | Shared property name constants; App LogContext in `WorkerBridge`; failure/timeout messages already good – add Debug complete + failed Information/Warning with id | Low |
| 2 | File output templates include `GrayMoonRequestId` (and OperationId when set) on App + Worker | Low – verify overlay sink still reads properties from the event, not the rendered text |
| 3 | `WorkspaceOperationRunner` (and push `RunId` always-on, full Guid) + ambient OperationId | Low–medium (touch orchestration entry) |
| 4 | Remove / stop advertising Sync’s parallel OperationId; bridge RequestId is the hop id | Low |
| 5 | Unify casing + architecture note | None |
| 6 | (Optional) Wire OperationId on `RequestCommand` | Medium – coordinated release only |

Tests: bridge unit tests that a failed response logs RequestId; Worker test that LogContext property is present on a handler log event (existing capturing logger patterns); no perf benchmarks required if volume rules are kept.

---

## 6. Where architecture is silent

`04-runtime-communication-and-concurrency.md` describes `requestId` for response delivery, not for Serilog. `06-developer-extension-guide.md` requires secret redaction, not correlation. Neither documents `GrayMoonRequestId`, LogContext, or parent operation ids. After implementation, §1 of `04-…` should record the two properties and the grep workflow; this todo can then be closed or reduced to “done”.

---

## 7. Why this is not a workaround

- Reuses the id the product already generates and requires for correctness (cancel, stream, complete).
- Fixes the real break: enrichment without rendering, and App without LogContext.
- Parent id is a normal causal scope (one mutation → many hops), not a second competing hop id.
- Explicitly refuses volume, telemetry stacks, and DTO smuggling that would slow or complicate GrayMoon.
