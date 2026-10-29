using System.Text;

namespace AITokenUsage.Core.Util;

/// <summary>Builds the process command line that opens a terminal window running a command (spec §4.4).</summary>
public static class TerminalCommandLine
{
    /// <summary>
    /// <c>wt.exe new-tab cmd /k &lt;command&gt;</c> when Windows Terminal exists, otherwise <c>cmd.exe /k &lt;command&gt;</c>.
    /// Arguments are returned as separate items so the caller can use an argument list (no shell parsing).
    /// </summary>
    public static (string FileName, IReadOnlyList<string> Arguments) Build(bool hasWindowsTerminal, string command)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);

        return hasWindowsTerminal
            ? ("wt.exe", new[] { "new-tab", "cmd", "/k", command })
            : ("cmd.exe", new[] { "/k", command });
    }

    /// <summary>
    /// The <see cref="Build"/> arguments as one command-line string, for ShellExecute (which has no argument list).
    /// The last item is the command that <c>cmd /k</c> runs: cmd takes everything after <c>/k</c> as a raw command line
    /// and Windows Terminal forwards everything after <c>new-tab</c> as the new tab's command line, so the command is
    /// appended verbatim (<c>/k codex login</c>, not <c>/k "codex login"</c>). The items before it are quoted with
    /// <see cref="QuoteArgument"/>. Commands are fixed constants (spec §4.4), never user input.
    /// </summary>
    public static string JoinArguments(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Count == 0)
        {
            return "";
        }

        var builder = new StringBuilder();
        for (int i = 0; i < arguments.Count - 1; i++)
        {
            builder.Append(QuoteArgument(arguments[i])).Append(' ');
        }

        return builder.Append(arguments[^1]).ToString();
    }

    /// <summary>
    /// Wraps an argument in double quotes when it is empty or contains whitespace, following the CommandLineToArgvW rules:
    /// backslashes before a quote are doubled and embedded quotes are escaped. Other arguments are returned unchanged.
    /// </summary>
    public static string QuoteArgument(string argument)
    {
        ArgumentNullException.ThrowIfNull(argument);
        if (argument.Length > 0 && !argument.Any(c => char.IsWhiteSpace(c) || c == '"'))
        {
            return argument;
        }

        var builder = new StringBuilder("\"");
        int backslashes = 0;
        foreach (char c in argument)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }

            // Backslashes are literal unless they precede a quote: then each is doubled and the quote escaped.
            builder.Append('\\', c == '"' ? (backslashes * 2) + 1 : backslashes);
            backslashes = 0;
            builder.Append(c);
        }

        // Backslashes before the closing quote are doubled so they do not escape it.
        return builder.Append('\\', backslashes * 2).Append('"').ToString();
    }
}
