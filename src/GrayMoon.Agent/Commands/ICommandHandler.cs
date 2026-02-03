namespace GrayMoon.Agent.Commands;

/// <summary>
/// Handler for a single command type; receives typed request and returns typed response.
/// </summary>
public interface ICommandHandler<in TRequest, TResponse>
{
    Task<TResponse> ExecuteAsync(TRequest request, CancellationToken cancellationToken = default);
}
