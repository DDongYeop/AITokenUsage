using AITokenUsage.Core.Abstractions;
using AITokenUsage.Core.Providers;

namespace AITokenUsage;

/// <summary>
/// FileSystemWatchers for every provider WatchPath. An existing directory is watched recursively; anything else
/// (file or not-yet-existing path) through its parent directory with a name filter. Paths whose parent does not
/// exist yet are picked up by the 60 s rescan. Debounce, matching and rate-limit checks are UsageService's job.
/// </summary>
internal sealed class ProviderPathWatcher : IDisposable
{
    public static readonly TimeSpan RescanInterval = TimeSpan.FromSeconds(60);

    private readonly IReadOnlyList<string> _watchPaths;
    private readonly Action<string> _onPathChanged;
    private readonly IAppLog _log;
    private readonly object _gate = new();
    private readonly Dictionary<WatchTarget, FileSystemWatcher> _watchers = new();
    private Timer? _timer;
    private int _scanning;
    private bool _disposed;

    public ProviderPathWatcher(IEnumerable<IUsageProvider> providers, Action<string> onPathChanged, IAppLog log)
    {
        _watchPaths = providers
            .SelectMany(p => p.WatchPaths)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        _onPathChanged = onPathChanged;
        _log = log;
    }

    /// <summary>Starts the first scan on the thread pool, then rescans every 60 s.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_disposed || _timer is not null)
            {
                return;
            }

            _timer = new Timer(_ => Rescan(), null, TimeSpan.Zero, RescanInterval);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _timer?.Dispose();
            foreach (FileSystemWatcher watcher in _watchers.Values)
            {
                watcher.Dispose();
            }

            _watchers.Clear();
        }
    }

    private void Rescan()
    {
        if (Interlocked.Exchange(ref _scanning, 1) == 1)
        {
            return;
        }

        try
        {
            var desired = new HashSet<WatchTarget>();
            foreach (string path in _watchPaths)
            {
                if (Resolve(path) is WatchTarget target)
                {
                    desired.Add(target);
                }
            }

            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                foreach (WatchTarget stale in _watchers.Keys.Where(t => !desired.Contains(t)).ToArray())
                {
                    _watchers[stale].Dispose();
                    _watchers.Remove(stale);
                }

                foreach (WatchTarget target in desired)
                {
                    if (!_watchers.ContainsKey(target) && TryCreate(target) is FileSystemWatcher watcher)
                    {
                        _watchers[target] = watcher;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _log.Warn("path watcher rescan failed", ex);
        }
        finally
        {
            Volatile.Write(ref _scanning, 0);
        }
    }

    private static WatchTarget? Resolve(string path)
    {
        string full;
        try
        {
            full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        if (Directory.Exists(full))
        {
            return new WatchTarget(full, "*", Recursive: true);
        }

        string? parent = Path.GetDirectoryName(full);
        string name = Path.GetFileName(full);
        if (string.IsNullOrEmpty(parent) || string.IsNullOrEmpty(name) || !Directory.Exists(parent))
        {
            return null;
        }

        return new WatchTarget(parent, name, Recursive: false);
    }

    private FileSystemWatcher? TryCreate(WatchTarget target)
    {
        FileSystemWatcher? watcher = null;
        try
        {
            watcher = new FileSystemWatcher(target.Folder, target.Filter)
            {
                IncludeSubdirectories = target.Recursive,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite
                    | NotifyFilters.Size | NotifyFilters.CreationTime,
            };
            watcher.Changed += OnFileEvent;
            watcher.Created += OnFileEvent;
            watcher.Deleted += OnFileEvent;
            watcher.Renamed += OnRenamed;
            watcher.Error += OnWatcherError;
            watcher.EnableRaisingEvents = true;
            _log.Info($"watching {target.Folder} (filter {target.Filter}, recursive {target.Recursive})");
            return watcher;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            watcher?.Dispose();
            _log.Warn($"cannot watch {target.Folder}", ex);
            return null;
        }
    }

    private void OnFileEvent(object sender, FileSystemEventArgs e) => Notify(e.FullPath);

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        Notify(e.OldFullPath);
        Notify(e.FullPath);
    }

    private void Notify(string path)
    {
        try
        {
            _onPathChanged(path);
        }
        catch (Exception ex)
        {
            _log.Warn("path change handler failed", ex);
        }
    }

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        _log.Warn("file watcher error; it will be recreated", e.GetException());
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            foreach (KeyValuePair<WatchTarget, FileSystemWatcher> pair in _watchers.Where(p => ReferenceEquals(p.Value, sender)).ToArray())
            {
                pair.Value.Dispose();
                _watchers.Remove(pair.Key);
            }
        }

        ThreadPool.QueueUserWorkItem(_ => Rescan());
    }

    private readonly record struct WatchTarget(string Folder, string Filter, bool Recursive);
}
