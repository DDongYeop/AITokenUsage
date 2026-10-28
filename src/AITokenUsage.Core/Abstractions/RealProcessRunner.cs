using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace AITokenUsage.Core.Abstractions;

/// <summary>Runs CLI tools without a console window and with UTF-8 stdout/stderr.</summary>
public sealed class RealProcessRunner : IProcessRunner
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(2);
    private static readonly string[] DefaultPathExtensions = [".COM", ".EXE", ".BAT", ".CMD"];

    public async Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken ct)
    {
        using var process = new Process { StartInfo = CreateStartInfo(spec, redirectStandardInput: false) };
        process.Start();

        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        Task<string> stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(spec.Timeout ?? DefaultTimeout);

        bool timedOut = false;
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            KillTree(process);
            ct.ThrowIfCancellationRequested();
            timedOut = true;
        }

        string stdout = await DrainAsync(stdoutTask).ConfigureAwait(false);
        string stderr = await DrainAsync(stderrTask).ConfigureAwait(false);
        int exitCode = timedOut ? -1 : process.ExitCode;
        return new ProcessResult(exitCode, stdout, stderr, timedOut);
    }

    public IInteractiveProcess StartInteractive(ProcessSpec spec)
    {
        var process = new Process { StartInfo = CreateStartInfo(spec, redirectStandardInput: true) };
        try
        {
            process.Start();
        }
        catch
        {
            process.Dispose();
            throw;
        }

        return new RealInteractiveProcess(process, spec.Timeout);
    }

    public string? FindOnPath(string commandName)
    {
        if (string.IsNullOrWhiteSpace(commandName))
        {
            return null;
        }

        if (Path.IsPathFullyQualified(commandName))
        {
            return File.Exists(commandName) ? commandName : null;
        }

        string[] extensions = ReadPathExtensions();
        bool hasExtension = Path.HasExtension(commandName);
        string pathVariable = Environment.GetEnvironmentVariable("PATH") ?? "";

        foreach (string rawDirectory in pathVariable.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string directory = rawDirectory.Trim('"');
            if (directory.Length == 0)
            {
                continue;
            }

            if (hasExtension)
            {
                string exact = Path.Combine(directory, commandName);
                if (File.Exists(exact))
                {
                    return exact;
                }

                continue;
            }

            foreach (string extension in extensions)
            {
                string candidate = Path.Combine(directory, commandName + extension);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    internal static void KillTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // The process already exited.
        }
        catch (Win32Exception)
        {
            // Access denied or the process is exiting.
        }
    }

    private static ProcessStartInfo CreateStartInfo(ProcessSpec spec, bool redirectStandardInput)
    {
        var startInfo = new ProcessStartInfo(spec.FileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = redirectStandardInput,
            StandardOutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardErrorEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        };

        if (redirectStandardInput)
        {
            // Without this the child would receive a UTF-8 BOM in front of the first line.
            startInfo.StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        }

        foreach (string argument in spec.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (spec.EnvironmentOverrides is not null)
        {
            foreach ((string name, string? value) in spec.EnvironmentOverrides)
            {
                if (value is null)
                {
                    startInfo.Environment.Remove(name);
                }
                else
                {
                    startInfo.Environment[name] = value;
                }
            }
        }

        return startInfo;
    }

    private static string[] ReadPathExtensions()
    {
        string? pathExt = Environment.GetEnvironmentVariable("PATHEXT");
        if (string.IsNullOrWhiteSpace(pathExt))
        {
            return DefaultPathExtensions;
        }

        return pathExt.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static async Task<string> DrainAsync(Task<string> readTask)
    {
        try
        {
            return await readTask.WaitAsync(DrainTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return "";
        }
        catch (IOException)
        {
            return "";
        }
        catch (ObjectDisposedException)
        {
            return "";
        }
    }
}
