namespace NResilience.Docs;

/// <summary>
///     <c>Explain()</c>: what the policy will do, in the text a person reads.
/// </summary>
/// <remarks>
///     The sample output on the page is <c>explain-api.txt</c>, and the test below asserts that
///     <c>Explain()</c> prints exactly it. So the page cannot claim output the method does not produce,
///     and a change to the renderer that nobody meant to publish fails here first.
/// </remarks>
public sealed class Explaining
{
    [Fact]
    public void A_policy_can_say_what_it_will_do()
    {
        // <snippet:explain-call>
        var api = Resilience.Http with { Deadline = TimeSpan.FromSeconds(value: 5), Name = "api" };

        // At a REPL, or once at startup. Nothing here contacts a dependency or runs your callback.
        Console.WriteLine(value: api.Explain());

        // The allocation-free form, for a log that wants it on a stream rather than on the heap.
        api.Explain(writer: Console.Out);

        // </snippet:explain-call>

        Assert.Equal(expected: Published(), actual: api.Explain());
    }

    /// <summary>
    ///     The distinction every support question turns on: the record says what was configured, and
    ///     <c>Measured</c> says what is in effect. <c>Explain()</c> prints both, side by side.
    /// </summary>
    [Fact]
    public void The_explanation_separates_what_is_configured_from_what_is_in_effect()
    {
        // <snippet:explain-configured-versus-measured>
        var api = Resilience.Http with { AttemptTimeout = TimeSpan.FromSeconds(value: 30) };

        // Configured: 30s. In effect: nothing yet - the ceiling is cold until it has samples, and
        // until then the attempt gets the 30 seconds. The "Measured now" block reports both.
        var configured = api.AttemptTimeout;
        var inEffect = api.Measured.AttemptCeiling;

        // </snippet:explain-configured-versus-measured>

        Assert.Equal(expected: TimeSpan.FromSeconds(value: 30), actual: configured);
        Assert.Null(@object: inEffect);
        Assert.Contains(expectedSubstring: "cold", actualString: api.Explain(), comparisonType: StringComparison.Ordinal);
    }

    /// <summary>The sample output the page shows, read from the file that is inlined into it.</summary>
    private static string Published()
    {
        var directory = new DirectoryInfo(path: AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(path: Path.Combine(path1: directory.FullName, path2: "NResilience.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(@object: directory);

        var file = Path.Combine(directory.FullName, "tests", "NResilience.Docs", "explain-api.txt");
        return File.ReadAllText(path: file).Replace(oldValue: "\r\n", newValue: "\n", comparisonType: StringComparison.Ordinal).TrimEnd('\n');
    }
}
