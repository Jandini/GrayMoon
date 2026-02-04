namespace GrayMoon.Agent.Abstractions;

/// <summary>
/// Dispatches a command by name to the appropriate handler and returns the response as object.
/// </summary>
public interface ICommandDispatcher
{
    Task<object?> ExecuteAsync(string commandName, object request, CancellationToken cancellationToken = default);
}
