using GrayMoon.App.Data;
using GrayMoon.App.Models;
using GrayMoon.App.Services.Security;
using GrayMoon.Common.Ai;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Services.Ai;

/// <summary>
/// The environment variables GrayMoon sets when it starts an AI agent: a commit message or pull request text run on the
/// Worker, and Open in Cursor / Claude CLI / Codex CLI from GrayMoon Desktop. One list for this GrayMoon, stored in the
/// local database with encrypted values and never written to <c>graymoon.json</c>.
/// </summary>
/// <remarks>
/// This is the provider, not the Worker page: the page edits the list through <see cref="SaveAsync"/>, and GrayMoon
/// itself adds a variable through <see cref="ApplyFixAsync"/> when an agent fails for a reason a variable fixes. Both
/// go through one gate, so the list never holds a name twice in a scope. A singleton with factory-created contexts:
/// it has no per-circuit state, and <see cref="Changed"/> lets an open page pick up a change it did not make.
/// </remarks>
public sealed class AgentEnvironmentStore(
    IDbContextFactory<AppDbContext> dbContextFactory,
    ITokenProtector protector,
    ILogger<AgentEnvironmentStore> logger)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyList<AgentEnvironmentVariable>? _cached;

    /// <summary>Raised after the stored list changed, on the thread that changed it. Subscribers marshal to their own dispatcher.</summary>
    public event Action? Changed;

    public async Task<IReadOnlyList<AgentEnvironmentVariable>> GetAsync(CancellationToken cancellationToken = default)
    {
        if (_cached is { } cached)
            return cached;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            return _cached ??= await LoadAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The variables one agent starts with (<see cref="AgentEnvironment.Resolve"/>). Empty when none is listed.</summary>
    public async Task<IReadOnlyDictionary<string, string>> ResolveAsync(string providerId, CancellationToken cancellationToken = default) =>
        AgentEnvironment.Resolve(await GetAsync(cancellationToken), providerId);

    /// <summary>
    /// Replaces the list. Returns why it was refused (an invalid name or value, a name listed twice in a scope), or null
    /// when it was stored.
    /// </summary>
    public async Task<string?> SaveAsync(IReadOnlyList<AgentEnvironmentVariable> variables, CancellationToken cancellationToken = default)
    {
        if (AgentEnvironment.ListError(variables) is { } error)
            return error;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await WriteAsync(variables, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke();
        return null;
    }

    /// <summary>
    /// Adds the variable an agent's adapter named for a failure, without listing a name twice
    /// (<see cref="AgentEnvironment.ApplyFix"/>). Returns the row that was added or updated, or null when the agent
    /// already runs with the fix - then there is nothing to try again with.
    /// </summary>
    public async Task<AgentEnvironmentVariable?> ApplyFixAsync(string providerId, AgentEnvironmentFix fix, CancellationToken cancellationToken = default)
    {
        if (!AiProviderIds.IsKnown(providerId))
            throw new ArgumentException($"Unknown AI provider \"{providerId}\".", nameof(providerId));

        AgentEnvironmentVariable? changed;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var current = _cached ??= await LoadAsync(cancellationToken);
            var result = AgentEnvironment.ApplyFix(current, providerId, fix);
            changed = result.Changed;
            if (changed is null)
                return null;
            if (AgentEnvironment.ListError(result.Variables) is { } error)
            {
                logger.LogWarning("An environment fix was not stored. Provider={Provider} Variable={Variable} Reason={Reason}", providerId, fix.Name, error);
                return null;
            }

            await WriteAsync(result.Variables, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }

        logger.LogInformation("Environment variable stored by a fix. Provider={Provider} Variable={Variable}", providerId, changed.Name);
        Changed?.Invoke();
        return changed;
    }

    /// <summary>
    /// Lists the known variables the user already has set on the Worker host (<see cref="AgentEnvironment.ImportHostValues"/>):
    /// a row per variable for its agent, never a second row for a name the agent already gets from the list. Returns the
    /// rows that were added; empty when the list already covers them.
    /// </summary>
    public async Task<IReadOnlyList<AgentEnvironmentVariable>> ImportHostValuesAsync(IReadOnlyList<AgentEnvironmentHostValue> hostValues, CancellationToken cancellationToken = default)
    {
        if (hostValues.Count == 0)
            return [];

        IReadOnlyList<AgentEnvironmentVariable> added;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var current = _cached ??= await LoadAsync(cancellationToken);
            var result = AgentEnvironment.ImportHostValues(current, hostValues);
            added = result.Added;
            if (added.Count == 0)
                return added;
            if (AgentEnvironment.ListError(result.Variables) is { } error)
            {
                logger.LogWarning("Environment variables set on the Worker host were not listed. Reason={Reason}", error);
                return [];
            }

            await WriteAsync(result.Variables, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke();
        return added;
    }

    private async Task<IReadOnlyList<AgentEnvironmentVariable>> LoadAsync(CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.AgentEnvironmentVariables.AsNoTracking()
            .OrderBy(v => v.Position)
            .ThenBy(v => v.AgentEnvironmentVariableId)
            .ToListAsync(cancellationToken);

        var variables = new List<AgentEnvironmentVariable>(rows.Count);
        foreach (var row in rows)
        {
            try
            {
                variables.Add(new AgentEnvironmentVariable(row.Scope, row.Name, protector.Unprotect(row.ProtectedValue)));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A value encrypted with a key this install no longer has. The variable is not applied; saving the list replaces the row.
                logger.LogWarning(ex, "A stored environment variable could not be decrypted and is left out. Scope={Scope} Variable={Variable}", row.Scope, row.Name);
            }
        }

        return variables;
    }

    /// <summary>Called inside the gate. Replaces every row in one transaction, then the cache.</summary>
    private async Task WriteAsync(IReadOnlyList<AgentEnvironmentVariable> variables, CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.AgentEnvironmentVariables.ExecuteDeleteAsync(cancellationToken);
        for (var i = 0; i < variables.Count; i++)
        {
            db.AgentEnvironmentVariables.Add(new AgentEnvironmentVariableRow
            {
                Scope = variables[i].Scope,
                Name = variables[i].Name,
                ProtectedValue = protector.Protect(variables[i].Value),
                Position = i,
            });
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        _cached = variables.ToList();
    }
}
