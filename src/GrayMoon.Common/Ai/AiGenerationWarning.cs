using System.Text.Json.Serialization;

namespace GrayMoon.Common.Ai;

/// <summary>
/// Stable codes for a successful AI generation that still needs the user's attention. Not an
/// <see cref="AiGenerationErrorKind"/>: the text was accepted after validation.
/// </summary>
public static class AiGenerationWarningCodes
{
    /// <summary>
    /// The provider CLI returned a verified successful result envelope and usable JSON, then exited with a nonzero code.
    /// </summary>
    public const string ProviderExitedAfterResult = "ProviderExitedAfterResult";

    /// <summary>
    /// The first run failed for a reason an environment variable fixes; GrayMoon added that variable and the second run
    /// answered. Raised by the App, never by the Worker.
    /// </summary>
    public const string EnvironmentVariableAdded = "EnvironmentVariableAdded";
}

/// <summary>
/// A non-fatal notice attached to validated generated text. Safe to show in the UI: no prompts, diffs, stderr or paths.
/// </summary>
public sealed record AiGenerationWarning(
    string Code,
    string Message,
    string? CorrelationId = null)
{
    /// <summary>User-facing copy for a recovered commit message after an abnormal Cursor exit.</summary>
    public const string CommitUserMessage =
        "Message generated, but Cursor reported a problem when exiting. Review the message before committing. See logs for details.";

    /// <summary>User-facing copy for recovered pull request title and description after an abnormal Cursor exit.</summary>
    public const string PullRequestUserMessage =
        "Title and description generated, but Cursor reported a problem when exiting. Review them before creating. See logs for details.";

    /// <summary>Worker-side placeholder; the App replaces it with the consumer-specific copy after validation.</summary>
    public const string WorkerCandidateMessage =
        "Cursor exited with an error after returning a successful result. See logs for details.";

    public static AiGenerationWarning ProviderExitedAfterResult(string? correlationId, string message = WorkerCandidateMessage) =>
        new(AiGenerationWarningCodes.ProviderExitedAfterResult, message, correlationId);

    /// <summary>What GrayMoon did after a failed run: the variable it added and for which agent. The value is not shown.</summary>
    public static string EnvironmentVariableAddedMessage(string providerId, string variableName) =>
        $"{AiProviderIds.DisplayName(providerId)} failed the first time, so GrayMoon added the environment variable {variableName} for {AiProviderIds.DisplayName(providerId)} and tried again. See Worker, Environment.";

    public static AiGenerationWarning EnvironmentVariableAdded(string providerId, string variableName) =>
        new(AiGenerationWarningCodes.EnvironmentVariableAdded, EnvironmentVariableAddedMessage(providerId, variableName));
}

/// <summary>Optional warning on the Worker <c>GenerateAiText</c> response. Older App/Worker binaries ignore missing fields.</summary>
public sealed class AiGenerationWarningDto
{
    [JsonPropertyName("code")]
    public string? Code { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("correlationId")]
    public string? CorrelationId { get; set; }

    public AiGenerationWarning? ToWarning() =>
        string.IsNullOrWhiteSpace(Code) || string.IsNullOrWhiteSpace(Message)
            ? null
            : new AiGenerationWarning(Code.Trim(), Message.Trim(), string.IsNullOrWhiteSpace(CorrelationId) ? null : CorrelationId.Trim());

    public static AiGenerationWarningDto? From(AiGenerationWarning? warning) =>
        warning is null
            ? null
            : new AiGenerationWarningDto
            {
                Code = warning.Code,
                Message = warning.Message,
                CorrelationId = warning.CorrelationId,
            };
}
