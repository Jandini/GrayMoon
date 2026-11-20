using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace GrayMoon.Common.Ai;

/// <summary>
/// Which AI agents an environment variable is for: <see cref="All"/>, or one <see cref="AiProviderIds"/> value.
/// </summary>
public static class AgentEnvironmentScopes
{
    /// <summary>Every AI agent GrayMoon starts.</summary>
    public const string All = "all";

    /// <summary><see cref="All"/> first, then the providers in menu order.</summary>
    public static readonly IReadOnlyList<string> Known = [All, .. AiProviderIds.All];

    public static bool IsKnown(string? scope) =>
        scope is not null && Known.Contains(scope, StringComparer.Ordinal);

    public static string DisplayName(string scope) =>
        scope == All ? "All" : AiProviderIds.DisplayName(scope);
}

/// <summary>One environment variable GrayMoon sets when it starts the AI agents of <see cref="Scope"/>.</summary>
public sealed record AgentEnvironmentVariable(
    [property: JsonPropertyName("scope")] string Scope,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("value")] string Value);

/// <summary>
/// A variable an agent is known to read, offered as a suggestion when the user types a name. It comes from the agent's
/// own adapter on the Worker, so the list follows the agents that are installed.
/// </summary>
public sealed record KnownAgentEnvironmentVariable(string ProviderId, string Name, string Value, string Description);

/// <summary>
/// A variable that makes a failed agent run work, named by the agent's adapter for one recognized failure.
/// <see cref="AppendToExisting"/> is for list-like variables (<c>NODE_OPTIONS</c>): the value is added to what is
/// already set instead of replacing it.
/// </summary>
public sealed record AgentEnvironmentFix(string Name, string Value, bool AppendToExisting);

/// <summary><see cref="AgentEnvironmentFix"/> on the Worker <c>GenerateAiText</c> response.</summary>
public sealed class AgentEnvironmentFixDto
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("value")]
    public string? Value { get; set; }

    [JsonPropertyName("appendToExisting")]
    public bool AppendToExisting { get; set; }

    public AgentEnvironmentFix? ToFix() =>
        AgentEnvironment.NameError(Name) is null && !string.IsNullOrEmpty(Value) && AgentEnvironment.ValueError(Value) is null
            ? new AgentEnvironmentFix(Name!, Value, AppendToExisting)
            : null;

    public static AgentEnvironmentFixDto? From(AgentEnvironmentFix? fix) =>
        fix is null ? null : new AgentEnvironmentFixDto { Name = fix.Name, Value = fix.Value, AppendToExisting = fix.AppendToExisting };
}

/// <summary>A known variable the user already has set on the Worker host, for the agent that reads it.</summary>
public sealed record AgentEnvironmentHostValue(string ProviderId, string Name, string Value);

/// <summary>The outcome of <see cref="AgentEnvironment.ImportHostValues"/>: the list to store, and the rows that were added.</summary>
public sealed record AgentEnvironmentImportResult(IReadOnlyList<AgentEnvironmentVariable> Variables, IReadOnlyList<AgentEnvironmentVariable> Added);

/// <summary>The outcome of <see cref="AgentEnvironment.ApplyFix"/>: the list to store, and the row that changed (null when nothing did).</summary>
public sealed record AgentEnvironmentFixResult(IReadOnlyList<AgentEnvironmentVariable> Variables, AgentEnvironmentVariable? Changed);

/// <summary>
/// The rules for agent environment variables, shared by the App (which stores them) and the Worker (which applies them
/// to a process): what a valid name and value are, that a scope lists a name once, and that an agent's own scope wins
/// over <see cref="AgentEnvironmentScopes.All"/>. Names are compared without case, as Windows does.
/// </summary>
public static partial class AgentEnvironment
{
    public const int MaxVariables = 64;
    public const int MaxNameLength = 128;
    public const int MaxValueLength = 4_096;

    public static readonly StringComparer NameComparer = StringComparer.OrdinalIgnoreCase;

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();

    /// <summary>Why <paramref name="name"/> cannot be a variable name, or null when it can.</summary>
    public static string? NameError(string? name)
    {
        if (string.IsNullOrEmpty(name))
            return "Enter a name.";
        if (name.Length > MaxNameLength)
            return $"A name can have at most {MaxNameLength} characters.";
        return NamePattern().IsMatch(name)
            ? null
            : "A name has letters, digits and underscores, and does not start with a digit.";
    }

    /// <summary>Why <paramref name="value"/> cannot be a variable value, or null when it can. An empty value is allowed.</summary>
    public static string? ValueError(string? value)
    {
        if (value is null)
            return null;
        if (value.Length > MaxValueLength)
            return $"A value can have at most {MaxValueLength} characters.";
        return value.AsSpan().IndexOfAny('\0', '\r', '\n') >= 0
            ? "A value is one line of text."
            : null;
    }

    /// <summary>The first problem in <paramref name="variables"/>, or null when the list can be stored and applied.</summary>
    public static string? ListError(IReadOnlyList<AgentEnvironmentVariable> variables)
    {
        if (variables.Count > MaxVariables)
            return $"At most {MaxVariables} variables can be listed.";

        var seen = new HashSet<(string Scope, string Name)>(ScopeAndNameComparer.Instance);
        foreach (var variable in variables)
        {
            if (!AgentEnvironmentScopes.IsKnown(variable.Scope))
                return $"\"{variable.Scope}\" is not a scope.";
            if (NameError(variable.Name) is { } nameError)
                return string.IsNullOrEmpty(variable.Name) ? nameError : $"{variable.Name}: {nameError}";
            if (ValueError(variable.Value) is { } valueError)
                return $"{variable.Name}: {valueError}";
            if (!seen.Add((variable.Scope, variable.Name)))
                return $"{variable.Name} is listed twice for {AgentEnvironmentScopes.DisplayName(variable.Scope)}.";
        }

        return null;
    }

    /// <summary>
    /// The variables one agent starts with: every <see cref="AgentEnvironmentScopes.All"/> row, then the agent's own rows
    /// on top, so a name listed for both takes the agent's value.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Resolve(IEnumerable<AgentEnvironmentVariable> variables, string providerId)
    {
        var all = new Dictionary<string, string>(NameComparer);
        var own = new Dictionary<string, string>(NameComparer);
        foreach (var variable in variables)
        {
            if (variable.Scope == AgentEnvironmentScopes.All)
                all[variable.Name] = variable.Value;
            else if (string.Equals(variable.Scope, providerId, StringComparison.Ordinal))
                own[variable.Name] = variable.Value;
        }

        foreach (var (name, value) in own)
            all[name] = value;
        return all;
    }

    /// <summary>
    /// Lists the known variables the user already has set on the Worker host, each for its own agent, so the list shows
    /// what the agent runs with. A name the agent already gets from the list (its own row, or an
    /// <see cref="AgentEnvironmentScopes.All"/> row) is left alone: the list wins over the host and no name is listed
    /// twice. A value that is not a usable name or value is skipped, and so is anything past <see cref="MaxVariables"/>.
    /// </summary>
    public static AgentEnvironmentImportResult ImportHostValues(IReadOnlyList<AgentEnvironmentVariable> variables, IEnumerable<AgentEnvironmentHostValue> hostValues)
    {
        var next = variables.ToList();
        var added = new List<AgentEnvironmentVariable>();
        foreach (var host in hostValues)
        {
            if (next.Count >= MaxVariables)
                break;
            if (!AiProviderIds.IsKnown(host.ProviderId)
                || NameError(host.Name) is not null
                || string.IsNullOrEmpty(host.Value)
                || ValueError(host.Value) is not null
                || Resolve(next, host.ProviderId).ContainsKey(host.Name))
                continue;

            var row = new AgentEnvironmentVariable(host.ProviderId, host.Name, host.Value);
            next.Add(row);
            added.Add(row);
        }

        return added.Count == 0
            ? new AgentEnvironmentImportResult(variables, added)
            : new AgentEnvironmentImportResult(next, added);
    }

    /// <summary>True when <paramref name="value"/> already carries what <paramref name="fix"/> would set.</summary>
    public static bool IsInEffect(AgentEnvironmentFix fix, string? value)
    {
        if (value is null)
            return false;
        return fix.AppendToExisting
            ? value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(fix.Value, StringComparer.Ordinal)
            : string.Equals(value, fix.Value, StringComparison.Ordinal);
    }

    /// <summary>
    /// Adds <paramref name="fix"/> for <paramref name="providerId"/> without listing a name twice: the agent's own row is
    /// updated when there is one, else a row is added for the agent. For an appending fix the new value starts from what
    /// the agent runs with today (its own row, else the <see cref="AgentEnvironmentScopes.All"/> row), so existing options
    /// are kept. Nothing changes when the agent already runs with the fix.
    /// </summary>
    public static AgentEnvironmentFixResult ApplyFix(IReadOnlyList<AgentEnvironmentVariable> variables, string providerId, AgentEnvironmentFix fix)
    {
        Resolve(variables, providerId).TryGetValue(fix.Name, out var current);
        if (IsInEffect(fix, current))
            return new AgentEnvironmentFixResult(variables, null);

        var value = fix.AppendToExisting && !string.IsNullOrWhiteSpace(current)
            ? $"{current.Trim()} {fix.Value}"
            : fix.Value;
        var changed = new AgentEnvironmentVariable(providerId, fix.Name, value);

        var next = new List<AgentEnvironmentVariable>(variables.Count + 1);
        var replaced = false;
        foreach (var variable in variables)
        {
            if (string.Equals(variable.Scope, providerId, StringComparison.Ordinal) && NameComparer.Equals(variable.Name, fix.Name))
            {
                // The row keeps the name as the user wrote it; only its value changes.
                changed = changed with { Name = variable.Name };
                next.Add(changed);
                replaced = true;
            }
            else
            {
                next.Add(variable);
            }
        }

        if (!replaced)
            next.Add(changed);
        return new AgentEnvironmentFixResult(next, changed);
    }

    private sealed class ScopeAndNameComparer : IEqualityComparer<(string Scope, string Name)>
    {
        public static readonly ScopeAndNameComparer Instance = new();

        public bool Equals((string Scope, string Name) x, (string Scope, string Name) y) =>
            string.Equals(x.Scope, y.Scope, StringComparison.Ordinal) && NameComparer.Equals(x.Name, y.Name);

        public int GetHashCode((string Scope, string Name) value) =>
            HashCode.Combine(value.Scope, NameComparer.GetHashCode(value.Name));
    }
}
