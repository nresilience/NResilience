using Microsoft.Extensions.Logging;

namespace NResilience.Probes;

/// <summary>
///     A logger that carries nothing. Every <see cref="ILogger.IsEnabled" /> call answers false, which is
///     the state a process is in when nobody has turned the resilience category up - and therefore the
///     state the allocation gate has to price.
/// </summary>
internal sealed class SilentLogger : ILogger
{
    internal static readonly SilentLogger Instance = new();

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => false;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
    }
}
