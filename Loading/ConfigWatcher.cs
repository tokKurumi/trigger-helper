using Microsoft.Extensions.Logging;

namespace TriggerHelper.Loading;

internal sealed class ConfigWatcher : IDisposable
{
    private const int DebounceMilliseconds = 300;

    private readonly FileSystemWatcher _watcher;
    private readonly Timer _debounceTimer;
    private readonly Action _onChange;
    private readonly ILogger _logger;
    private int _disposed;

    public ConfigWatcher(string directory, Action onChange, ILogger logger)
    {
        _onChange = onChange;
        _logger = logger;
        _debounceTimer = new Timer(OnDebounceFire, state: null, Timeout.Infinite, Timeout.Infinite);

        _watcher = new FileSystemWatcher(directory)
        {
            Filter = "*.jsonc",
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
        };

        _watcher.Changed += OnFsEvent;
        _watcher.Created += OnFsEvent;
        _watcher.Renamed += OnFsEvent;
        _watcher.Error += OnFsError;
        _watcher.EnableRaisingEvents = true;
    }

    private void OnFsEvent(object sender, FileSystemEventArgs e)
        => _debounceTimer.Change(DebounceMilliseconds, Timeout.Infinite);

    private void OnFsError(object sender, ErrorEventArgs e)
        => _logger.LogWarning(e.GetException(), "Config watcher error — events may have been lost.");

    private void OnDebounceFire(object? state)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        try
        {
            _onChange();
        }
        catch (Exception ex)
        {
            // The callback runs on a thread-pool thread, so an unhandled exception there would
            // crash the process. Catch and log; the caller can recover on the next change.
            _logger.LogError(ex, "Config-change callback threw.");
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _watcher.EnableRaisingEvents = false;
        _watcher.Changed -= OnFsEvent;
        _watcher.Created -= OnFsEvent;
        _watcher.Renamed -= OnFsEvent;
        _watcher.Error -= OnFsError;
        _watcher.Dispose();
        _debounceTimer.Dispose();
    }
}
