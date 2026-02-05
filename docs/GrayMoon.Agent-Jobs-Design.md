# GrayMoon.Agent — Jobs: Separate Services, Interfaces & JSON Handling

## 1. Goals

- **Separate interface** for Jobs: clear contract per job kind (command vs notify) and per command type.
- **Separate services** for handling: one handler service per job type (or per command), not a single `JobBackgroundService` switch.
- **Better JSON handling**: typed request/response DTOs; deserialize at the edge, pass strongly-typed objects through the pipeline.
- **Proper code separation**: queue and processor stay generic; job-specific logic lives in dedicated handlers and models.

---

## 2. Job Interface & Model Separation

### 2.1 Base job abstraction

All enqueued work is represented as a **job**. Two kinds:

- **Command jobs** — originate from SignalR `RequestCommand`; have a request ID and require a `ResponseCommand` back.
- **Notify jobs** — originate from HTTP `/notify`; no request ID; agent pushes `SyncCommand` to the app.

Introduce a small type hierarchy and shared model location:

```
GrayMoon.Agent/
├── Jobs/
│   ├── IJob.cs                    # Marker or base for all jobs
│   ├── ICommandJob.cs             # Command name + typed args + request ID
│   ├── INotifyJob.cs              # NotifySync: workspace/repo/path
│   ├── JobEnvelope.cs             # Queue payload: discriminator + one of the job types
│   └── (optional) JobKind.cs      # Enum: Command | Notify
├── Jobs/
│   └── Requests/                  # Typed args per command (replaces JsonElement)
│       ├── SyncRepositoryRequest.cs
│       ├── RefreshRepositoryVersionRequest.cs
│       ├── EnsureWorkspaceRequest.cs
│       ├── GetWorkspaceRepositoriesRequest.cs
│       ├── GetRepositoryVersionRequest.cs
│       └── GetWorkspaceExistsRequest.cs
├── Jobs/
│   └── Response/                  # Typed response DTOs per command (optional but recommended)
│       ├── SyncRepositoryResponse.cs
│       ├── RefreshRepositoryVersionResponse.cs
│       └── ...
```

- **`IJob`**  
  - Marker interface (or abstract class) for “something that can be enqueued and executed.”  
  - No `JsonElement` here; only typed data.

- **`ICommandJob`**  
  - Properties: `RequestId`, `Command` (string), and a **typed args object** (e.g. `SyncRepositoryRequest`).  
  - Implementations can be one type per command, e.g. `SyncRepositoryJob : ICommandJob` with `Args` as `SyncRepositoryRequest`.

- **`INotifyJob`**  
  - Properties: `RepositoryId`, `WorkspaceId`, `RepositoryPath` (and any other notify-only data).  
  - Single implementation today: NotifySync.

- **`JobEnvelope`**  
  - What the queue actually carries: a discriminator (e.g. `JobKind` or type name) plus one concrete job (e.g. `ICommandJob` or `INotifyJob`).  
  - Ensures the queue and processor stay generic; all “what is this job?” logic is in the envelope and handlers.

This gives a **separate interface** for jobs and keeps **code separation**: queue and worker loop do not depend on `JsonElement` or command-specific parsing.

### 2.2 Typed request/response (replace JsonElement)

- **Requests**  
  - For each command, define a DTO in `Jobs/Requests/`, e.g. `SyncRepositoryRequest` with `WorkspaceName`, `RepositoryId`, `RepositoryName`, `CloneUrl`, `BearerToken`, `WorkspaceId`.  
  - Same for `RefreshRepositoryVersionRequest`, `EnsureWorkspaceRequest`, etc.  
  - Use `System.Text.Json` attributes where needed (`[JsonPropertyName]`, etc.).

- **Responses**  
  - Define DTOs in `Jobs/Response/` for each command that returns data, e.g. `SyncRepositoryResponse` (`Version`, `Branch`, `Projects`).  
  - Handlers return these types; the response sender serializes them to JSON for `ResponseCommand(requestId, success, data, error)`.

- **JSON handling**  
  - **At the edge only**: when receiving `RequestCommand(requestId, command, args)`, the Agent deserializes `args` (e.g. `JsonElement` or string) into the correct request type for that `command`.  
  - After that, the entire pipeline (queue → handler → response) uses only typed objects.  
  - No `GetString(args, "workspaceName")` inside handlers; only properties on request DTOs.

This is the **better JSON handling**: one deserialization at the boundary, strong types everywhere else.

---

## 3. Separate Services for Handling

### 3.1 Handler interfaces

One **handler** per command (and one for notify):

- `ICommandHandler<TRequest, TResponse>`  
  - `Task<TResponse> ExecuteAsync(TRequest request, CancellationToken cancellationToken)`  
  - Implementations: `SyncRepositoryHandler`, `RefreshRepositoryVersionHandler`, etc., each taking the corresponding request/response types.

- `INotifySyncHandler`  
  - `Task ExecuteAsync(INotifyJob payload, CancellationToken cancellationToken)`  
  - Single implementation: runs GitVersion and invokes `SyncCommand` on the hub.

Handlers do **not** receive `JsonElement` or raw JSON; they receive only the typed request/notify payload.

### 3.2 Handler registration and resolution

- Register each handler in DI, e.g.:
  - `ICommandHandler<SyncRepositoryRequest, SyncRepositoryResponse>` → `SyncRepositoryHandler`
  - `ICommandHandler<RefreshRepositoryVersionRequest, RefreshRepositoryVersionResponse>` → `RefreshRepositoryVersionHandler`
  - etc.

- **Command handler resolution**  
  - By command name (e.g. `"SyncRepository"` → `ICommandHandler<SyncRepositoryRequest, SyncRepositoryResponse>`).  
  - Options:
    - **Option A**: A small registry that maps `command` string → handler (or handler type). The processor looks up the handler, deserializes args to the handler’s request type, calls `ExecuteAsync`, then serializes the result for `ResponseCommand`.
    - **Option B**: Each handler is registered with its command name; a single `IJobHandlerResolver` or `ICommandHandlerFactory` returns the right handler for a given `ICommandJob` (or command name + args type).

- **Notify handler**  
  - Single implementation; processor sees `JobKind.Notify` (or `INotifyJob`) and calls `INotifySyncHandler.ExecuteAsync`.

So **separate services** = one service (class) per command/notify type, behind interfaces, with resolution by command name or job type.

### 3.3 Processor role (slim)

- **JobBackgroundService** (or `JobQueueProcessor`) remains a **generic worker**:
  - Reads from the queue (`JobEnvelope`).
  - If command job: resolve handler by command name, call handler with typed request, send `ResponseCommand` with typed response (or serialized response).
  - If notify job: call `INotifySyncHandler.ExecuteAsync`.
  - No switch on command strings; no `JsonElement` parsing. All command-specific logic lives in the handler services.

This keeps **proper code separation**: pipeline vs. command-specific logic.

---

## 4. Data Flow (end-to-end)

### 4.1 Command flow (SignalR → Queue → Handler → Response)

1. **SignalR** receives `RequestCommand(requestId, command, args)` (args as `JsonElement` or string).
2. **Command job factory** (or “command enqueue” service):
   - Maps `command` → request type (e.g. `"SyncRepository"` → `SyncRepositoryRequest`).
   - Deserializes `args` into that request type (single place for JSON → DTO).
   - Builds an `ICommandJob` (or `JobEnvelope` containing it) with `RequestId`, `Command`, and the typed request.
   - Enqueues to `IJobQueue`.
3. **JobBackgroundService** dequeues `JobEnvelope`.
4. For command jobs: **resolve handler** by command → get `ICommandHandler<TReq, TRes>` → run `ExecuteAsync(request)` → get typed response.
5. **Response sender**: serializes response to JSON (if needed), calls hub `ResponseCommand(requestId, success, data, error)`.

All JSON for commands is at step 2 (in) and step 5 (out). Handlers only see typed DTOs.

### 4.2 Notify flow (/notify → Queue → Handler → SyncCommand)

1. **HookListenerHostedService** receives POST `/notify` with JSON body.
2. Deserializes body to `NotifyPayload` (existing or aligned with `INotifyJob`).
3. Builds `INotifyJob` (or `JobEnvelope` with notify payload) and enqueues.
4. **JobBackgroundService** dequeues, sees notify job, calls `INotifySyncHandler.ExecuteAsync`.
5. Handler runs GitVersion, then invokes hub `SyncCommand(workspaceId, repositoryId, version, branch)`.

No `JsonElement` in the notify path either; only typed models.

---

## 5. Recommended Folder & File Layout

```
GrayMoon.Agent/
├── Jobs/
│   ├── IJob.cs
│   ├── ICommandJob.cs
│   ├── INotifyJob.cs
│   ├── JobEnvelope.cs
│   ├── JobKind.cs
│   ├── Requests/
│   │   ├── SyncRepositoryRequest.cs
│   │   ├── RefreshRepositoryVersionRequest.cs
│   │   ├── EnsureWorkspaceRequest.cs
│   │   ├── GetWorkspaceRepositoriesRequest.cs
│   │   ├── GetRepositoryVersionRequest.cs
│   │   └── GetWorkspaceExistsRequest.cs
│   └── Response/
│       ├── SyncRepositoryResponse.cs
│       ├── RefreshRepositoryVersionResponse.cs
│       ├── EnsureWorkspaceResponse.cs
│       ├── GetWorkspaceRepositoriesResponse.cs
│       ├── GetRepositoryVersionResponse.cs
│       └── GetWorkspaceExistsResponse.cs
├── Commands/
│   ├── ICommandHandler.cs          # ICommandHandler<TRequest, TResponse>
│   ├── INotifySyncHandler.cs
│   ├── SyncRepositoryHandler.cs
│   ├── RefreshRepositoryVersionHandler.cs
│   ├── EnsureWorkspaceHandler.cs
│   ├── GetWorkspaceRepositoriesHandler.cs
│   ├── GetRepositoryVersionHandler.cs
│   ├── GetWorkspaceExistsHandler.cs
│   └── NotifySyncHandler.cs
├── Services/                        # or "Resolution"
│   ├── ICommandHandlerResolver.cs  # Resolves by command name → handler
│   ├── CommandHandlerResolver.cs
│   └── CommandJobFactory.cs        # RequestCommand → typed ICommandJob (JSON → DTO here)
├── Queue/
│   ├── IJobQueue.cs                # Enqueue(JobEnvelope), ReadAllAsync()
│   └── JobQueue.cs
├── Hosted/
│   ├── SignalRConnectionHostedService.cs  # Uses CommandJobFactory to enqueue typed jobs
│   ├── HookListenerHostedService.cs       # Builds INotifyJob from NotifyPayload
│   ├── JobBackgroundService.cs    # Slim: dequeue → resolve handler → execute → send response
│   └── (optional) ResponseSender.cs       # Sends ResponseCommand with serialized response
└── Models/
    └── GitVersionResult.cs         # Keep; used by handlers
```

- **Queue** carries `JobEnvelope` only (or a type that can hold either `ICommandJob` or `INotifyJob`).
- **JobBackgroundService** and **CommandJobFactory** are the only places that need to know command names and request types for deserialization; handlers stay purely typed.

---

## 6. JSON Conventions

- Use **System.Text.Json** consistently.
- **Serialization options**: shared `JsonSerializerOptions` (e.g. property name policy, ignore nulls) in one place (e.g. `AgentJsonOptions`) and reuse when deserializing incoming args and when serializing response `data` for `ResponseCommand`.
- **Errors**: keep `ResponseCommand(requestId, success, data, error)`; on failure, `success: false`, `data: null`, `error` with message. No JSON in `error` required; string is enough.
- **Backward compatibility**: typed request property names should match current app payloads (e.g. `workspaceName`, `repositoryId`, `repositoryName`, `cloneUrl`, `bearerToken`, `workspaceId`) so the existing App does not need to change.

---

## 7. Summary

| Aspect | Current | After |
|--------|---------|--------|
| Job representation | Single `QueuedJob` with `Command` + `JsonElement?` + notify fields | `IJob` / `ICommandJob` / `INotifyJob`, queue carries `JobEnvelope` with typed payloads |
| JSON | Parsed inside `JobBackgroundService` via `GetString`/`GetInt` on `JsonElement` | Deserialize once at edge to typed Requests; handlers use DTOs only |
| Commands | One big `JobBackgroundService` with a switch and private methods | One handler service per command + `INotifySyncHandler` in `Commands/`; `JobBackgroundService` in `Hosted/` resolves and invokes |
| Code separation | All logic in `JobBackgroundService.cs` | Command handlers in `Commands/`, requests/responses in `Jobs/Requests` and `Jobs/Response`, resolution in `Services/`, `JobBackgroundService` in `Hosted/` |

This design gives you **separate interfaces** for jobs, **separate services** for each job type, **better JSON handling** via typed DTOs at the boundary, and **clear code separation** between queue, resolution, and domain logic.
