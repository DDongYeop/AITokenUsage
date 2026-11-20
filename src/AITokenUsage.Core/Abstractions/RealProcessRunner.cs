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

        if (!OperatingSystem.IsWindows())
        {
            return FindOnUnixPath(commandName, Environment.GetEnvironmentVariable("PATH") ?? "",
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        }

        return FindOnWindowsPath(commandName, Environment.GetEnvironmentVariable("PATH") ?? "",
            Environment.GetEnvironmentVariable("LOCALAPPDATA"));
    }

    internal static string? FindOnWindowsPath(string commandName, string pathVariable, string? localAppData)
    {
        string[] extensions = ReadPathExtensions();
        bool hasExtension = Path.HasExtension(commandName);

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

        if ((commandName.Equals("agy", StringComparison.OrdinalIgnoreCase) ||
             commandName.Equals("agy.exe", StringComparison.OrdinalIgnoreCase)) &&
            localAppData is not null && Path.IsPathFullyQualified(localAppData))
        {
            string installed = Path.Combine(localAppData, "agy", "bin", "agy.exe");
            if (File.Exists(installed)) return installed;
        }

        return null;
    }

    /// <summary>
    /// macOS/Linux lookup: PATH (':'-separated, no extensions, executable files only), then the folders where Homebrew,
    /// npm, bun and the official installers put CLIs. A menu bar app started from Finder gets only
    /// /usr/bin:/bin:/usr/sbin:/sbin, so the extra folders are what finds claude, codex, gemini and gh there.
    /// </summary>
    public static string? FindOnUnixPath(string commandName, string pathVariable, string home)
    {
        foreach (string directory in UnixSearchDirectories(pathVariable, home))
        {
            string candidate = Path.Combine(directory, commandName);
            if (IsExecutableFile(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    public static IEnumerable<string> UnixSearchDirectories(string pathVariable, string home)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string directory in pathVariable.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (seen.Add(directory)) yield return directory;
        }

        string[] wellKnown =
        [
            "/opt/homebrew/bin",
            "/usr/local/bin",
            Path.Combine(home, ".local", "bin"),
            Path.Combine(home, ".claude", "local"),
            Path.Combine(home, ".npm-global", "bin"),
            Path.Combine(home, ".bun", "bin"),
            Path.Combine(home, ".volta", "bin"),
            "/usr/bin",
            "/bin",
        ];
        foreach (string directory in wellKnown)
        {
            if (seen.Add(directory)) yield return directory;
        }

        // nvm: every installed Node version's bin folder, newest name last (lookup order does not matter much here).
        string nvmVersions = Path.Combine(home, ".nvm", "versions", "node");
        if (Directory.Exists(nvmVersions))
        {
            string[] versions;
            try
            {
                versions = Directory.GetDirectories(nvmVersions);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                versions = [];
            }

            foreach (string version in versions.OrderByDescending(v => v, StringComparer.Ordinal))
            {
                string bin = Path.Combine(version, "bin");
                if (seen.Add(bin)) yield return bin;
            }
        }
    }

    private static bool IsExecutableFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            if (OperatingSystem.IsWindows()) return true;
            UnixFileMode mode = File.GetUnixFileMode(path);
            return (mode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
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

        if (spec.WorkingDirectory is not null)
        {
            startInfo.WorkingDirectory = spec.WorkingDirectory;
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
