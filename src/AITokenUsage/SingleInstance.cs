using System.Windows.Threading;

namespace AITokenUsage;

/// <summary>
/// Single instance via a named mutex. A second instance signals the named event (first instance opens settings)
/// and exits. Create and dispose on the UI thread: the mutex is owned by the thread that created it.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    public const string MutexName = @"Local\AITokenUsage.SingleInstance";
    public const string ShowSettingsEventName = @"Local\AITokenUsage.ShowSettings";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _showSettings;
    private readonly ManualResetEvent _stop = new(false);
    private Thread? _listener;
    private bool _disposed;

    private SingleInstance(Mutex mutex, EventWaitHandle showSettings)
    {
        _mutex = mutex;
        _showSettings = showSettings;
    }

    /// <summary>Returns the owner object for the first instance; for a second instance signals the first and returns null.</summary>
    public static SingleInstance? TryAcquire()
    {
        var mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);

        // Both instances open-or-create the event, so the signal is never lost even if the first
        // instance has not created it yet.
        var showSettings = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSettingsEventName);
        if (createdNew)
        {
            return new SingleInstance(mutex, showSettings);
        }

        showSettings.Set();
        showSettings.Dispose();
        mutex.Dispose();
        return null;
    }

    /// <summary>Waits for the signal on a background thread and invokes the callback on the dispatcher.</summary>
    public void StartListening(Dispatcher dispatcher, Action onShowSettings)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_listener is not null)
        {
            return;
        }

        _listener = new Thread(() => Listen(dispatcher, onShowSettings))
        {
            IsBackground = true,
            Name = "AITokenUsage.SingleInstance",
        };
        _listener.Start();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stop.Set();
        bool stopped = _listener is null || _listener.Join(TimeSpan.FromSeconds(1));
        if (stopped)
        {
            _showSettings.Dispose();
            _stop.Dispose();
        }

        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Not owned by this thread; the handle close below releases it anyway.
        }

        _mutex.Dispose();
    }

    private void Listen(Dispatcher dispatcher, Action onShowSettings)
    {
        WaitHandle[] handles = [_stop, _showSettings];
        while (WaitHandle.WaitAny(handles) == 1)
        {
            if (dispatcher.HasShutdownStarted)
            {
                return;
            }

            dispatcher.BeginInvoke(DispatcherPriority.Normal, onShowSettings);
        }
    }
}
