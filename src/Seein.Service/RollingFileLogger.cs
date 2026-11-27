using Microsoft.Extensions.Logging;

namespace Seein.Service;

public sealed class RollingFileLoggerProvider(string directory, long maxBytes = 5 * 1024 * 1024, int files = 5) : ILoggerProvider
{
    private readonly object _gate = new();
    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);
    public void Dispose() { }

    private void Write(string category, LogLevel level, string message, Exception? exception)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "service.log");
            if (File.Exists(path) && new FileInfo(path).Length >= maxBytes)
            {
                var oldest = Path.Combine(directory, $"service.{files}.log");
                if (File.Exists(oldest)) File.Delete(oldest);
                for (var i = files - 1; i >= 1; i--)
                {
                    var source = Path.Combine(directory, $"service.{i}.log");
                    var destination = Path.Combine(directory, $"service.{i + 1}.log");
                    if (File.Exists(source)) File.Move(source, destination, true);
                }
                File.Move(path, Path.Combine(directory, "service.1.log"), true);
            }
            var line = $"{DateTimeOffset.Now:O} [{level}] {category}: {message}";
            if (exception is not null) line += $" | {exception.GetType().Name}: {exception.Message}";
            File.AppendAllText(path, line + Environment.NewLine);
        }
    }

    private sealed class Logger(RollingFileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        { if (IsEnabled(logLevel)) provider.Write(category, logLevel, formatter(state, exception), exception); }
    }
}
