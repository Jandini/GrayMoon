using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using GrayMoon.Abstractions.Notifications;
using GrayMoon.Abstractions.Worker;
using GrayMoon.Abstractions.Workspaces;
using GrayMoon.Common.Git;
using GrayMoon.Worker.Abstractions;
using GrayMoon.Worker.Jobs;
using GrayMoon.Worker.Jobs.Requests;
using GrayMoon.Worker.Jobs.Response;
using GrayMoon.Worker.Models;
using GrayMoon.Worker.Services;

namespace GrayMoon.Worker.Tests;

/// <summary>
/// The Worker is published as Native AOT, where JSON works only for the types named in
/// <see cref="WorkerJsonContext"/> and a missing type fails at run time, in that one command. These tests run on
/// the normal runtime and hold the context to two things: it names every type that crosses the hub, and the JSON
/// it produces is the JSON that reflection produced before, so the App needs no change.
/// </summary>
public class WorkerJsonContextTests
{
    /// <summary>The request and response type of every command, read from the dispatcher's constructor.</summary>
    internal static IReadOnlyList<(Type Request, Type Response)> CommandTypes() =>
        typeof(CommandDispatcher).GetConstructors().Single().GetParameters()
            .Select(p => p.ParameterType)
            .Where(t => t.IsGenericType && t.GetGenericTypeDefinition() == typeof(ICommandHandler<,>))
            .Select(t => (t.GetGenericArguments()[0], t.GetGenericArguments()[1]))
            .ToList();

    /// <summary>Types the Worker passes to or receives from the hub, the hook listener, GitVersion and the App's HTTP API.</summary>
    private static readonly Type[] OtherRootTypes =
    [
        typeof(string), typeof(int), typeof(JsonElement?), typeof(Dictionary<int, int>),
        typeof(WorkerCommandResponse), typeof(RepositorySyncNotification), typeof(GitChangesSnapshotNotification),
        typeof(GitVersionResult), typeof(WorkerTokenProvider.ConnectorTokenResponse), typeof(RepositoryOperationCapabilities)
    ];

    private static IEnumerable<Type> RootTypes() =>
        CommandTypes().SelectMany(p => new[] { p.Request, p.Response }).Concat(OtherRootTypes).Distinct();

    private static JsonSerializerOptions HubStyle(IJsonTypeInfoResolver resolver) => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        TypeInfoResolver = resolver
    };

    private static JsonSerializerOptions RequestStyle(IJsonTypeInfoResolver resolver) => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        TypeInfoResolver = resolver
    };

    [Fact]
    public void The_dispatcher_lists_commands()
    {
        // Guards the tests below: an empty list would make them pass without checking anything.
        Assert.True(CommandTypes().Count >= 60, $"Only {CommandTypes().Count} commands were found in the dispatcher's constructor.");
    }

    [Fact]
    public void Every_type_that_crosses_the_hub_is_in_the_context()
    {
        var missing = RootTypes()
            .Where(t => WorkerJsonContext.Default.GetTypeInfo(t) is null)
            .Select(t => t.Name)
            .ToList();

        Assert.True(missing.Count == 0,
            "Add [JsonSerializable(typeof(...))] to WorkerJsonContext for: " + string.Join(", ", missing));
    }

    [Fact]
    public void Every_command_the_factory_reads_is_a_command_of_the_dispatcher()
    {
        // The factory picks the generated type by name, so a request type it uses must be one the context was checked for.
        var requestTypes = CommandTypes().Select(p => p.Request).ToHashSet();
        var source = File.ReadAllText(Path.Combine(WorkerSourceDirectory(), "Services", "CommandJobFactory.cs"));
        var used = System.Text.RegularExpressions.Regex.Matches(source, @"WorkerJsonContext\.Default\.(\w+)")
            .Select(m => m.Groups[1].Value)
            .Distinct()
            .ToList();

        Assert.NotEmpty(used);
        var unknown = used.Where(name => requestTypes.All(t => t.Name != name)).ToList();
        Assert.True(unknown.Count == 0, "CommandJobFactory reads types no command handler takes: " + string.Join(", ", unknown));
        Assert.DoesNotContain("JsonSerializer.Deserialize<", source);
    }

    [Fact]
    public void Generated_json_has_the_same_shape_as_reflection_json()
    {
        var differences = new List<string>();
        foreach (var (style, name) in new (Func<IJsonTypeInfoResolver, JsonSerializerOptions>, string)[] { (HubStyle, "hub"), (RequestStyle, "request") })
        {
            var generated = style(WorkerJsonContext.Default);
            var reflected = style(new DefaultJsonTypeInfoResolver());
            var seen = new HashSet<Type>();
            foreach (var root in RootTypes())
                CompareShape(root, generated, reflected, name, root.Name, seen, differences);
        }

        Assert.True(differences.Count == 0, string.Join(Environment.NewLine, differences.Take(40)));
    }

    [Fact]
    public void A_default_instance_of_every_response_is_written_the_same_as_by_reflection()
    {
        var generated = HubStyle(WorkerJsonContext.Default);
        var reflected = HubStyle(new DefaultJsonTypeInfoResolver());
        var differences = new List<string>();
        foreach (var type in CommandTypes().Select(p => p.Response).Distinct())
        {
            var instance = type.GetConstructor(Type.EmptyTypes) is not null
                ? Activator.CreateInstance(type)!
                : RuntimeHelpers.GetUninitializedObject(type);

            // As the hub sends it: the response sits in the object-typed Data of the envelope.
            var envelope = new WorkerCommandResponse(true, instance, null);
            var expected = JsonSerializer.Serialize(envelope, reflected);
            var actual = JsonSerializer.Serialize(envelope, generated);
            if (expected != actual)
                differences.Add($"{type.Name}:{Environment.NewLine}  reflection: {expected}{Environment.NewLine}  generated:  {actual}");
        }

        Assert.True(differences.Count == 0, string.Join(Environment.NewLine, differences.Take(10)));
    }

    [Fact]
    public void A_request_is_read_the_same_as_by_reflection()
    {
        const string json = """
            {"workspaceName":"ws","RepositoryName":"repo","workspaceId":7,"repositoryId":9,"workspaceRoot":"C:/w","maxParallelOperations":3,
             "capabilities":{"usesRepositoryVersioning":true}}
            """;
        var generated = RequestStyle(WorkerJsonContext.Default);
        var reflected = RequestStyle(new DefaultJsonTypeInfoResolver());

        var viaContext = JsonSerializer.Deserialize(json, WorkerJsonContext.Default.SyncRepositoryRequest)!;
        var viaReflection = (SyncRepositoryRequest)JsonSerializer.Deserialize(json, typeof(SyncRepositoryRequest), reflected)!;

        Assert.Equal("repo", viaContext.RepositoryName);
        Assert.Equal(7, viaContext.WorkspaceId);
        Assert.Equal(JsonSerializer.Serialize(viaReflection, typeof(SyncRepositoryRequest), reflected),
            JsonSerializer.Serialize(viaContext, typeof(SyncRepositoryRequest), generated));
    }

    [Fact]
    public void The_hook_payload_is_read_with_exact_property_names()
    {
        var exact = JsonSerializer.Deserialize("""{"repositoryId":4,"workspaceId":5,"repositoryPath":"C:/r"}""", WorkerStrictJsonContext.Default.NotifyPayload)!;
        var otherCase = JsonSerializer.Deserialize("""{"RepositoryId":4,"WorkspaceId":5}""", WorkerStrictJsonContext.Default.NotifyPayload)!;

        Assert.Equal((4, 5, "C:/r"), (exact.RepositoryId, exact.WorkspaceId, exact.RepositoryPath));
        Assert.Equal((0, 0), (otherCase.RepositoryId, otherCase.WorkspaceId));
    }

    [Fact]
    public void A_failed_response_reaches_the_hub_as_failed()
    {
        var failed = new PushRepositoryResponse { Success = false, ErrorMessage = "remote rejected" };
        var failedWithoutText = new DetachRepositoriesResponse { Success = false };
        var succeeded = new PushRepositoryResponse { Success = true, ErrorMessage = "ignored" };

        Assert.Equal((false, "remote rejected"), Hosted.JobBackgroundService.GetCommandSuccessAndError(failed));
        Assert.Equal((false, "Command failed."), Hosted.JobBackgroundService.GetCommandSuccessAndError(failedWithoutText));
        Assert.Equal((true, null), Hosted.JobBackgroundService.GetCommandSuccessAndError(succeeded));
        Assert.Equal((true, null), Hosted.JobBackgroundService.GetCommandSuccessAndError(new GetHostInfoResponse()));
        Assert.Equal((true, null), Hosted.JobBackgroundService.GetCommandSuccessAndError(null));
    }

    private static string WorkerSourceDirectory([CallerFilePath] string thisFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", "GrayMoon.Worker"));

    private static void CompareShape(Type type, JsonSerializerOptions generated, JsonSerializerOptions reflected, string style, string path,
        HashSet<Type> seen, List<string> differences)
    {
        if (!seen.Add(type))
            return;

        JsonTypeInfo actual;
        var expected = reflected.GetTypeInfo(type);
        try
        {
            actual = generated.GetTypeInfo(type);
        }
        catch (NotSupportedException)
        {
            differences.Add($"[{style}] {path}: {type} is not in WorkerJsonContext");
            return;
        }

        if (expected.Kind != actual.Kind)
        {
            differences.Add($"[{style}] {path}: kind {actual.Kind}, reflection has {expected.Kind}");
            return;
        }

        if (expected.Kind == JsonTypeInfoKind.None)
        {
            // A leaf (number, string, enum, a type with its own converter): the same converter writes it.
            if (expected.Converter.GetType() != actual.Converter.GetType())
                differences.Add($"[{style}] {path}: converter {actual.Converter.GetType().Name}, reflection has {expected.Converter.GetType().Name}");
            return;
        }

        if (expected.Kind == JsonTypeInfoKind.Object)
        {
            static string Describe(JsonPropertyInfo p) =>
                $"{p.Name}:{p.PropertyType}:required={p.IsRequired}:get={p.Get is not null}:set={p.Set is not null}:numbers={p.NumberHandling}:converter={p.CustomConverter?.GetType().Name}:extension={p.IsExtensionData}";

            var expectedProperties = expected.Properties.Select(Describe).ToList();
            var actualProperties = actual.Properties.Select(Describe).ToList();
            if (!expectedProperties.SequenceEqual(actualProperties))
            {
                differences.Add($"[{style}] {path}: properties differ.{Environment.NewLine}  reflection: {string.Join(" | ", expectedProperties)}{Environment.NewLine}  generated:  {string.Join(" | ", actualProperties)}");
                return;
            }

            foreach (var property in expected.Properties)
                CompareShape(property.PropertyType, generated, reflected, style, $"{path}.{property.Name}", seen, differences);
            return;
        }

        if (expected.KeyType is not null)
            CompareShape(expected.KeyType, generated, reflected, style, path + "{key}", seen, differences);
        if (expected.ElementType is not null)
            CompareShape(expected.ElementType, generated, reflected, style, path + "[]", seen, differences);
    }
}

/// <summary>
/// The request and response types carry the facts the Worker used to read by property name (which Native AOT does
/// not support) through interfaces. A type that has the property but not the interface would silently lose the
/// behaviour, so each such type must implement it.
/// </summary>
public class CommandContractConventionTests
{
    private static bool HasProperty(Type type, string name, Type propertyType) =>
        type.GetProperty(name, propertyType) is { } property && property.PropertyType == propertyType;

    [Fact]
    public void Every_response_with_a_Success_property_reports_its_outcome()
    {
        var missing = WorkerJsonContextTests.CommandTypes().Select(p => p.Response).Distinct()
            .Where(t => HasProperty(t, "Success", typeof(bool)) && !typeof(ICommandOutcome).IsAssignableFrom(t))
            .Select(t => t.Name).ToList();

        Assert.True(missing.Count == 0, "Implement ICommandOutcome on: " + string.Join(", ", missing));
    }

    [Fact]
    public void Every_request_with_a_Workspace_id_is_counted_for_its_Workspace()
    {
        var missing = WorkerJsonContextTests.CommandTypes().Select(p => p.Request).Distinct()
            .Where(t => HasProperty(t, "WorkspaceId", typeof(int)) && !typeof(IWorkspaceScopedRequest).IsAssignableFrom(t))
            .Select(t => t.Name).ToList();

        Assert.True(missing.Count == 0, "Implement IWorkspaceScopedRequest on: " + string.Join(", ", missing));
    }

    [Fact]
    public void Every_request_with_a_repository_or_Workspace_name_labels_its_output()
    {
        var requests = WorkerJsonContextTests.CommandTypes().Select(p => p.Request).Distinct().ToList();
        var missing = requests
            .Where(t => HasProperty(t, "RepositoryName", typeof(string)) && !typeof(IRepositoryNamedRequest).IsAssignableFrom(t))
            .Select(t => t.Name + " (IRepositoryNamedRequest)")
            .Concat(requests
                .Where(t => HasProperty(t, "WorkspaceName", typeof(string)) && !typeof(IWorkspaceNamedRequest).IsAssignableFrom(t))
                .Select(t => t.Name + " (IWorkspaceNamedRequest)"))
            .ToList();

        Assert.True(missing.Count == 0, "Implement the interface on: " + string.Join(", ", missing));
    }

    [Fact]
    public void The_Workspace_of_a_command_job_comes_from_its_request()
    {
        var withWorkspace = JobEnvelope.Command(new CommandJob { RequestId = "1", Command = "SyncRepository", Request = new SyncRepositoryRequest { WorkspaceId = 12 } });
        var withoutWorkspace = JobEnvelope.Command(new CommandJob { RequestId = "2", Command = "GetHostInfo", Request = new GetHostInfoRequest() });

        Assert.Equal(12, withWorkspace.TryGetWorkspaceId());
        Assert.Null(withoutWorkspace.TryGetWorkspaceId());
    }
}

/// <summary>
/// Serilog writes <c>{@Value}</c> by reading the object's properties through reflection. In the Native AOT exe that
/// logs whatever survived trimming, so the Worker's log templates must not use it.
/// </summary>
public class WorkerLogTemplateConventionTests
{
    [Fact]
    public void No_Worker_log_template_destructures_an_object()
    {
        var violations = FindDestructuring(WorkerSources()).ToList();

        Assert.True(violations.Count == 0, "Log the JSON text or single values instead of {@...}:" + Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void The_scan_finds_a_destructuring_template_and_ignores_other_text()
    {
        var found = FindDestructuring(
        [
            ("A.cs", "logger.LogTrace(\"content: {@ResponseBody}\", body);"),
            ("B.cs", "/// The syntax is <c>{@Repo}</c>."),
            ("C.cs", "logger.LogInformation(\"{RequestId} done\", id);")
        ]).ToList();

        Assert.Equal(["A.cs:1"], found);
    }

    private static IEnumerable<string> FindDestructuring(IEnumerable<(string File, string Text)> sources)
    {
        foreach (var (file, text) in sources)
        {
            var lines = text.Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                if (lines[i].Contains("Log", StringComparison.Ordinal) && System.Text.RegularExpressions.Regex.IsMatch(lines[i], @"\.Log\w*\(.*\{@\w+"))
                    yield return $"{file}:{i + 1}";
            }
        }
    }

    private static IEnumerable<(string File, string Text)> WorkerSources([CallerFilePath] string thisFile = "")
    {
        var src = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, ".."));
        return new[] { "GrayMoon.Worker", "GrayMoon.Common", "GrayMoon.Abstractions" }
            .SelectMany(project => Directory.EnumerateFiles(Path.Combine(src, project), "*.cs", SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Select(f => (Path.GetFileName(f), File.ReadAllText(f)));
    }
}
