using GrayMoon.Abstractions.Worker;
using GrayMoon.Worker.Jobs;
using GrayMoon.Worker.Jobs.Requests;

namespace GrayMoon.Worker.Hosted;

/// <summary>Resolves a short label for overlay terminal prefixes (repo, workspace, or command name).</summary>
internal static class CommandJobStreamLabel
{
    public static string? Resolve(string command, object request)
    {
        // Its output is the CodeGraph build's, not the watch command's.
        if (command == WorkerHubMethods.WatchCodeGraphInit)
            return "codegraph";

        // The provider CLI's own name (claude, codex, cursor), not the command name.
        if (command == WorkerHubMethods.GenerateAiText && request is GenerateAiTextRequest { ProviderId: var provider } && !string.IsNullOrWhiteSpace(provider))
            return provider.Trim();

        if (request is IRepositoryNamedRequest { RepositoryName: var repo } && !string.IsNullOrWhiteSpace(repo))
            return repo.Trim();

        if (request is IWorkspaceNamedRequest { WorkspaceName: var ws } && !string.IsNullOrWhiteSpace(ws))
            return ws.Trim();

        return string.IsNullOrWhiteSpace(command) ? null : command.Trim();
    }
}
