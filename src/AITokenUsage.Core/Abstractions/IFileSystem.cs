namespace AITokenUsage.Core.Abstractions;

public interface IFileSystem
{
    bool FileExists(string path);

    bool DirectoryExists(string path);

    /// <summary>
    /// Reads the whole file with FileShare.ReadWrite|Delete and closes it immediately.
    /// Returns null when the file or its directory does not exist. Other IO failures throw IOException.
    /// </summary>
    string? ReadAllTextShared(string path);

    /// <summary>Lists files matching <paramref name="searchPattern"/>. Empty when the directory does not exist.</summary>
    IEnumerable<string> EnumerateFiles(string directory, string searchPattern, bool recursive);

    DateTime GetLastWriteTimeUtc(string path);

    /// <summary>
    /// Opens the file in shared mode and yields its lines from the last one to the first (JSONL tail scan).
    /// A trailing newline does not produce an empty first line. Yields nothing when the file does not exist.
    /// </summary>
    IEnumerable<string> ReadLinesReverse(string path);
}
