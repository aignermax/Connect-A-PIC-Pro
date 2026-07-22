using System.Collections.ObjectModel;
using CAP_Contracts.Logger;

namespace CAP_Core;

/// <summary>
/// Singleton service that collects application log entries for display in the error console UI.
/// Retains at most <see cref="MaxEntries"/> entries to prevent unbounded memory growth.
/// </summary>
public class ErrorConsoleService
{
    /// <summary>Maximum number of retained log entries.</summary>
    public const int MaxEntries = 100;

    private readonly ObservableCollection<Log> _entries = new();

    /// <summary>All current log entries, oldest first.</summary>
    public ReadOnlyObservableCollection<Log> Entries { get; }

    /// <summary>Raised whenever a new entry is added.</summary>
    public event EventHandler<Log>? EntryAdded;

    /// <summary>
    /// <see cref="Entries"/> is bound to the UI, so mutations must happen on the UI thread —
    /// but callers log from worker threads (simulation runs, python renders). The app installs
    /// its dispatcher here once at startup (Core cannot reference Avalonia); unset (tests,
    /// headless) logging stays synchronous on the calling thread.
    /// </summary>
    public Action<Action>? PostToUiThread { get; set; }

    /// <summary>Initializes a new <see cref="ErrorConsoleService"/>.</summary>
    public ErrorConsoleService()
    {
        Entries = new ReadOnlyObservableCollection<Log>(_entries);
    }

    /// <summary>Logs an entry at the specified severity level.</summary>
    /// <param name="level">Severity of the log entry.</param>
    /// <param name="message">Human-readable message.</param>
    public void Log(LogLevel level, string message)
    {
        var entry = new Log
        {
            Level = level,
            Message = message,
            TimeStamp = DateTime.Now,
            ClassName = ""
        };

        var post = PostToUiThread;
        if (post != null)
            post(() => Append(entry));
        else
            Append(entry);
    }

    private void Append(Log entry)
    {
        if (_entries.Count >= MaxEntries)
            _entries.RemoveAt(0);

        _entries.Add(entry);
        EntryAdded?.Invoke(this, entry);
    }

    /// <summary>Logs an error message, optionally including exception details.</summary>
    /// <param name="message">Context message describing where the error occurred.</param>
    /// <param name="ex">Optional exception to include in the log.</param>
    public void LogError(string message, Exception? ex = null)
    {
        var fullMessage = ex != null ? $"{message}: {ex.Message}" : message;
        Log(LogLevel.Error, fullMessage);
    }

    /// <summary>Logs a warning message.</summary>
    /// <param name="message">Warning description.</param>
    public void LogWarning(string message) => Log(LogLevel.Warn, message);

    /// <summary>Logs an informational message.</summary>
    /// <param name="message">Info description.</param>
    public void LogInfo(string message) => Log(LogLevel.Info, message);

    /// <summary>Removes all log entries from the console.</summary>
    public void Clear() => _entries.Clear();
}
