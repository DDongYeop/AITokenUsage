using System.ComponentModel;
using System.Diagnostics;
using AITokenUsage.Core.Abstractions;
using AITokenUsage.Core.Accounts;
using AITokenUsage.Core.Util;

namespace AITokenUsage;

/// <summary>
/// Opens a terminal window running a fixed CLI command for the connection guide (v1.1 spec §4.4): a Windows Terminal tab
/// when wt.exe exists, otherwise a cmd.exe window. Commands are code constants from ConnectionGuideBuilder, never user input.
/// Also starts the extra-account sign-in (<see cref="LaunchAccountLogin"/>), which never goes through a command line.
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
    /// Starts the sign-in of an extra account (multi-account spec §2.2, §3) off the UI thread. No shell command line is
    /// built: cmd.exe (from the system folder) is started directly with fixed arguments, and the account folder goes
    /// through the child's environment, so no quoting can drop or change it (Windows Terminal re-parses a wt.exe command
    /// line). The app has no console, so cmd gets its own window (Windows 11 may open it in Windows Terminal).
    /// False when it could not be started (already logged). Logs the account kind only, never the folder or environment.
    /// </summary>
    public static Task<bool> LaunchAccountLogin(string kind, AccountLoginCommand command, IAppLog log)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(log);

        return Task.Run(() =>
        {
            // Start in the user profile, like Launch: the CLIs would otherwise ask whether to trust System32.
            var startInfo = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                Arguments = command.Arguments,
                WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                UseShellExecute = false,
                CreateNoWindow = false,
            };

            foreach ((string name, string? value) in command.Environment)
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

            try
            {
                Process.Start(startInfo)?.Dispose();
                log.Info($"terminal opened for {kind} account login");
                return true;
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
            {
                log.Warn($"opening a terminal for {kind} account login failed ({ex.GetType().Name})");
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
