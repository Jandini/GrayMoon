using System.Reflection;
using System.Reflection.Emit;
using System.Text.RegularExpressions;
using GrayMoon.App.Models;
using GrayMoon.App.Services.Ui;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace GrayMoon.App.Tests.Conventions;

/// <summary>
/// Guardrail 4 (review 2026-11-10, finding 15): the real App container builds with scope and build
/// validation on, so a scoped service injected into a singleton fails here and not in a browser tab.
/// </summary>
public sealed class ServiceContainerConventionTests
{
    [Fact]
    public async Task App_container_builds_with_scope_and_build_validation()
    {
        var builder = CreateValidatingBuilder();

        await using var app = builder.Build();

        // Per-circuit UI services resolve from a scope and are not shared between two scopes.
        await using var tabA = app.Services.CreateAsyncScope();
        await using var tabB = app.Services.CreateAsyncScope();
        Assert.NotSame(tabA.ServiceProvider.GetRequiredService<IToastService>(), tabB.ServiceProvider.GetRequiredService<IToastService>());
    }

    [Fact]
    public void A_singleton_that_injects_a_scoped_service_fails_the_container_build()
    {
        var builder = CreateValidatingBuilder();
        builder.Services.AddSingleton<SingletonHoldingPerCircuitState>();

        var ex = Assert.ThrowsAny<Exception>(() => builder.Build());
        Assert.Contains(nameof(IToastService), ex.ToString());
    }

    [Fact]
    public void Ui_notification_services_are_registered_scoped()
    {
        var builder = CreateValidatingBuilder();

        // Services that hold what one browser tab shows. A singleton here would show one tab's state in every tab.
        foreach (var service in new[] { typeof(IToastService) })
        {
            var descriptor = Assert.Single(builder.Services, d => d.ServiceType == service);
            Assert.Equal(ServiceLifetime.Scoped, descriptor.Lifetime);
        }
    }

    private static WebApplicationBuilder CreateValidatingBuilder()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Development });
        builder.Host.UseDefaultServiceProvider(options =>
        {
            options.ValidateScopes = true;
            options.ValidateOnBuild = true;
        });
        // Nothing is opened: the container is built, not started, so the database file is never created.
        var database = Path.Combine(Path.GetTempPath(), "graymoon-container-validation", "graymoon.db");
        builder.Services.AddGrayMoonServices(builder.Configuration, $"Data Source={database};Cache=Shared;Default Timeout=5;Foreign Keys=True;");
        return builder;
    }

    private sealed class SingletonHoldingPerCircuitState(IToastService toast)
    {
        public IToastService Toast { get; } = toast;
    }
}

/// <summary>
/// Guardrail 5 (review 2026-11-10, finding 2): the branch, tag, upstream, sync status, commit count and version
/// columns of the shared <see cref="WorkspaceRepositoryLink"/> are written only by the code that owns per-context state. The scan
/// reads the compiled IL, so it sees the real property setter and not a same-named property of another type.
/// </summary>
public sealed class SharedLinkWriteConventionTests
{
    private static readonly string[] GuardedProperties =
    [
        nameof(WorkspaceRepositoryLink.BranchName),
        nameof(WorkspaceRepositoryLink.CheckedOutTag),
        nameof(WorkspaceRepositoryLink.BranchHasUpstream),
        nameof(WorkspaceRepositoryLink.SyncStatus),
        nameof(WorkspaceRepositoryLink.OutgoingCommits),
        nameof(WorkspaceRepositoryLink.IncomingCommits),
        nameof(WorkspaceRepositoryLink.GitVersion),
    ];

    /// <summary>
    /// Types that set the guarded columns: the properties each may set and the reason. A known violation is
    /// recorded as an entry marked OPEN so it cannot spread; remove the entry when the code is fixed.
    /// </summary>
    private static readonly Dictionary<string, (string[] Properties, string Reason)> AllowedWriters = new(StringComparer.Ordinal)
    {
        ["GrayMoon.App.Services.Workspaces.WorkspaceRepositoryStateWriter"] = (GuardedProperties,
            "The owner of per-context state; it mirrors the special Workspace context onto the link."),
        ["GrayMoon.App.Models.WorkspaceRepositoryLink"] = (GuardedProperties,
            "The model's own copy method builds a detached link."),
        ["GrayMoon.App.Services.Queries.WorkspaceRepositoryLinkListMapper"] = (GuardedProperties,
            "Maps a context-scoped list row to a detached link for the grid; nothing is saved."),
        ["GrayMoon.App.Components.Pages.WorkspaceRepositories"] = (
            [
                nameof(WorkspaceRepositoryLink.BranchName), nameof(WorkspaceRepositoryLink.GitVersion),
                nameof(WorkspaceRepositoryLink.OutgoingCommits), nameof(WorkspaceRepositoryLink.IncomingCommits),
            ],
            "Updates the detached grid row of the selected context during a sync; nothing is saved."),
        ["GrayMoon.App.Services.Application.WorkspaceSyncOperations"] = (
            [
                nameof(WorkspaceRepositoryLink.BranchName), nameof(WorkspaceRepositoryLink.CheckedOutTag),
                nameof(WorkspaceRepositoryLink.OutgoingCommits),
            ],
            "Overlays the Feature context's state on links loaded AsNoTracking before a handler filters them; nothing is saved."),
        ["GrayMoon.App.Repositories.WorkspaceRepository"] = ([nameof(WorkspaceRepositoryLink.SyncStatus)],
            "Creates a new link row in its initial NeedsSync state; no context has state for it yet."),
        ["GrayMoon.App.Services.Application.WorkspaceRepositoryOperations"] = ([nameof(WorkspaceRepositoryLink.SyncStatus)],
            "Creates a new link row in its initial NeedsSync state; no context has state for it yet."),
    };

    [Fact]
    public void Shared_link_branch_tag_and_upstream_are_written_only_by_the_allowed_writers()
    {
        var writers = FindWriters(typeof(WorkspaceRepositoryLink).Assembly);

        Assert.NotEmpty(writers);
        var violations = writers
            .Select(w => (Type: w.Key, Extra: w.Value.Except(AllowedWriters.TryGetValue(w.Key, out var allowed) ? allowed.Properties : []).Order(StringComparer.Ordinal).ToList()))
            .Where(w => w.Extra.Count > 0)
            .Select(w => $"{w.Type}: {string.Join(", ", w.Extra)}")
            .Order(StringComparer.Ordinal)
            .ToList();
        Assert.True(violations.Count == 0,
            "WorkspaceRepositoryLink.BranchName / CheckedOutTag / BranchHasUpstream / SyncStatus / OutgoingCommits / IncomingCommits / GitVersion belong to the special Workspace context. "
            + "Write per-context state through WorkspaceRepositoryStateWriter (GrayMoon/AGENTS.md, Feature-context scoping):"
            + Environment.NewLine + string.Join(Environment.NewLine, violations));

        var stale = AllowedWriters.Keys.Where(k => !writers.ContainsKey(k)).ToList();
        Assert.True(stale.Count == 0, "Allowed writers that no longer write the link; remove them from the list: " + string.Join(", ", stale));
    }

    [Fact]
    public void Scan_finds_a_setter_call_in_plain_async_and_lambda_code()
    {
        var writers = FindWriters(typeof(SharedLinkWriteConventionTests).Assembly);

        var found = Assert.Contains(typeof(SampleLinkWriter).FullName!, writers);
        Assert.Equal(GuardedProperties.Order(StringComparer.Ordinal), found.Order(StringComparer.Ordinal));
        Assert.DoesNotContain(typeof(SampleLinkReader).FullName!, writers);
    }

    /// <summary>Outermost declaring type -> the guarded properties its methods (lambdas and state machines included) set.</summary>
    internal static Dictionary<string, HashSet<string>> FindWriters(Assembly assembly)
    {
        var setters = GuardedProperties.ToDictionary(
            p => (MethodBase)typeof(WorkspaceRepositoryLink).GetProperty(p)!.SetMethod!,
            p => p);
        var result = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        foreach (var (method, callee) in IlCallScanner.Calls(assembly))
        {
            var match = setters.Keys.FirstOrDefault(s => s.Module == callee.Module && s.MetadataToken == callee.MetadataToken);
            if (match == null)
                continue;

            var owner = method.DeclaringType!;
            while (owner.DeclaringType != null)
                owner = owner.DeclaringType;
            if (!result.TryGetValue(owner.FullName!, out var properties))
                result[owner.FullName!] = properties = new HashSet<string>(StringComparer.Ordinal);
            properties.Add(setters[match]);
        }

        return result;
    }
}

/// <summary>Synthetic code the IL scan must (writer) and must not (reader) report.</summary>
internal static class SampleLinkWriter
{
    public static void Plain(WorkspaceRepositoryLink link) => link.BranchName = "main";

    public static async Task InAsync(WorkspaceRepositoryLink link)
    {
        await Task.Yield();
        link.CheckedOutTag = "1.0.0";
    }

    public static Action<WorkspaceRepositoryLink> InLambda() => link => link.BranchHasUpstream = true;

    public static void Counts(WorkspaceRepositoryLink link)
    {
        link.SyncStatus = RepoSyncStatus.InSync;
        link.OutgoingCommits = 1;
        link.IncomingCommits = 2;
        link.GitVersion = "1.0.0";
    }
}

internal static class SampleLinkReader
{
    public static string? Read(WorkspaceRepositoryLink link) =>
        link.BranchName + link.CheckedOutTag + link.BranchHasUpstream + link.SyncStatus + link.OutgoingCommits + link.IncomingCommits + link.GitVersion;
}

/// <summary>Walks the IL of every method in an assembly and yields the methods it calls.</summary>
internal static class IlCallScanner
{
    private static readonly Dictionary<short, OpCode> OpCodesByValue = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(f => (OpCode)f.GetValue(null)!)
        .ToDictionary(o => o.Value);

    public static IEnumerable<(MethodBase Caller, MethodBase Callee)> Calls(Assembly assembly)
    {
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        foreach (var type in assembly.GetTypes())
        {
            foreach (var method in type.GetMethods(all).Cast<MethodBase>().Concat(type.GetConstructors(all)))
            {
                byte[]? il;
                try { il = method.GetMethodBody()?.GetILAsByteArray(); }
                catch (Exception ex) when (ex is InvalidOperationException or BadImageFormatException) { il = null; }
                if (il == null)
                    continue;

                foreach (var token in MethodTokens(il))
                {
                    MethodBase? callee;
                    try
                    {
                        callee = method.Module.ResolveMethod(
                            token,
                            type.IsGenericType ? type.GetGenericArguments() : null,
                            method.IsGenericMethod ? method.GetGenericArguments() : null);
                    }
                    catch (Exception ex) when (ex is ArgumentException or BadImageFormatException)
                    {
                        // Not resolvable in this generic context: not a call to a plain property setter.
                        callee = null;
                    }

                    if (callee != null)
                        yield return (method, callee);
                }
            }
        }
    }

    private static IEnumerable<int> MethodTokens(byte[] il)
    {
        var i = 0;
        while (i < il.Length)
        {
            short value = il[i++];
            if (value == 0xFE && i < il.Length)
                value = unchecked((short)(0xFE00 | il[i++]));
            if (!OpCodesByValue.TryGetValue(value, out var op))
                yield break; // unknown opcode: stop instead of misreading operands

            switch (op.OperandType)
            {
                case OperandType.InlineMethod:
                    yield return BitConverter.ToInt32(il, i);
                    i += 4;
                    break;
                case OperandType.InlineSwitch:
                    i += 4 + (4 * BitConverter.ToInt32(il, i));
                    break;
                case OperandType.InlineNone:
                    break;
                case OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar:
                    i += 1;
                    break;
                case OperandType.InlineVar:
                    i += 2;
                    break;
                case OperandType.InlineI8 or OperandType.InlineR:
                    i += 8;
                    break;
                default:
                    i += 4;
                    break;
            }
        }
    }
}

/// <summary>
/// Guardrail 7 (review 2026-11-10, findings 8, 10 and 11): a "best effort" catch always reports. It logs,
/// rethrows, or carries the exception into the outcome it returns. Scanned: the App's workspace, git, connector
/// and Feature services and its hubs, and the whole Worker. The rest of the App is not scanned yet.
/// </summary>
public sealed class BestEffortCatchConventionTests
{
    private static readonly string[] ScannedFolders =
    [
        "Services/Workspaces", "Services/Git", "Services/Connectors", "Services/Features", "Hubs",
        "../GrayMoon.Worker",
    ];

    /// <summary>
    /// "File: first line of the catch" for a catch that may stay silent, with the reason. A reason that starts
    /// with OPEN is a known gap, listed so it cannot spread; remove the entry when the code reports.
    /// </summary>
    private static readonly Dictionary<string, string> Exceptions = new(StringComparer.Ordinal)
    {
        ["Services/Workspaces/PackageWaitDiagnostics.cs: catch (Exception) { return Correlate(commitSha, commitShaSource, null, lookupAttempted: true, lookupFailed: true); }"] =
            "The outcome it returns records the failure (lookupFailed: true), which the caller shows.",
        ["Services/Workspaces/SyncRecomputeCoalescer.cs: catch { /* the previous run's failure belongs to its own callers */ }"] =
            "Only waits for the previous run to finish; that run's failure was already thrown to its own callers.",
        ["Services/Connectors/TokenHealthBackgroundService.cs: catch (Exception) { return new ConnectorHealthResult(connectorId, false, \"Token needs to be re-entered.\"); }"] =
            "The outcome it returns is the report: the connector is shown unhealthy with this message.",

        // The Worker, as found when the scan was widened to it. One key covers every catch in the file with the same text.
        ["GrayMoon.Worker/Cli/Handlers/InstallCommandHandler.cs: catch (Exception ex) when (ServiceLogonFailure.IsMatch(ex)) { logonFailure = true; return false; }"] =
            "Sets logonFailure, which the caller turns into the message the user sees.",
        ["GrayMoon.Worker/Cli/Handlers/RunCommandHandler.cs: catch { }"] =
            "A hub URL that does not parse leaves the App API base URL unset; the token provider logs that when it is needed.",
        ["GrayMoon.Worker/Commands/GetHostInfoCommand.cs: catch { return null; }"] =
            "Probes a tool's version; null is shown as \"not installed\" on the Worker page.",
        ["GrayMoon.Worker/Commands/WriteRepositoryFileCommand.cs: catch { /* best-effort */ }"] =
            "Deletes the temp file while the write's own exception is rethrown by the enclosing catch.",
        ["GrayMoon.Worker/Logging/OverlayStreamSerilogSink.cs: catch { }"] =
            "Runs inside a log sink; logging from it would recurse.",
        ["GrayMoon.Worker/Services/Ai/AiCliDiagnostics.cs: catch { return (category, \"unavailable\"); }"] =
            "Diagnostics only; \"unavailable\" is the value written to the diagnostic log.",
        ["GrayMoon.Worker/Services/Ai/AiCliDiagnostics.cs: catch { versionsCount = -1; }"] =
            "Diagnostics only; -1 is the value written to the diagnostic log.",
        ["GrayMoon.Worker/Services/Ai/AiCliDiagnostics.cs: catch { return null; }"] =
            "Diagnostics only (two places); null is the value written to the diagnostic log.",
        ["GrayMoon.Worker/Services/CsProjFileParser.cs: catch { return Task.FromResult<CsProjFileInfo?>(null); }"] =
            "OPEN: a project file that cannot be read or parsed is dropped from the scan with no log line; the class has no logger.",
        ["GrayMoon.Worker/Services/CsProjFileParser.cs: catch { return false; }"] =
            "OPEN: a project file that cannot be read is reported as \"not updated\" with no reason; the class has no logger.",
        ["GrayMoon.Worker/Services/CsProjFileParser.cs: catch { return null; }"] =
            "OPEN: a project file that cannot be read is reported as \"nothing to update\" with no reason; the class has no logger.",
        ["GrayMoon.Worker/Services/FeatureFolderCleaner.cs: catch (Exception) { return false; }"] =
            "A marker whose folder is not a valid path does not name this folder, so the folder is left alone.",
        ["GrayMoon.Worker/Services/FeatureFolderCleaner.cs: catch (Exception) { return true; }"] =
            "An unreadable .git file counts as a registered worktree, so the folder is not deleted.",
        ["GrayMoon.Worker/Services/GitService.cs: catch { /* best-effort */ }"] =
            "Clears a read-only flag before a delete; the delete that follows reports its own failure.",
        ["GrayMoon.Worker/Services/GitWorktreeService.cs: catch { }"] =
            "Deletes an empty worktree folder; the residue scan that follows reports what is left.",
        ["GrayMoon.Worker/Services/GitWorktreeService.cs: catch { return; }"] =
            "Three places. Two are in the residue delete, whose caller scans for residue afterwards. "
            + "OPEN: the third is in that residue scan (VisitResidueEntries), where a folder that cannot be listed is counted as empty.",
        ["GrayMoon.Worker/Services/WorkspaceFileSearchService.cs: catch { return Task.FromResult<IReadOnlyList<WorkspaceFileSearchResult>>([]); }"] =
            "OPEN: a workspace folder that cannot be listed gives an empty search result with no reason.",
        ["GrayMoon.Worker/Services/WorktreeCreateHookDeferral.cs: catch (Exception) { return path.Trim(); }"] =
            "Normalizes a path for a comparison; a path that is not valid is compared as written.",
    };

    private static readonly Regex CatchAll = new(
        @"\bcatch\s*(?:\(\s*(?:System\.)?Exception(?:\s+(?<variable>\w+))?\s*\))?\s*(?<filter>when\s*\([^{]*\))?\s*\{",
        RegexOptions.Compiled);

    /// <summary>
    /// The filter text after the variable name when it only names exception types (<c>ex is A or B</c>). Such a
    /// catch is a typed catch written with a filter, the same as <c>catch (IOException)</c>, and not a catch-all.
    /// </summary>
    private const string TypeListFilter = @"\s+is\s+[\w.]+(?:\s+or\s+[\w.]+)+\s*\)\s*$|\s+is\s+(?!not\b)[\w.]+\s*\)\s*$";

    private static readonly Regex Reports = new(
        @"\bthrow\b|\b_?[lL]og(?:ger)?\s*\??\s*\.\s*\w+\s*\(|\bLog(?:Trace|Debug|Information|Warning|Error|Critical)\s*\(",
        RegexOptions.Compiled);

    [Fact]
    public void Catch_all_blocks_in_the_scanned_folders_always_report()
    {
        var catches = 0;
        var violations = new List<string>();
        foreach (var folder in ScannedFolders)
        {
            foreach (var file in ConventionSource.Files(Path.Combine(ConventionSource.AppDirectory, folder), ".cs"))
            {
                var source = File.ReadAllText(file);
                catches += CatchAll.Matches(ConventionSource.WithoutLineComments(source)).Count;
                // An App file is named from the App project, a Worker file from "GrayMoon.Worker/".
                var name = ConventionSource.Relative(file);
                if (name.StartsWith("../", StringComparison.Ordinal))
                    name = name[3..];
                violations.AddRange(FindSilentCatches(source).Select(c => $"{name}: {c.Text}"));
            }
        }

        var stale = Exceptions.Keys.Except(violations).ToList();
        Assert.True(stale.Count == 0, "Exceptions that no longer apply; remove them from the list: " + string.Join("; ", stale));
        violations.RemoveAll(Exceptions.ContainsKey);

        Assert.True(catches > 10, "The scan found almost no catch blocks; it is broken.");
        Assert.True(violations.Count == 0,
            "A catch-all must log, rethrow, or put the exception into the outcome it returns (GrayMoon/AGENTS.md, Failures, cancellation and waiting):"
            + Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void Scan_reports_only_the_catch_that_stays_silent()
    {
        const string source = """
            try { A(); } catch (Exception) { }
            try { B(); } catch { return false; }
            try { C(); } catch (Exception ex) { logger.LogWarning(ex, "C failed"); }
            try { D(); } catch (Exception ex) { return OperationResult.Fail(ex.Message); }
            try { E(); } catch (Exception) { throw; }
            try { F(); } catch (OperationCanceledException) { }
            try { G(); } catch (Exception ex) when (ex is not OperationCanceledException) { failed++; }
            try { H(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            try { I(); } catch (Exception ex) when (ex is IOException) { return null; }
            try { J(); } catch (Exception ex) when (IsTransient(ex)) { retry = true; }
            """;

        // H and I only list exception types in the filter: typed catches, like F. J's filter is a call, so the
        // scan cannot tell what it lets through and reports it.
        Assert.Equal(
            [
                "catch (Exception) { }",
                "catch { return false; }",
                "catch (Exception ex) when (ex is not OperationCanceledException) { failed++; }",
                "catch (Exception ex) when (IsTransient(ex)) { retry = true; }",
            ],
            FindSilentCatches(source).Select(c => c.Text).ToArray());
    }

    /// <summary>Catch-all blocks that neither log, rethrow, nor use the caught exception.</summary>
    internal static IReadOnlyList<(int Line, string Text)> FindSilentCatches(string source)
    {
        source = ConventionSource.WithoutLineComments(source);
        var result = new List<(int, string)>();
        foreach (Match m in CatchAll.Matches(source))
        {
            var body = ConventionSource.Balanced(source, m.Index + m.Length - 1, '{', '}');
            if (Reports.IsMatch(body))
                continue;
            var variable = m.Groups["variable"].Value;
            if (variable.Length > 0 && Regex.IsMatch(body, $@"\b{Regex.Escape(variable)}\b"))
                continue;
            if (variable.Length > 0 && Regex.IsMatch(m.Groups["filter"].Value, $@"^when\s*\(\s*{Regex.Escape(variable)}{TypeListFilter}"))
                continue;

            var text = Regex.Replace(m.Value[..^1] + body, @"\s+", " ");
            result.Add((ConventionSource.LineOf(source, m.Index), text.Length > 160 ? text[..160] + "..." : text));
        }

        return result;
    }
}
