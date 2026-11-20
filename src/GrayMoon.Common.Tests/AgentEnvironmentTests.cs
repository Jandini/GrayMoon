using GrayMoon.Common.Ai;

namespace GrayMoon.Common.Tests;

public sealed class AgentEnvironmentTests
{
    private static readonly AgentEnvironmentFix SystemCa = new("NODE_OPTIONS", "--use-system-ca", AppendToExisting: true);

    private static AgentEnvironmentVariable Var(string scope, string name, string value) => new(scope, name, value);

    [Theory]
    [InlineData("NODE_OPTIONS")]
    [InlineData("_private")]
    [InlineData("a1")]
    public void A_name_of_letters_digits_and_underscores_is_valid(string name) =>
        Assert.Null(AgentEnvironment.NameError(name));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1ABC")]
    [InlineData("A B")]
    [InlineData("A=B")]
    [InlineData("A%B%")]
    [InlineData("A-B")]
    public void Any_other_name_is_refused(string? name) =>
        Assert.NotNull(AgentEnvironment.NameError(name));

    [Fact]
    public void A_value_is_one_line_and_may_be_empty()
    {
        Assert.Null(AgentEnvironment.ValueError(""));
        Assert.Null(AgentEnvironment.ValueError("a%PATH%b & \"q\" ^ | c"));
        Assert.NotNull(AgentEnvironment.ValueError("one\ntwo"));
        Assert.NotNull(AgentEnvironment.ValueError("nul\0"));
        Assert.NotNull(AgentEnvironment.ValueError(new string('x', AgentEnvironment.MaxValueLength + 1)));
    }

    [Fact]
    public void A_name_can_be_listed_once_per_scope_whatever_its_case()
    {
        Assert.Null(AgentEnvironment.ListError(
        [
            Var(AgentEnvironmentScopes.All, "NODE_OPTIONS", "a"),
            Var(AiProviderIds.Cursor, "NODE_OPTIONS", "b"),
            Var(AiProviderIds.Claude, "NODE_OPTIONS", "c"),
        ]));

        var error = AgentEnvironment.ListError(
        [
            Var(AiProviderIds.Cursor, "NODE_OPTIONS", "a"),
            Var(AiProviderIds.Cursor, "node_options", "b"),
        ]);
        Assert.Contains("listed twice", error);
    }

    [Fact]
    public void An_unknown_scope_a_bad_row_and_too_many_rows_are_refused()
    {
        Assert.NotNull(AgentEnvironment.ListError([Var("vscode", "A", "1")]));
        Assert.NotNull(AgentEnvironment.ListError([Var(AgentEnvironmentScopes.All, "", "1")]));
        Assert.NotNull(AgentEnvironment.ListError([Var(AgentEnvironmentScopes.All, "A", "1\n2")]));
        Assert.NotNull(AgentEnvironment.ListError(
            Enumerable.Range(0, AgentEnvironment.MaxVariables + 1).Select(i => Var(AgentEnvironmentScopes.All, $"V{i}", "1")).ToList()));
    }

    [Fact]
    public void An_agent_gets_the_all_rows_and_its_own_rows_and_its_own_win()
    {
        AgentEnvironmentVariable[] list =
        [
            Var(AgentEnvironmentScopes.All, "SHARED", "all"),
            Var(AgentEnvironmentScopes.All, "NODE_OPTIONS", "--from-all"),
            Var(AiProviderIds.Cursor, "node_options", "--from-cursor"),
            Var(AiProviderIds.Claude, "CLAUDE_ONLY", "1"),
        ];

        var cursor = AgentEnvironment.Resolve(list, AiProviderIds.Cursor);
        Assert.Equal(2, cursor.Count);
        Assert.Equal("all", cursor["SHARED"]);
        Assert.Equal("--from-cursor", cursor["NODE_OPTIONS"]);

        var claude = AgentEnvironment.Resolve(list, AiProviderIds.Claude);
        Assert.Equal("--from-all", claude["NODE_OPTIONS"]);
        Assert.Equal("1", claude["CLAUDE_ONLY"]);

        Assert.Empty(AgentEnvironment.Resolve([], AiProviderIds.Codex));
    }

    [Fact]
    public void A_fix_adds_one_row_for_the_agent()
    {
        var result = AgentEnvironment.ApplyFix([Var(AiProviderIds.Claude, "OTHER", "1")], AiProviderIds.Cursor, SystemCa);

        Assert.Equal(Var(AiProviderIds.Cursor, "NODE_OPTIONS", "--use-system-ca"), result.Changed);
        Assert.Equal(2, result.Variables.Count);
        Assert.Null(AgentEnvironment.ListError(result.Variables));
    }

    [Fact]
    public void A_fix_never_lists_the_name_twice_and_keeps_the_options_already_set()
    {
        var result = AgentEnvironment.ApplyFix(
            [Var(AiProviderIds.Cursor, "Node_Options", "--max-old-space-size=4096")],
            AiProviderIds.Cursor,
            SystemCa);

        var row = Assert.Single(result.Variables);
        Assert.Equal("Node_Options", row.Name);
        Assert.Equal("--max-old-space-size=4096 --use-system-ca", row.Value);
        Assert.Equal(row, result.Changed);
    }

    [Fact]
    public void A_fix_for_an_agent_starts_from_the_all_row_it_would_otherwise_hide()
    {
        var all = Var(AgentEnvironmentScopes.All, "NODE_OPTIONS", "--max-old-space-size=4096");

        var result = AgentEnvironment.ApplyFix([all], AiProviderIds.Cursor, SystemCa);

        Assert.Equal([all, Var(AiProviderIds.Cursor, "NODE_OPTIONS", "--max-old-space-size=4096 --use-system-ca")], result.Variables);
        Assert.Equal("--max-old-space-size=4096 --use-system-ca", AgentEnvironment.Resolve(result.Variables, AiProviderIds.Cursor)["NODE_OPTIONS"]);
        Assert.Equal("--max-old-space-size=4096", AgentEnvironment.Resolve(result.Variables, AiProviderIds.Claude)["NODE_OPTIONS"]);
    }

    [Theory]
    [InlineData("cursor", "--use-system-ca")]
    [InlineData("cursor", "--trace-warnings --use-system-ca")]
    [InlineData("all", "--use-system-ca --trace-warnings")]
    public void A_fix_the_agent_already_runs_with_changes_nothing(string scope, string value)
    {
        AgentEnvironmentVariable[] list = [Var(scope, "NODE_OPTIONS", value)];

        var result = AgentEnvironment.ApplyFix(list, AiProviderIds.Cursor, SystemCa);

        Assert.Null(result.Changed);
        Assert.Same(list, result.Variables);
    }

    [Fact]
    public void A_replacing_fix_overwrites_the_agents_value()
    {
        var fix = new AgentEnvironmentFix("MODE", "safe", AppendToExisting: false);

        var result = AgentEnvironment.ApplyFix([Var(AiProviderIds.Cursor, "MODE", "fast")], AiProviderIds.Cursor, fix);

        Assert.Equal("safe", Assert.Single(result.Variables).Value);
        Assert.True(AgentEnvironment.IsInEffect(fix, "safe"));
        Assert.False(AgentEnvironment.IsInEffect(fix, "safe mode"));
    }

    [Fact]
    public void A_known_variable_set_on_the_host_is_listed_for_its_agent()
    {
        AgentEnvironmentVariable[] list = [Var(AgentEnvironmentScopes.All, "HTTPS_PROXY", "http://proxy:8080")];

        var result = AgentEnvironment.ImportHostValues(list,
        [
            new(AiProviderIds.Cursor, "NODE_OPTIONS", "--max-old-space-size=4096"),
            new(AiProviderIds.Claude, "CLAUDE_CODE_DISABLE_BG_SHELL_PRESSURE_REAP", "1"),
        ]);

        Assert.Equal(
        [
            Var(AiProviderIds.Cursor, "NODE_OPTIONS", "--max-old-space-size=4096"),
            Var(AiProviderIds.Claude, "CLAUDE_CODE_DISABLE_BG_SHELL_PRESSURE_REAP", "1"),
        ], result.Added);
        Assert.Equal(3, result.Variables.Count);
        Assert.Equal(list[0], result.Variables[0]);
        Assert.Null(AgentEnvironment.ListError(result.Variables));
    }

    [Theory]
    [InlineData("cursor", "NODE_OPTIONS")]
    [InlineData("cursor", "node_options")]
    [InlineData("all", "NODE_OPTIONS")]
    public void A_host_value_never_replaces_or_doubles_a_name_the_agent_already_gets_from_the_list(string scope, string name)
    {
        AgentEnvironmentVariable[] list = [Var(scope, name, "--from-the-list")];

        var result = AgentEnvironment.ImportHostValues(list, [new(AiProviderIds.Cursor, "NODE_OPTIONS", "--from-the-host")]);

        Assert.Empty(result.Added);
        Assert.Same(list, result.Variables);
    }

    [Fact]
    public void A_host_value_for_another_agent_does_not_block_this_one()
    {
        AgentEnvironmentVariable[] list = [Var(AiProviderIds.Claude, "NODE_OPTIONS", "--claude")];

        var result = AgentEnvironment.ImportHostValues(list, [new(AiProviderIds.Cursor, "NODE_OPTIONS", "--host")]);

        Assert.Equal(Var(AiProviderIds.Cursor, "NODE_OPTIONS", "--host"), Assert.Single(result.Added));
    }

    [Fact]
    public void Unusable_host_values_are_skipped_and_the_same_one_twice_is_listed_once()
    {
        var result = AgentEnvironment.ImportHostValues([],
        [
            new("vscode", "A", "1"),
            new(AiProviderIds.Cursor, "NOT A NAME", "1"),
            new(AiProviderIds.Cursor, "EMPTY", ""),
            new(AiProviderIds.Cursor, "TWO_LINES", "1\n2"),
            new(AiProviderIds.Cursor, "NODE_OPTIONS", "--a"),
            new(AiProviderIds.Cursor, "NODE_OPTIONS", "--b"),
        ]);

        Assert.Equal(Var(AiProviderIds.Cursor, "NODE_OPTIONS", "--a"), Assert.Single(result.Added));
    }

    [Fact]
    public void Host_values_stop_at_the_variable_limit()
    {
        var full = Enumerable.Range(0, AgentEnvironment.MaxVariables).Select(i => Var(AgentEnvironmentScopes.All, $"V{i}", "1")).ToList();

        var result = AgentEnvironment.ImportHostValues(full, [new(AiProviderIds.Cursor, "NODE_OPTIONS", "--a")]);

        Assert.Empty(result.Added);
        Assert.Same(full, result.Variables);
    }

    [Fact]
    public void A_fix_is_appended_to_the_value_that_came_from_the_host()
    {
        var imported = AgentEnvironment.ImportHostValues([], [new(AiProviderIds.Cursor, "NODE_OPTIONS", "--max-old-space-size=4096")]);

        var result = AgentEnvironment.ApplyFix(imported.Variables, AiProviderIds.Cursor, SystemCa);

        Assert.Equal("--max-old-space-size=4096 --use-system-ca", Assert.Single(result.Variables).Value);
    }

    [Fact]
    public void A_fix_from_the_wire_is_dropped_when_its_name_or_value_is_not_usable()
    {
        Assert.NotNull(new AgentEnvironmentFixDto { Name = "NODE_OPTIONS", Value = "--use-system-ca", AppendToExisting = true }.ToFix());
        Assert.Null(new AgentEnvironmentFixDto { Name = "A B", Value = "1" }.ToFix());
        Assert.Null(new AgentEnvironmentFixDto { Name = "A", Value = "" }.ToFix());
        Assert.Null(new AgentEnvironmentFixDto { Name = "A", Value = "1\n2" }.ToFix());
    }
}
