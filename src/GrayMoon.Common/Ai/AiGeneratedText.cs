using System.Text.Json;

namespace GrayMoon.Common.Ai;

/// <summary>JSON schemas the providers must answer with. Kept small and closed (no extra properties).</summary>
public static class AiOutputSchemas
{
    public const string Commit =
        """{"type":"object","properties":{"subject":{"type":"string"},"body":{"type":"string"}},"required":["subject","body"],"additionalProperties":false}""";

    public const string PullRequest =
        """{"type":"object","properties":{"title":{"type":"string"},"description":{"type":"string"}},"required":["title","description"],"additionalProperties":false}""";

    public static string For(GitEvidenceKind kind) => kind == GitEvidenceKind.PullRequest ? PullRequest : Commit;
}

/// <summary>A generated commit message: <see cref="Subject"/>, then a blank line and <see cref="Body"/> when there is one.</summary>
public sealed record GeneratedCommitMessage(string Subject, string Body)
{
    public string Message => Body.Length == 0 ? Subject : $"{Subject}\n\n{Body}";
}

public sealed record GeneratedPullRequestText(string Title, string Description);

/// <summary>
/// Strict parse of a provider's answer. The answer is data: it is only ever placed into an editable text field, never
/// run. Anything that is not the expected JSON object with non-empty text within the length limits is rejected with a
/// reason; there is no repair loop.
/// </summary>
public static class AiGeneratedTextParser
{
    public const int MaxSubjectLength = 200;
    public const int MaxCommitBodyLength = 8_000;
    public const int MaxTitleLength = 256;
    public const int MaxDescriptionLength = 30_000;

    public static bool TryParseCommit(string? json, out GeneratedCommitMessage? message, out string? error)
    {
        message = null;
        if (!TryReadObject(json, out var root, out error))
            return false;

        using (root)
        {
            var subject = OneLine(ReadString(root!.RootElement, "subject"));
            var body = NormalizeNewlines(ReadString(root.RootElement, "body")).Trim();
            if (subject.Length == 0)
            {
                error = "The AI answer has no commit subject.";
                return false;
            }

            if (subject.Length > MaxSubjectLength || body.Length > MaxCommitBodyLength)
            {
                error = "The AI answer is longer than a commit message should be.";
                return false;
            }

            message = new GeneratedCommitMessage(subject, body);
            return true;
        }
    }

    public static bool TryParsePullRequest(string? json, out GeneratedPullRequestText? text, out string? error)
    {
        text = null;
        if (!TryReadObject(json, out var root, out error))
            return false;

        using (root)
        {
            var title = OneLine(ReadString(root!.RootElement, "title"));
            var description = NormalizeNewlines(ReadString(root.RootElement, "description")).Trim();
            if (title.Length == 0 || description.Length == 0)
            {
                error = "The AI answer has no pull request title or description.";
                return false;
            }

            if (title.Length > MaxTitleLength || description.Length > MaxDescriptionLength)
            {
                error = "The AI answer is longer than a pull request title or description can be.";
                return false;
            }

            text = new GeneratedPullRequestText(title, description);
            return true;
        }
    }

    /// <summary>
    /// The JSON object in a provider's text answer. Accepts the object alone or wrapped in one Markdown code fence, the way
    /// some CLIs print it; any other surrounding text is rejected.
    /// </summary>
    public static string? ExtractJsonObject(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var trimmed = text.Trim();
        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewline = trimmed.IndexOf('\n');
            var lastFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
            if (firstNewline < 0 || lastFence <= firstNewline)
                return null;
            trimmed = trimmed[(firstNewline + 1)..lastFence].Trim();
        }

        return trimmed.StartsWith('{') && trimmed.EndsWith('}') ? trimmed : null;
    }

    private static bool TryReadObject(string? json, out JsonDocument? document, out string? error)
    {
        document = null;
        error = null;
        var candidate = ExtractJsonObject(json);
        if (candidate is null)
        {
            error = "The AI answer was not a JSON object.";
            return false;
        }

        try
        {
            document = JsonDocument.Parse(candidate);
        }
        catch (JsonException)
        {
            error = "The AI answer was not valid JSON.";
            return false;
        }

        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            document.Dispose();
            document = null;
            error = "The AI answer was not a JSON object.";
            return false;
        }

        return true;
    }

    private static string ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static string NormalizeNewlines(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n');

    private static string OneLine(string text)
    {
        var normalized = NormalizeNewlines(text).Trim();
        var newline = normalized.IndexOf('\n');
        return (newline < 0 ? normalized : normalized[..newline]).Trim();
    }
}
