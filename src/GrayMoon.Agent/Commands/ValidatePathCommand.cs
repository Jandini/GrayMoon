using GrayMoon.Agent.Abstractions;
using GrayMoon.Agent.Jobs.Requests;
using GrayMoon.Agent.Jobs.Response;

namespace GrayMoon.Agent.Commands;

public sealed class ValidatePathCommand : ICommandHandler<ValidatePathRequest, ValidatePathResponse>
{
    public Task<ValidatePathResponse> ExecuteAsync(ValidatePathRequest request, CancellationToken cancellationToken = default)
    {
        var path = request.Path;

        if (string.IsNullOrWhiteSpace(path))
            return Task.FromResult(new ValidatePathResponse { IsValid = false, ErrorMessage = "Path is required." });

        try
        {
            // Validates path syntax (throws on invalid chars, relative paths, etc.)
            var fullPath = Path.GetFullPath(path);

            // Try to create the directory if it doesn't exist — leave it in place once created
            Directory.CreateDirectory(fullPath);

            return Task.FromResult(new ValidatePathResponse { IsValid = true });
        }
        catch (Exception ex)
        {
            return Task.FromResult(new ValidatePathResponse { IsValid = false, ErrorMessage = ex.Message });
        }
    }
}
