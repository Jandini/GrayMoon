using System.Text.Json.Serialization;

namespace GrayMoon.Worker.Jobs.Response;

public sealed class GetHostInfoResponse
{
    [JsonPropertyName("dotnetVersion")]
    public string? DotnetVersion { get; set; }

    [JsonPropertyName("gitVersion")]
    public string? GitVersion { get; set; }

    [JsonPropertyName("gitVersionToolVersion")]
    public string? GitVersionToolVersion { get; set; }

    /// <summary>Optional CodeGraph CLI version; null when it is not installed. Not a prerequisite.</summary>
    [JsonPropertyName("codeGraphVersion")]
    public string? CodeGraphVersion { get; set; }

    /// <summary>Optional Cursor Agent CLI version (<c>agent</c> or <c>cursor-agent</c>); null when it is not installed. Only the Cursor AI provider needs it.</summary>
    [JsonPropertyName("cursorAgentVersion")]
    public string? CursorAgentVersion { get; set; }

    /// <summary>Optional Claude Code CLI version (<c>claude</c>); null when it is not installed. Only the Claude AI provider needs it.</summary>
    [JsonPropertyName("claudeVersion")]
    public string? ClaudeVersion { get; set; }

    /// <summary>Optional Codex CLI version (<c>codex</c>); null when it is not installed. Only the Codex AI provider needs it.</summary>
    [JsonPropertyName("codexVersion")]
    public string? CodexVersion { get; set; }

    /// <summary>
    /// Every AI CLI this Worker can run, from the same probe as <see cref="ClaudeVersion"/>, <see cref="CodexVersion"/> and <see cref="CursorAgentVersion"/>.
    /// Commit and New Pull Request read this instead of probing again.
    /// </summary>
    [JsonPropertyName("aiProviders")]
    public List<AiProviderAvailabilityDto> AiProviders { get; set; } = [];

    /// <summary>Worker host user profile directory (e.g. C:\Users\name). Used to default Feature worktree storage.</summary>
    [JsonPropertyName("userProfilePath")]
    public string? UserProfilePath { get; set; }
}
