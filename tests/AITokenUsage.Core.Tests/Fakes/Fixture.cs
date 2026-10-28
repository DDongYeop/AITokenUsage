namespace AITokenUsage.Core.Tests.Fakes;

/// <summary>Reads sample files copied to the test output folder (Fixtures/&lt;provider&gt;/...).</summary>
public static class Fixture
{
    /// <param name="relativePath">Path below Fixtures, e.g. "claude/usage-limits.json".</param>
    public static string Read(string relativePath) => File.ReadAllText(GetPath(relativePath));

    public static string GetPath(string relativePath) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", relativePath.Replace('/', Path.DirectorySeparatorChar));
}
