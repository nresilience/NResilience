using NResilience;

// The core library on its own: a policy value, a retried call, and the outcome without an exception.
// Runs against an in-process fake dependency, so it needs no network.
var api = Resilience.Default with
{
    Name = "sample",
    Attempts = 4,
    Deadline = TimeSpan.FromSeconds(5),
    Backoff = Backoff.Exponential(TimeSpan.FromMilliseconds(50)),
    Classifier = Classifier.Default.On<FlakyDependencyException>(Verdict.Transient),
    OnEvent = e => Console.WriteLine($"  {e}"),
};

Console.WriteLine("A call that fails twice and then succeeds:");
var flaky = new FakeDependency(2);
var value = await api.RunAsync(attempt => flaky.ReadAsync(attempt), CancellationToken.None);
Console.WriteLine($"  -> {value}");

Console.WriteLine();
Console.WriteLine("A call that never succeeds, reported rather than thrown:");
var broken = new FakeDependency(int.MaxValue);
var result = await api.TryRunAsync(attempt => broken.ReadAsync(attempt), CancellationToken.None);

Console.WriteLine($"  -> {result.StopReason}");
Console.WriteLine($"  -> {result.Attempts}");
Console.WriteLine($"  -> falling back to the cached value: {(result.TryGetValue(out var v) ? v : "(cached)")}");

Console.WriteLine();
Console.WriteLine("What the classifier will and will not retry:");
Console.WriteLine(api.Classifier);

internal sealed class FakeDependency(int failuresBeforeSuccess)
{
    private int _calls;

    internal Task<string> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return ++_calls > failuresBeforeSuccess
            ? Task.FromResult($"answered on attempt {_calls}")
            : Task.FromException<string>(new FlakyDependencyException($"attempt {_calls} failed"));
    }
}

internal sealed class FlakyDependencyException(string message) : Exception(message);
