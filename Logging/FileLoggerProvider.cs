using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace MyApp.Logging;

public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly BlockingCollection<string> _queue = new(new ConcurrentQueue<string>());
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _writerTask;
    private readonly string _logPath;

    public FileLoggerProvider(string logPath)
    {
        _logPath = logPath;
        Directory.CreateDirectory(Path.GetDirectoryName(_logPath)!);
        _writerTask = Task.Run(WriterLoopAsync);
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(categoryName, _queue);

    public void Dispose()
    {
        // CompleteAdding alone ends the consuming enumerable once the backlog is drained.
        // Cancelling first would discard exactly the last lines written during shutdown -
        // which are the ones that explain why shutdown happened.
        _queue.CompleteAdding();
        try
        {
            if (!_writerTask.Wait(TimeSpan.FromSeconds(3)))
            {
                _cts.Cancel();
            }
        }
        catch
        {
            // ignore shutdown race
        }

        _cts.Dispose();
        _queue.Dispose();
    }

    private async Task WriterLoopAsync()
    {
        await using var stream = new FileStream(_logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        await using var writer = new StreamWriter(stream);

        try
        {
            foreach (var line in _queue.GetConsumingEnumerable(_cts.Token))
            {
                await writer.WriteLineAsync(line);
                await writer.FlushAsync();
            }
        }
        catch (OperationCanceledException)
        {
            // no-op
        }
    }

    private sealed class FileLogger(string categoryName, BlockingCollection<string> queue) : ILogger
    {
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var message = formatter(state, exception);
            if (string.IsNullOrWhiteSpace(message))
            {
                return;
            }

            var line = $"{DateTimeOffset.UtcNow:O} [{logLevel}] {categoryName} :: {message}";
            if (exception is not null)
            {
                line += $" | ex={exception}";
            }

            try
            {
                queue.Add(line);
            }
            catch (Exception)
            {
                // The provider is being disposed; a log line lost at shutdown must not
                // become an exception inside whatever was being logged.
            }
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        public void Dispose() { }
    }
}
