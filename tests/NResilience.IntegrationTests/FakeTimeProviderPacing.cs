using Microsoft.Extensions.Time.Testing;

namespace NResilience.IntegrationTests;

/// <summary>
///     Extension on <see cref="FakeTimeProvider" /> that advances the clock on a timer, so a test that
///     triggers rejection delays (which are served against the policy's clock) does not have to
///     interleave the advances by hand.
/// </summary>
internal static class FakeTimeProviderPacing
{
    /// <summary>
    ///     Starts a timer that advances the clock by <paramref name="interval" /> every
    ///     <paramref name="interval" /> of real time. Returns a disposable that stops the timer.
    /// </summary>
    internal static IDisposable StartPaceThread(this FakeTimeProvider time, TimeSpan interval)
    {
        var stopped = false;

        var thread = new Thread(() =>
        {
            while (!stopped)
            {
                Thread.Sleep(interval);
                time.Advance(interval);
            }
        })
        {
            IsBackground = true,
        };

        thread.Start();
        return new Pacer(() => stopped = true, thread);
    }

    private sealed class Pacer(Action stop, Thread thread) : IDisposable
    {
        public void Dispose()
        {
            stop();
            thread.Join(TimeSpan.FromSeconds(1));
        }
    }
}
