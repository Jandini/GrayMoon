namespace GrayMoon.Agent.Commands;

/// <summary>
/// Handler for a single command type; receives typed request and returns typed result.
/// </summary>
public interface ICommandHandler<in TRequest, TResult>
{
    Task<TResult> ExecuteAsync(TRequest request, CancellationToken cancellationToken = default);
}
