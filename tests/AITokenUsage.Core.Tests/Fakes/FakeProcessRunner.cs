using System.ComponentModel;
using AITokenUsage.Core.Abstractions;

namespace AITokenUsage.Core.Tests.Fakes;

/// <summary>
/// Scripted process runner. Without a handler, RunAsync/StartInteractive throw
/// Win32Exception(2) exactly like Process.Start does for a missing executable.
/// </summary>
public sealed class FakeProcessRunner : IProcessRunner
{
    private readonly object _gate = new();
    private readonly List<ProcessSpec> _calls = [];
    private readonly Dictionary<string, string?> _paths = new(StringComparer.OrdinalIgnoreCase);
    private Func<ProcessSpec, ProcessResult>? _onRun;
    private Func<ProcessSpec, FakeInteractiveProcess>? _onInteractive;

    /// <summary>Every RunAsync and StartInteractive call, in order.</summary>
    public IReadOnlyList<ProcessSpec> Calls
    {
        get
        {
            lock (_gate)
            {
                return _calls.ToArray();
            }
        }
    }

    public FakeProcessRunner OnRun(Func<ProcessSpec, ProcessResult> handler)
    {
        _onRun = handler;
        return this;
    }

    public FakeProcessRunner OnInteractive(Func<ProcessSpec, FakeInteractiveProcess> handler)
    {
        _onInteractive = handler;
        return this;
    }

    /// <summary>Makes FindOnPath(command) return <paramref name="fullPath"/> (null = not found).</summary>
    public FakeProcessRunner SetPath(string command, string? fullPath)
    {
        _paths[command] = fullPath;
        return this;
    }

    public Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Record(spec);
        if (_onRun is null)
        {
            throw new Win32Exception(2, $"FakeProcessRunner: no OnRun handler for '{spec.FileName}'.");
        }

        return Task.FromResult(_onRun(spec));
    }

    public IInteractiveProcess StartInteractive(ProcessSpec spec)
    {
        Record(spec);
        if (_onInteractive is null)
        {
            throw new Win32Exception(2, $"FakeProcessRunner: no OnInteractive handler for '{spec.FileName}'.");
        }

        return _onInteractive(spec);
    }

    public string? FindOnPath(string commandName) =>
        _paths.TryGetValue(commandName, out string? fullPath) ? fullPath : null;

    private void Record(ProcessSpec spec)
    {
        lock (_gate)
        {
            _calls.Add(spec);
        }
    }
}
