namespace AITokenUsage.Core.Providers.Gemini;

/// <summary>Minimal KEY=VALUE parser for the .env files gemini-cli loads.</summary>
public static class GeminiDotEnv
{
    public static IReadOnlyDictionary<string, string> Parse(string content)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            if (line.StartsWith("export ", StringComparison.Ordinal))
            {
                line = line["export ".Length..].TrimStart();
            }

            var equals = line.IndexOf('=');
            if (equals <= 0)
            {
                continue;
            }

            var key = line[..equals].Trim();
            var value = line[(equals + 1)..].Trim();
            if (value.Length > 0 && (value[0] == '"' || value[0] == '\''))
            {
                var close = value.IndexOf(value[0], 1);
                value = close > 0 ? value[1..close] : value[1..];
            }
            else
            {
                var comment = value.IndexOf(" #", StringComparison.Ordinal);
                if (comment >= 0)
                {
                    value = value[..comment].TrimEnd();
                }
            }

            result[key] = value;
        }

        return result;
    }
}
