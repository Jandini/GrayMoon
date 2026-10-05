---
name: add-worker-command
description: Step-by-step recipe for adding a new GrayMoon.Worker command (ICommandHandler) end-to-end, from request/response DTOs through hub registration.
---

# Adding a new Worker command

Each Worker operation implements `ICommandHandler<TRequest, TResponse>` in `src/GrayMoon.Worker/Commands/`. Commands arrive over SignalR (`/hub/worker`) and are dispatched by name; `System.CommandLine` is only used for the Worker CLI verbs (`run`, `install`, ...), not for commands.

To add a new command:

1. Define sealed request/response DTOs in `src/GrayMoon.Worker/Jobs/Requests/` and `src/GrayMoon.Worker/Jobs/Response/` with explicit `[JsonPropertyName]` names.
2. Create the handler class in `src/GrayMoon.Worker/Commands/`.
3. Register it via `AddSingleton<ICommandHandler<TRequest, TResponse>, YourCommand>()` in `Cli/Handlers/RunCommandHandler.cs`.
4. Add it to the executor dictionary in `Services/CommandDispatcher.cs` (constructor parameter plus dictionary entry).
5. Add a `DeserializeRequest` case in `Services/CommandJobFactory.cs`. Without it the request payload is not deserialized.
6. Add the name constant to `GrayMoon.Abstractions/Worker/WorkerHubMethods.cs` (many older commands use string literals; prefer the constant).
7. If the command is a fast read or a diff, add its name to `ReadOnlyCommands` or `DiffCommands` in `Hosted/SignalRConnectionHostedService.cs`; otherwise it runs in the main pool.
8. Call it from the App via `IWorkerBridge.SendCommandAsync(name, args, ct)` and deserialize `WorkerCommandResponse.Data` into an App-side response type.
9. Add a test in `src/GrayMoon.Worker.Tests/`.
