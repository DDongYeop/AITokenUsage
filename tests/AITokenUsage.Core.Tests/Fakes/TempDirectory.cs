namespace AITokenUsage.Core.Tests.Fakes;

/// <summary>A unique real directory under %TEMP%\AITokenUsage.Tests, deleted on Dispose.</summary>
public sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AITokenUsage.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // A test left a handle open; the folder lives in %TEMP% and is harmless.
        }
        catch (UnauthorizedAccessException)
        {
            // Same as above.
        }
    }
}
