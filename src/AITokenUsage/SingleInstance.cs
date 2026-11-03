using System.Diagnostics;
using System.Windows.Threading;

namespace AITokenUsage;

/// <summary>
/// Single instance via a named mutex. A second instance signals the named event (first instance opens settings)
/// and exits. The installer signals the exit event (installer\AITokenUsage.iss) to close the app before it replaces
/// the files. Create and dispose on the UI thread: the mutex is owned by the thread that created it.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    public const string MutexName = @"Local\AITokenUsage.SingleInstance";
    public const string ShowSettingsEventName = @"Local\AITokenUsage.ShowSettings";
    public const string ExitEventName = @"Local\AITokenUsage.Exit";

    private static readonly TimeSpan RetryInterval = TimeSpan.FromMilliseconds(200);

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _showSettings;
    private readonly EventWaitHandle _exit;
    private readonly ManualResetEvent _stop = new(false);
    private Thread? _listener;
    private bool _disposed;

    private SingleInstance(Mutex mutex, EventWaitHandle showSettings, EventWaitHandle exit)
    {
        _mutex = mutex;
        _showSettings = showSettings;
        _exit = exit;
    }

    /// <summary>Returns the owner object for the first instance; for a second instance signals the first and returns null.</summary>
    public static SingleInstance? TryAcquire() => TryAcquire(TimeSpan.Zero);

    /// <summary>
    /// Like <see cref="TryAcquire()"/>, but when the mutex is already held it retries every 200 ms until
    /// <paramref name="waitForRelease"/> has elapsed (a restarting instance waits for its predecessor to exit).
    /// The first instance is signalled only after the wait ran out.
    /// </summary>
    public static SingleInstance? TryAcquire(TimeSpan waitForRelease) => TryAcquire(waitForRelease, signalFirstInstanceOnTimeout: true);

    /// <param name="signalFirstInstanceOnTimeout">
    /// False for a restarted instance: the owner is the exiting predecessor, so signalling it would only make it open a settings window.
    /// </param>
    public static SingleInstance? TryAcquire(TimeSpan waitForRelease, bool signalFirstInstanceOnTimeout)
    {
        long start = Stopwatch.GetTimestamp();
        Mutex mutex;
        bool createdNew;
        while (true)
        {
            mutex = new Mutex(initiallyOwned: true, MutexName, out createdNew);
            if (createdNew || Stopwatch.GetElapsedTime(start) >= waitForRelease)
            {
                break;
            }

            // Not created new means we do not own it: just close our handle and try again.
            mutex.Dispose();
            Thread.Sleep(RetryInterval);
        }

        // Both instances open-or-create the event, so the signal is never lost even if the first
        // instance has not created it yet.
        var showSettings = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSettingsEventName);
        if (createdNew)
        {
            // Only the owner opens the exit event: the installer treats "the event exists" as "this app can close itself".
            return new SingleInstance(mutex, showSettings, new EventWaitHandle(false, EventResetMode.AutoReset, ExitEventName));
        }

        if (signalFirstInstanceOnTimeout)
        {
            showSettings.Set();
        }

        showSettings.Dispose();
        mutex.Dispose();
        return null;
    }

    /// <summary>Waits for the signals on a background thread and invokes the matching callback on the dispatcher.</summary>
    public void StartListening(Dispatcher dispatcher, Action onShowSettings, Action onExit)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_listener is not null)
        {
            return;
        }

        _listener = new Thread(() => Listen(dispatcher, onShowSettings, onExit))
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
            _exit.Dispose();
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

    private void Listen(Dispatcher dispatcher, Action onShowSettings, Action onExit)
    {
        WaitHandle[] handles = [_stop, _showSettings, _exit];
        int signalled;
        while ((signalled = WaitHandle.WaitAny(handles)) != 0)
        {
            if (dispatcher.HasShutdownStarted)
            {
                return;
            }

            dispatcher.BeginInvoke(DispatcherPriority.Normal, signalled == 1 ? onShowSettings : onExit);
        }
    }
}
