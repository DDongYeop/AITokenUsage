using System.ComponentModel;
using System.Diagnostics;
using AITokenUsage.Core.Abstractions;
using AITokenUsage.Core.Util;

namespace AITokenUsage;

/// <summary>
/// Opens a terminal window running a fixed CLI command for the connection guide (v1.1 spec §4.4): a Windows Terminal tab
/// when wt.exe exists, otherwise a cmd.exe window. Commands are code constants from ConnectionGuideBuilder, never user input.
/// </summary>
public static class TerminalLauncher
{
    private const string WindowsTerminalFileName = "wt.exe";

    /// <summary>Starts the terminal off the UI thread. False when it could not be started (already logged).</summary>
    public static Task<bool> Launch(string command, IAppLog log)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        ArgumentNullException.ThrowIfNull(log);

        return Task.Run(() =>
        {
            string? windowsTerminal = FindWindowsTerminal();
            (string fileName, IReadOnlyList<string> arguments) = TerminalCommandLine.Build(windowsTerminal is not null, command);

            // ShellExecute has no argument list, so the arguments go as one command-line string. Start in the user profile:
            // the app's own current directory is often C:\Windows\System32 (started from the Run key), and the CLIs would
            // ask whether to trust that folder. (Windows Terminal uses its profile's starting directory anyway.)
            var startInfo = new ProcessStartInfo
            {
                FileName = windowsTerminal ?? fileName,
                Arguments = TerminalCommandLine.JoinArguments(arguments),
                WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                UseShellExecute = true,
            };

            try
            {
                Process.Start(startInfo)?.Dispose();
                log.Info($"terminal opened for '{command}' ({Path.GetFileName(startInfo.FileName)})");
                return true;
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
            {
                log.Warn($"opening a terminal for '{command}' failed", ex);
                return false;
            }
        });
    }

    /// <summary>
    /// The full path of wt.exe: the per-user app execution alias (%LOCALAPPDATA%\Microsoft\WindowsApps) first, then the
    /// PATH directories. Null when Windows Terminal is not installed.
    /// </summary>
    private static string? FindWindowsTerminal()
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        IEnumerable<string> directories = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(directory => directory.Trim('"'));
        if (localAppData.Length > 0)
        {
            directories = directories.Prepend(Path.Combine(localAppData, "Microsoft", "WindowsApps"));
        }

        foreach (string directory in directories)
        {
            try
            {
                string candidate = Path.Combine(Environment.ExpandEnvironmentVariables(directory), WindowsTerminalFileName);

                // The WindowsApps entry is an app execution alias (a reparse point); File.Exists reports it as a file.
                if (Path.IsPathFullyQualified(candidate) && File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (ArgumentException)
            {
                // A malformed PATH entry: skip it.
            }
        }

        return null;
    }
}
