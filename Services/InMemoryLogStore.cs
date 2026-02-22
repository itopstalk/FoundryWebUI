using System.Collections.Concurrent;

namespace FoundryWebUI.Services;

/// <summary>Captures application log entries in a ring buffer for display in the Logs UI.</summary>
public static class InMemoryLogStore
{
    private static readonly ConcurrentQueue<LogEntry> _entries = new();
    private const int MaxEntries = 2000;

    public static void Add(string level, string category, string message)
    {
        _entries.Enqueue(new LogEntry
        {
            Time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"),
            Level = level,
            Category = category,
            Message = message
        });

        while (_entries.Count > MaxEntries)
            _entries.TryDequeue(out _);
    }

    public static List<LogEntry> GetEntries(int count)
    {
        return _entries.TakeLast(count).ToList();
    }

    public class LogEntry
    {
        public string Time { get; set; } = "";
        public string Level { get; set; } = "";
        public string Category { get; set; } = "";
        public string Message { get; set; } = "";
    }
}

/// <summary>ILogger provider that writes to the InMemoryLogStore.</summary>
public class InMemoryLoggerProvider : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new InMemoryLogger(categoryName);
    public void Dispose() { }
}

public class InMemoryLogger : ILogger
{
    private readonly string _category;
    public InMemoryLogger(string category) => _category = category;
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;
        var level = logLevel switch
        {
            LogLevel.Trace => "trace",
            LogLevel.Debug => "debug",
            LogLevel.Information => "info",
            LogLevel.Warning => "warn",
            LogLevel.Error => "error",
            LogLevel.Critical => "critical",
            _ => "info"
        };
        var message = formatter(state, exception);
        if (exception != null)
            message += $"\n{exception}";
        // Shorten the category for display
        var shortCat = _category.Contains('.') ? _category[((_category.LastIndexOf('.') + 1))..] : _category;
        InMemoryLogStore.Add(level, shortCat, message);
    }
}
