using System.Text.RegularExpressions;
using GrayMoon.Common.Manifest;

namespace GrayMoon.Common.Ai;

/// <summary>Stable ids of the AI CLI providers. They are the values of <c>ai.providers[].provider</c> in <c>graymoon.json</c> and of the local pin.</summary>
public static class AiProviderIds
{
    public const string Claude = "claude";
    public const string Codex = "codex";
    public const string Cursor = "cursor";

    /// <summary>Every provider this GrayMoon can run, in menu order.</summary>
    public static readonly IReadOnlyList<string> All = [Claude, Codex, Cursor];

    public static bool IsKnown(string? providerId) =>
        providerId is not null && All.Contains(providerId, StringComparer.Ordinal);

    public static string DisplayName(string providerId) => providerId switch
    {
        Claude => "Claude",
        Codex => "Codex",
        Cursor => "Cursor",
        _ => providerId,
    };
}

/// <summary>The model to use for one provider, resolved from <c>graymoon.json</c>. <see cref="Model"/> null means the CLI default (only when GrayMoon has no default for the provider).</summary>
public sealed record AiProviderModelChoice(string ProviderId, string? Model, string? Error)
{
    public bool IsValid => Error is null;
}

/// <summary>
/// Reads the per-provider model choices of the <c>ai</c> section. Models are opaque provider-specific strings; this only
/// checks that one is safe to pass as a single command-line argument. Ids are matched case-insensitively.
/// </summary>
public static partial class AiProviderConfiguration
{
    public const int MaxModelLength = 100;

    // Letters, digits and the punctuation real model ids use (claude-sonnet-4-5, gpt-5.1-codex, sonnet[1m], vendor/model:tag).
    // Nothing a shell or cmd.exe treats specially, so a model can never become an option or a second command.
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._:/@\[\]-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex ModelPattern();

    public static bool IsValidModel(string? model) =>
        !string.IsNullOrEmpty(model) && model.Length <= MaxModelLength && ModelPattern().IsMatch(model);

    /// <summary>
    /// The model GrayMoon uses when <c>graymoon.json</c> names none. Writing a commit message or a pull request description
    /// from prepared evidence is a small task, so the default is the provider's fast, inexpensive model rather than whatever
    /// the CLI would pick (often its strongest one). Codex has no such model that every account accepts (ChatGPT accounts
    /// reject the mini models), so it keeps the CLI default; set <c>ai.providers</c> to choose one.
    /// </summary>
    public static string? DefaultModel(string providerId) => providerId switch
    {
        AiProviderIds.Claude => "haiku",
        AiProviderIds.Cursor => "grok",
        _ => null,
    };

    /// <summary>
    /// The model for <paramref name="providerId"/>: the configured one, else <see cref="DefaultModel"/>. A provider listed
    /// more than once, or with a model that is not safe to pass on, is an error naming the fix; the caller never guesses.
    /// </summary>
    public static AiProviderModelChoice Resolve(WorkspaceManifestAi? ai, string providerId)
    {
        var entries = (ai?.Providers ?? [])
            .Where(p => string.Equals(p.Provider, providerId, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (entries.Count == 0)
            return new AiProviderModelChoice(providerId, DefaultModel(providerId), null);

        if (entries.Count > 1)
            return new AiProviderModelChoice(providerId, null,
                $"graymoon.json lists the AI provider \"{providerId}\" {entries.Count} times; keep one entry.");

        var model = entries[0].Model;
        if (string.IsNullOrWhiteSpace(model))
            return new AiProviderModelChoice(providerId, DefaultModel(providerId), null);

        return IsValidModel(model)
            ? new AiProviderModelChoice(providerId, model, null)
            : new AiProviderModelChoice(providerId, null,
                $"graymoon.json has an invalid model for the AI provider \"{providerId}\". Use the model id as the CLI accepts it (letters, digits and . _ - : / @ [ ], at most {MaxModelLength} characters).");
    }

    /// <summary>Provider ids in the section that this GrayMoon does not know. They are kept in the file and otherwise ignored.</summary>
    public static IReadOnlyList<string> UnknownProviderIds(WorkspaceManifestAi? ai) =>
        (ai?.Providers ?? [])
            .Select(p => p.Provider)
            .Where(id => !AiProviderIds.All.Contains(id, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}
