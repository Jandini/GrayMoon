namespace GrayMoon.Agent.Services;

/// <summary>
/// Resolves and executes the command handler for a given command name with typed request; returns result as object.
/// </summary>
public interface ICommandHandlerResolver
{
    Task<object?> ExecuteAsync(string commandName, object request, CancellationToken cancellationToken = default);
}
