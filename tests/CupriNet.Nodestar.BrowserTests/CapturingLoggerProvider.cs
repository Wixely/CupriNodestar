using Microsoft.Extensions.Logging;

namespace CupriNet.Nodestar.BrowserTests;

/// <summary>Routes the node's log into the test, so a failure can show why rather than only what.</summary>
internal sealed class CapturingLoggerProvider(Action<string> sink) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new Capturing(categoryName, sink);

    public void Dispose() { }

    private sealed class Capturing(string category, Action<string> sink) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(level)) return;
            var line = $"[{level}] {category}: {formatter(state, error)}";
            if (error is not null) line += $" :: {error.GetType().Name}: {error.Message}";
            sink(line);
        }
    }
}
