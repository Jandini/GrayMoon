using System.Reflection;

namespace GrayMoon.Agent.Hosted;

/// <summary>Resolves a short label for overlay terminal prefixes (repo, workspace, or command name).</summary>
internal static class CommandJobStreamLabel
{
    public static string? Resolve(string command, object request)
    {
        if (TryStringProperty(request, "RepositoryName", out var repo) && !string.IsNullOrWhiteSpace(repo))
            return repo.Trim();

        if (TryStringProperty(request, "WorkspaceName", out var ws) && !string.IsNullOrWhiteSpace(ws))
            return ws.Trim();

        return string.IsNullOrWhiteSpace(command) ? null : command.Trim();
    }

    private static bool TryStringProperty(object request, string propertyName, out string? value)
    {
        value = null;
        var prop = request.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
        if (prop?.PropertyType != typeof(string))
            return false;

        value = prop.GetValue(request) as string;
        return true;
    }
}
