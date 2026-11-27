using System.Reflection;
using System.Text.Json;

namespace GrayMoon.App.Tests;

/// <summary>
/// Counts the Worker commands a test recorded on <see cref="FakeWorkerBridge"/>. It is the App-side twin of the
/// Worker's <c>GitProcessCounter</c>: an assertion that an operation sent no <c>SyncRepository</c>, or exactly one
/// command per repository, is an assertion about work that did not happen, which elapsed time cannot show.
/// </summary>
public sealed class WorkerCommandCounts
{
    private readonly IReadOnlyList<(string Command, object Args)> _calls;

    private WorkerCommandCounts(IReadOnlyList<(string Command, object Args)> calls) => _calls = calls;

    /// <summary>A snapshot of every command recorded so far. Take it when the operation under test has ended.</summary>
    public static WorkerCommandCounts Of(FakeWorkerBridge bridge)
        => new(bridge.Calls.ToArray());

    /// <summary>A snapshot of the commands recorded after the first <paramref name="skip"/> ones (the count taken before the operation under test).</summary>
    public static WorkerCommandCounts Since(FakeWorkerBridge bridge, int skip)
        => new(bridge.Calls.ToArray().Skip(skip).ToArray());

    public int Total => _calls.Count;

    /// <summary>The distinct command names, in first-seen order.</summary>
    public IReadOnlyList<string> Commands
        => [.. _calls.Select(c => c.Command).Distinct(StringComparer.OrdinalIgnoreCase)];

    public int Count(string command)
        => _calls.Count(c => string.Equals(c.Command, command, StringComparison.OrdinalIgnoreCase));

    /// <summary>The argument objects of every recorded <paramref name="command"/>.</summary>
    public IReadOnlyList<object> ArgsOf(string command)
        => [.. _calls.Where(c => string.Equals(c.Command, command, StringComparison.OrdinalIgnoreCase)).Select(c => c.Args)];

    /// <summary>
    /// The values of one argument property (for example "repositoryName") over every recorded <paramref name="command"/>.
    /// The name is matched without case against the properties of an anonymous or typed argument object and the keys
    /// of a dictionary; a call without that property contributes null.
    /// </summary>
    public IReadOnlyList<string?> ArgValues(string command, string propertyName)
        => [.. ArgsOf(command).Select(a => ReadProperty(a, propertyName))];

    /// <summary>Fails with the recorded command list when any of <paramref name="commands"/> was sent.</summary>
    public void AssertNone(params string[] commands)
    {
        var sent = commands.Where(c => Count(c) > 0).Select(c => $"{c} x{Count(c)}").ToList();
        Assert.True(sent.Count == 0, $"Expected none of [{string.Join(", ", commands)}] but saw {string.Join(", ", sent)}. All commands: {Describe()}");
    }

    /// <summary>Fails with the recorded command list when <paramref name="command"/> was not sent exactly <paramref name="expected"/> times.</summary>
    public void AssertCount(string command, int expected)
        => Assert.True(Count(command) == expected, $"Expected {command} x{expected} but saw x{Count(command)}. All commands: {Describe()}");

    /// <summary>"Name xN" per command, for an assertion message.</summary>
    public string Describe()
        => _calls.Count == 0
            ? "(none)"
            : string.Join(", ", Commands.Select(c => $"{c} x{Count(c)}"));

    private static string? ReadProperty(object args, string propertyName)
    {
        switch (args)
        {
            case JsonElement { ValueKind: JsonValueKind.Object } json:
                foreach (var property in json.EnumerateObject())
                {
                    if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                        return property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : property.Value.ToString();
                }
                return null;
            case IEnumerable<KeyValuePair<string, object?>> pairs:
                foreach (var pair in pairs)
                {
                    if (string.Equals(pair.Key, propertyName, StringComparison.OrdinalIgnoreCase))
                        return pair.Value?.ToString();
                }
                return null;
            default:
                var property2 = args.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
                return property2?.GetValue(args)?.ToString();
        }
    }
}
