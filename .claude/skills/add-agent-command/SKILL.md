---
name: add-agent-command
description: Step-by-step recipe for adding a new GrayMoon.Agent command (ICommandHandler) end-to-end, from request/response DTOs through hub registration.
---

# Adding a new Agent command

The Agent uses `System.CommandLine`. Each agent operation implements `ICommandHandler<TRequest, TResponse>` in `src/GrayMoon.Agent/Commands/`.

To add a new command:

1. Define request/response DTOs.
2. Create the handler class.
3. Register it via `AddSingleton<ICommandHandler<TRequest, TResponse>, YourCommand>()` in `RunCommandHandler.cs`.
4. Add it to the dispatcher dictionary in `CommandDispatcher.cs`.
5. Add the hub method constant to `AgentHubMethods.cs`.
6. Call via `AgentBridge.SendCommandAsync` on the App side.
