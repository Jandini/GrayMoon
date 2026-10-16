using System.Text.Json.Serialization;

namespace GrayMoon.Common.Ai;

/// <summary>Why an AI generation did not produce text. Shared by the Worker (which runs the CLI) and the App (which shows it).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<AiGenerationErrorKind>))]
public enum AiGenerationErrorKind
{
    None,

    /// <summary>The provider's CLI was not found on the Worker's PATH.</summary>
    NotInstalled,

    /// <summary>The CLI is installed but not signed in, or its credentials expired.</summary>
    NotAuthenticated,

    /// <summary>The model configured in <c>graymoon.json</c> is not accepted by the CLI.</summary>
    UnsupportedModel,

    /// <summary>The CLI rejected the arguments GrayMoon passes: a version without the verified no-tools options.</summary>
    UnsupportedCli,

    Timeout,

    Cancelled,

    /// <summary>The CLI answered, but not with the expected JSON.</summary>
    InvalidOutput,

    /// <summary>The CLI failed for another reason; the message carries its first error line.</summary>
    ProviderFailed,

    /// <summary>The <c>ai</c> section of <c>graymoon.json</c> is invalid for this provider.</summary>
    Configuration,

    /// <summary>GrayMoon could not collect the Git evidence, or the evidence is incomplete.</summary>
    Evidence,

    /// <summary>There is nothing to describe (no changes in scope).</summary>
    NothingToDescribe,

    /// <summary>The Worker is not connected or cannot run commands.</summary>
    WorkerUnavailable,
}
