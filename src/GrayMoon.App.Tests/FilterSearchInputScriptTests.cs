using System.Text;
using System.Text.Json;
using GrayMoon.Common.Search;
using Jint;

namespace GrayMoon.App.Tests;

/// <summary>
/// The filter box colors the text and marks invalid syntax in the browser
/// (<c>wwwroot/js/filterSearchInput.js</c>), so typing never waits on the server. The filtering itself
/// is done by <see cref="FilterSearchExpression"/>. These tests run the real script and hold its
/// <c>scan</c> to that parser: a query the box shows as valid is one the parser accepts, and the reverse.
/// </summary>
public sealed class FilterSearchInputScriptTests
{
    private const int Plain = 0;
    private const int And = 1;
    private const int Or = 2;
    private const int Paren = 3;
    private const int Field = 4;

    private static readonly Engine Script = LoadScript();
    private static readonly Lock ScriptGate = new();

    private static Engine LoadScript()
    {
        var engine = new Engine();
        engine.Execute("var window = globalThis;");
        engine.Execute(File.ReadAllText(Path.Combine(
            Conventions.ConventionSource.AppDirectory, "wwwroot", "js", "filterSearchInput.js")));

        // One buffer for every call, as the box keeps one per input: a scan must not depend on what
        // the previous scan left in it.
        engine.Execute("""
            var gmOut = { kinds: [], ends: [], count: 0 };
            function gmValid(text) {
                return window.grayMoonFilterSearch.scan(text, gmOut);
            }
            function gmRuns(text) {
                if (!window.grayMoonFilterSearch.scan(text, gmOut)) {
                    return null;
                }
                var runs = [];
                var start = 0;
                for (var i = 0; i < gmOut.count; i++) {
                    runs.push([gmOut.kinds[i], text.substring(start, gmOut.ends[i])]);
                    start = gmOut.ends[i];
                }
                return JSON.stringify(runs);
            }
            """);
        return engine;
    }

    private static bool ScriptSaysValid(string text)
    {
        lock (ScriptGate)
        {
            return Script.Invoke("gmValid", text).AsBoolean();
        }
    }

    private static (int Kind, string Text)[] ValidRuns(string text)
    {
        var runs = ScriptRuns(text);
        Assert.NotNull(runs);
        return runs;
    }

    private static (int Kind, string Text)[]? ScriptRuns(string text)
    {
        string? json;
        lock (ScriptGate)
        {
            var result = Script.Invoke("gmRuns", text);
            json = result.IsNull() ? null : result.AsString();
        }

        if (json is null)
        {
            return null;
        }

        using var document = JsonDocument.Parse(json);
        return document.RootElement.EnumerateArray()
            .Select(run => (run[0].GetInt32(), run[1].GetString()!))
            .ToArray();
    }

    [Fact]
    public void Runs_color_operators_parentheses_and_fields()
    {
        var runs = ValidRuns("api or (topic:web and gray)");

        Assert.Equal(
            [
                (Plain, "api "),
                (Or, "or"),
                (Plain, " "),
                (Paren, "("),
                (Field, "topic:web"),
                (Plain, " "),
                (And, "and"),
                (Plain, " gray"),
                (Paren, ")"),
            ],
            runs);
    }

    /// <summary>Neighbours of one color are one span, so a keystroke changes as few nodes as possible.</summary>
    [Fact]
    public void Neighbours_of_the_same_kind_are_one_run()
    {
        Assert.Equal([(Plain, "gray  api\tweb")], ValidRuns("gray  api\tweb"));
        Assert.Equal([(Paren, "(("), (Plain, "a"), (Paren, "))")], ValidRuns("((a))"));
    }

    [Theory]
    [InlineData("AND", And)]
    [InlineData("Or", Or)]
    [InlineData("android", Plain)]
    [InlineData("oracle", Plain)]
    [InlineData("topic:web", Field)]
    [InlineData("a:b:c", Field)]
    [InlineData(":web", Plain)]
    [InlineData("topic:", Plain)]
    [InlineData(":", Plain)]
    public void Second_word_gets_the_expected_kind(string word, int expectedKind)
    {
        // "x <word> y" keeps the query valid whichever kind the word is.
        var runs = ScriptRuns("x(" + word + ")y") ?? ScriptRuns("x " + word + " y");

        Assert.NotNull(runs);
        Assert.Contains(runs, run => run.Text.Contains(word) && run.Kind == expectedKind);
    }

    [Fact]
    public void Runs_always_spell_the_whole_text()
    {
        foreach (var query in GenerateQueries(seed: 7, count: 3_000))
        {
            var runs = ScriptRuns(query);
            if (runs is not null)
            {
                Assert.Equal(query, string.Concat(runs.Select(run => run.Text)));
            }
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("gray api")]
    [InlineData("zz or api and gray")]
    [InlineData("(zz or api) and gray")]
    [InlineData("a (b)")]
    [InlineData("(a)b")]
    [InlineData("(api")]
    [InlineData("api)")]
    [InlineData("()")]
    [InlineData("( )")]
    [InlineData("a and")]
    [InlineData("a or")]
    [InlineData("and a")]
    [InlineData("or")]
    [InlineData("a and or b")]
    [InlineData("a and and b")]
    [InlineData("a or (and b)")]
    [InlineData("(a or b) (c)")]
    public void Script_and_parser_agree_on_validity(string query)
    {
        Assert.Equal(FilterSearchExpression.IsValidQuery(query), ScriptSaysValid(query));
    }

    [Fact]
    public void Script_and_parser_agree_on_validity_for_generated_queries()
    {
        foreach (var query in GenerateQueries(seed: 20261120, count: 25_000))
        {
            var expected = FilterSearchExpression.IsValidQuery(query);
            Assert.True(
                expected == ScriptSaysValid(query),
                $"Parser says {(expected ? "valid" : "invalid")}, script disagrees: \"{query}\"");
        }
    }

    [Fact]
    public void Script_and_parser_agree_at_the_nesting_limit()
    {
        foreach (var depth in new[] { 1, FilterSearchExpression.MaxNestingDepth, FilterSearchExpression.MaxNestingDepth + 1, 500 })
        {
            var query = new string('(', depth) + "a" + new string(')', depth);
            Assert.Equal(FilterSearchExpression.IsValidQuery(query), ScriptSaysValid(query));
        }
    }

    /// <summary>
    /// A token ends at whitespace. The script has its own table of whitespace characters, and it must be
    /// <see cref="char.IsWhiteSpace(char)"/>: "a?or" is one word when ? is not whitespace, and a dangling
    /// operator (invalid) when it is.
    /// </summary>
    [Fact]
    public void Script_splits_tokens_at_the_same_characters_as_the_parser()
    {
        for (var code = 0; code <= char.MaxValue; code++)
        {
            var c = (char)code;
            if (c is '(' or ')' || char.IsSurrogate(c))
            {
                continue;
            }

            var query = "a" + c + "or";
            Assert.True(
                FilterSearchExpression.IsValidQuery(query) == ScriptSaysValid(query),
                $"U+{code:X4} is whitespace for only one of the two.");
        }
    }

    private static IEnumerable<string> GenerateQueries(int seed, int count)
    {
        string[] pieces =
        [
            "a", "gray", "and", "AND", "or", "Or", "(", ")", "(", ")", " ", " ", "  ", "\t", " ",
            "topic:web", "ro:yes", ":", ":x", "x:", "android", "nor", "　", "é",
        ];

        var random = new Random(seed);
        var builder = new StringBuilder();
        for (var n = 0; n < count; n++)
        {
            builder.Clear();
            var length = random.Next(0, 12);
            for (var i = 0; i < length; i++)
            {
                builder.Append(pieces[random.Next(pieces.Length)]);
            }

            yield return builder.ToString();
        }
    }
}
