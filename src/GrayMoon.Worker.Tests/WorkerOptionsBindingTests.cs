using System.Reflection;
using GrayMoon.Common;
using GrayMoon.Common.Git;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace GrayMoon.Worker.Tests;

/// <summary>
/// The Worker is a Native AOT exe, so its configuration binding is generated at build time (the generator is on
/// in every build of the Worker project, also the one these tests load). A property the generator skips keeps its
/// default and nothing fails. These tests give every property of every options class a value that is not its
/// default and read it back through the Worker's own binding calls.
/// </summary>
public class WorkerOptionsBindingTests
{
    [Fact]
    public void Every_property_of_every_options_class_is_bound_from_configuration()
    {
        var settings = new Dictionary<string, string?>();
        var expected = new List<(Type Options, PropertyInfo Property, object Value)>();
        AddSection<WorkerOptions>(WorkerOptions.SectionName, settings, expected);
        AddSection<GitChangesOptions>($"{WorkerOptions.SectionName}:GitChanges", settings, expected);
        AddSection<GitProcessOptions>($"{WorkerOptions.SectionName}:GitProcess", settings, expected);
        // ProcessExecutionOptions reads the GitProcess section; its one property has the same name there.
        expected.Add((typeof(ProcessExecutionOptions), typeof(ProcessExecutionOptions).GetProperty(nameof(ProcessExecutionOptions.DefaultTimeoutSeconds))!,
            int.Parse(settings[$"{WorkerOptions.SectionName}:GitProcess:{nameof(GitProcessOptions.DefaultTimeoutSeconds)}"]!)));
        Assert.Single(BindableProperties(typeof(ProcessExecutionOptions)));

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        using var services = new ServiceCollection().AddWorkerOptions(configuration).BuildServiceProvider();
        var bound = new Dictionary<Type, object>
        {
            [typeof(WorkerOptions)] = services.GetRequiredService<IOptions<WorkerOptions>>().Value,
            [typeof(GitChangesOptions)] = services.GetRequiredService<IOptions<GitChangesOptions>>().Value,
            [typeof(GitProcessOptions)] = services.GetRequiredService<IOptions<GitProcessOptions>>().Value,
            [typeof(ProcessExecutionOptions)] = services.GetRequiredService<IOptions<ProcessExecutionOptions>>().Value
        };

        var notBound = expected
            .Where(e => !Same(e.Value, e.Property.GetValue(bound[e.Options])))
            .Select(e => $"{e.Options.Name}.{e.Property.Name}")
            .ToList();

        Assert.True(expected.Count > 30, $"Only {expected.Count} properties were checked.");
        Assert.True(notBound.Count == 0, "Not bound from configuration: " + string.Join(", ", notBound));
    }

    [Fact]
    public void The_run_verb_reads_its_options_from_configuration()
    {
        var settings = new Dictionary<string, string?>();
        var expected = new List<(Type Options, PropertyInfo Property, object Value)>();
        AddSection<WorkerOptions>(WorkerOptions.SectionName, settings, expected);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        var options = WorkerOptionsSetup.ReadWorkerOptions(configuration);

        var notBound = expected.Where(e => !Same(e.Value, e.Property.GetValue(options))).Select(e => e.Property.Name).ToList();
        Assert.True(notBound.Count == 0, "Not bound from configuration: " + string.Join(", ", notBound));
    }

    private static IEnumerable<PropertyInfo> BindableProperties(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.SetMethod is { IsPublic: true });

    /// <summary>Adds a value that differs from the default for every settable property of <typeparamref name="T"/>.</summary>
    private static void AddSection<T>(string section, Dictionary<string, string?> settings, List<(Type, PropertyInfo, object)> expected) where T : new()
    {
        var defaults = new T();
        foreach (var property in BindableProperties(typeof(T)))
        {
            var current = property.GetValue(defaults);
            var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            var key = $"{section}:{property.Name}";
            if (type == typeof(int))
            {
                var value = (current as int? ?? 0) + 7;
                settings[key] = value.ToString();
                expected.Add((typeof(T), property, value));
            }
            else if (type == typeof(bool))
            {
                var value = !(current as bool? ?? false);
                settings[key] = value.ToString();
                expected.Add((typeof(T), property, value));
            }
            else if (type == typeof(string))
            {
                var value = "http://bound.example:1234/" + property.Name;
                settings[key] = value;
                expected.Add((typeof(T), property, value));
            }
            else if (type == typeof(int[]))
            {
                var value = new[] { 11, 22, 33, 44 };
                for (var i = 0; i < value.Length; i++)
                    settings[$"{key}:{i}"] = value[i].ToString();
                expected.Add((typeof(T), property, value));
            }
            else
            {
                // A new property type: teach this test to give it a value, so it is checked too.
                Assert.Fail($"{typeof(T).Name}.{property.Name} has type {property.PropertyType.Name}, which this test cannot set yet.");
            }
        }
    }

    private static bool Same(object expected, object? actual) =>
        expected is int[] expectedArray
            ? actual is int[] actualArray && ContainsInOrder(actualArray, expectedArray)
            : Equals(expected, actual);

    /// <summary>The binder appends to an array that has default entries, so the configured values are its tail.</summary>
    private static bool ContainsInOrder(int[] actual, int[] expected) =>
        actual.Length >= expected.Length && actual[^expected.Length..].SequenceEqual(expected);
}
