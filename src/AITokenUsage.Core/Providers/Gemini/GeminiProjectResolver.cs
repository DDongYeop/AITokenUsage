using AITokenUsage.Core.Abstractions;

namespace AITokenUsage.Core.Providers.Gemini;

/// <summary>
/// Project fallback when loadCodeAssist returns none:
/// process env → &lt;home&gt;\.gemini\.env → &lt;home&gt;\.env, each checking GOOGLE_CLOUD_PROJECT then GOOGLE_CLOUD_PROJECT_ID.
/// </summary>
public sealed class GeminiProjectResolver(IEnvironment env, IFileSystem files, GeminiPaths paths)
{
    private static readonly string[] Keys = ["GOOGLE_CLOUD_PROJECT", "GOOGLE_CLOUD_PROJECT_ID"];

    public string? Resolve()
    {
        foreach (var key in Keys)
        {
            var value = env.GetVariable(key);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        foreach (var path in new[] { paths.GeminiDotEnvPath, paths.HomeDotEnvPath })
        {
            string? content;
            try
            {
                content = files.ReadAllTextShared(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            if (content is null)
            {
                continue;
            }

            var values = GeminiDotEnv.Parse(content);
            foreach (var key in Keys)
            {
                if (values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
                {
                    return value.Trim();
                }
            }
        }

        return null;
    }
}
