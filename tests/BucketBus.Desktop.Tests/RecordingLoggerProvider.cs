using Microsoft.Extensions.Logging;

namespace BucketBus.Desktop.Tests;

internal sealed class RecordingLoggerProvider : ILoggerProvider
{
    public List<(string Message, Exception? Exception)> Entries { get; } = [];

    public ILogger CreateLogger(string categoryName) => new RecordingLogger(this);
    public void Dispose() { }

    private sealed class RecordingLogger(RecordingLoggerProvider provider) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => provider.Entries.Add((formatter(state, exception), exception));
    }
}
