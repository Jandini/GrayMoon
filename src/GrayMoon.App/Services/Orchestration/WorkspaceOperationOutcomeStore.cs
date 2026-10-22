using System.Collections.Concurrent;
using GrayMoon.Application.Features;

namespace GrayMoon.App.Services.Orchestration;

/// <summary>What a Workspace context's last Update / Push run reported, kept apart from any page instance.</summary>
public sealed record WorkspaceOperationOutcome(
    string Operation,
    DateTime StartedUtc,
    IReadOnlyDictionary<int, string> RepositoryErrors,
    IReadOnlyDictionary<int, string> LevelErrors)
{
    public bool HasErrors => RepositoryErrors.Count > 0 || LevelErrors.Count > 0;

    /// <summary>Text for the page-level banner: the level errors lowest level first, then a repository count.</summary>
    public string Describe()
    {
        var lines = new List<string>();
        foreach (var (level, message) in LevelErrors.OrderBy(kv => kv.Key))
            lines.Add(level > 0 ? $"Level {level}: {message}" : message);

        if (RepositoryErrors.Count > 0)
            lines.Add(RepositoryErrors.Count == 1 ? "1 repository reported an error." : $"{RepositoryErrors.Count} repositories reported errors.");

        return $"{Operation} stopped. " + string.Join("\n", lines);
    }
}

/// <summary>
/// Remembers the errors of the last Update / Push run per Workspace context. The Repositories page keeps its error
/// callouts in fields of the page instance, and those are lost when the user leaves the page or reloads while a run
/// is still going. A run that fails minutes later (for example a package wait) would then end without any trace on
/// screen. The operation facades write here, so the failure is shown whenever the page is next opened.
/// </summary>
public sealed class WorkspaceOperationOutcomeStore
{
    private readonly ConcurrentDictionary<(int WorkspaceId, int ContextId), Entry> _entries = new();

    /// <summary>Starts a run: forgets the previous outcome of this context and returns the recorder for the new one.</summary>
    public Recorder Begin(int workspaceId, WorkspaceFeatureContextId contextId, string operation)
    {
        var entry = new Entry(operation, DateTime.UtcNow);
        _entries[(workspaceId, contextId.Value)] = entry;
        return new Recorder(entry);
    }

    /// <summary>The last run's outcome when it reported errors, else null.</summary>
    public WorkspaceOperationOutcome? Get(int workspaceId, WorkspaceFeatureContextId contextId) =>
        _entries.TryGetValue((workspaceId, contextId.Value), out var entry) && entry.Snapshot() is { HasErrors: true } outcome
            ? outcome
            : null;

    public void Clear(int workspaceId, WorkspaceFeatureContextId contextId) =>
        _entries.TryRemove((workspaceId, contextId.Value), out _);

    public sealed class Recorder
    {
        private readonly Entry _entry;

        internal Recorder(Entry entry) => _entry = entry;

        public void Repository(int repositoryId, string message) => _entry.Add(_entry.RepositoryErrors, repositoryId, message);

        public void Level(int level, string message) => _entry.Add(_entry.LevelErrors, level, message);

        /// <summary>Records the errors of a finished run's result (for runs that report through their result, not callbacks).</summary>
        public void Result(OperationResult result)
        {
            if (result.RepoErrors is { } repoErrors)
            {
                foreach (var (id, message) in repoErrors)
                    Repository(id, message);
            }

            if (result.LevelErrors is { } levelErrors)
            {
                foreach (var (level, message) in levelErrors)
                    Level(level, message);
            }

            if (!result.Success && result.RepoErrors is not { Count: > 0 } && result.LevelErrors is not { Count: > 0 }
                && !string.IsNullOrWhiteSpace(result.Error))
                Level(0, result.Error);
        }
    }

    internal sealed class Entry(string operation, DateTime startedUtc)
    {
        private readonly object _gate = new();

        public Dictionary<int, string> RepositoryErrors { get; } = new();
        public Dictionary<int, string> LevelErrors { get; } = new();

        public void Add(Dictionary<int, string> target, int key, string message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return;

            var trimmed = message.Trim();
            lock (_gate)
            {
                if (!target.TryGetValue(key, out var existing))
                    target[key] = trimmed;
                else if (!existing.Contains(trimmed, StringComparison.Ordinal))
                    target[key] = existing + "\n" + trimmed;
            }
        }

        public WorkspaceOperationOutcome Snapshot()
        {
            lock (_gate)
            {
                return new WorkspaceOperationOutcome(
                    operation,
                    startedUtc,
                    new Dictionary<int, string>(RepositoryErrors),
                    new Dictionary<int, string>(LevelErrors));
            }
        }
    }
}
