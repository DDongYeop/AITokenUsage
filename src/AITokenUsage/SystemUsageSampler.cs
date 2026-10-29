using System.Runtime.InteropServices;
using System.Windows.Threading;
using AITokenUsage.Core.Abstractions;
using AITokenUsage.Core.SystemUsage;

namespace AITokenUsage;

/// <summary>
/// Measures whole-PC CPU and memory use every <see cref="Interval"/> on the UI thread (design §2). Both Win32 calls
/// are microsecond system calls with no file or network I/O, so a DispatcherTimer is enough. UI thread only.
/// </summary>
public sealed partial class SystemUsageSampler : IDisposable
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);

    private readonly TimeProvider _time;
    private readonly IAppLog _log;
    private readonly DispatcherTimer _timer;
    private CpuTimes? _previousCpu;
    private bool _cpuWarned;
    private bool _memoryWarned;
    private bool _failureWarned;
    private bool _disposed;

    public SystemUsageSampler(Dispatcher dispatcher, TimeProvider time, IAppLog log)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(log);

        _time = time;
        _log = log;
        _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = Interval };
        _timer.Tick += OnTick;
    }

    /// <summary>Raised on the UI thread with each new measurement (the first one after Start has no CPU value).</summary>
    public event EventHandler<SystemUsageSample>? Sampled;

    public bool IsRunning => _timer.IsEnabled;

    /// <summary>Takes a first measurement right away (memory only: CPU needs two readings), then one every 2 s.</summary>
    public void Start()
    {
        if (_disposed || _timer.IsEnabled)
        {
            return;
        }

        _previousCpu = null;
        _timer.Start();
        SampleOnce();
    }

    /// <summary>Stops measuring. The next Start begins a fresh CPU baseline.</summary>
    public void Stop()
    {
        _timer.Stop();
        _previousCpu = null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Stop();
        _timer.Tick -= OnTick;
        Sampled = null;
    }

    private void OnTick(object? sender, EventArgs e) => SampleOnce();

    private void SampleOnce()
    {
        if (_disposed)
        {
            return;
        }

        SystemUsageSample? sample;
        try
        {
            sample = Measure();
        }
        catch (Exception ex)
        {
            // A DispatcherTimer tick must never throw into the dispatcher; skip this round.
            if (!_failureWarned)
            {
                _failureWarned = true;
                _log.Warn("system usage: measurement failed; skipping (logged once)", ex);
            }

            return;
        }

        if (sample is not null)
        {
            Sampled?.Invoke(this, sample);
        }
    }

    /// <summary>One reading of both APIs; null (round skipped) when either call fails.</summary>
    private SystemUsageSample? Measure()
    {
        if (!GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user))
        {
            WarnOnce(ref _cpuWarned, "GetSystemTimes");
            return null;
        }

        var memory = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref memory))
        {
            WarnOnce(ref _memoryWarned, "GlobalMemoryStatusEx");
            return null;
        }

        var cpu = new CpuTimes(idle.ToTicks(), kernel.ToTicks(), user.ToTicks());
        double? cpuPercent = _previousCpu is null ? null : CpuUsageCalculator.Percent(_previousCpu, cpu);
        _previousCpu = cpu;

        long total = ToLong(memory.ullTotalPhys);
        long used = ToLong(memory.ullTotalPhys - Math.Min(memory.ullAvailPhys, memory.ullTotalPhys));
        return new SystemUsageSample(cpuPercent, used, total, _time.GetUtcNow());
    }

    private void WarnOnce(ref bool warned, string api)
    {
        if (warned)
        {
            return;
        }

        warned = true;
        _log.Warn($"system usage: {api} failed (error {Marshal.GetLastPInvokeError()}); skipping this round (logged once)");
    }

    private static long ToLong(ulong value) => value > long.MaxValue ? long.MaxValue : (long)value;

    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME
    {
        public uint dwLowDateTime;
        public uint dwHighDateTime;

        public readonly ulong ToTicks() => ((ulong)dwHighDateTime << 32) | dwLowDateTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemTimes(out FILETIME idleTime, out FILETIME kernelTime, out FILETIME userTime);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);
}
