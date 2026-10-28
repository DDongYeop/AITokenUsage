using System.IO.Enumeration;
using AITokenUsage.Core.Abstractions;

namespace AITokenUsage.Core.Tests.Fakes;

/// <summary>In-memory file system. Paths compare OrdinalIgnoreCase; '/' and '\' are equivalent.</summary>
public sealed class FakeFileSystem : IFileSystem
{
    public static readonly DateTime DefaultLastWriteUtc = new(2026, 10, 28, 0, 0, 0, DateTimeKind.Utc);

    private static readonly DateTime MissingFileTime = DateTime.FromFileTimeUtc(0);

    private readonly object _gate = new();
    private readonly Dictionary<string, (string Content, DateTime LastWriteUtc)> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _directories = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Queue<string?>> _queuedReads = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Exception> _readErrors = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _readCounts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Adds or replaces a file and registers all of its parent directories.</summary>
    public FakeFileSystem AddFile(string path, string content, DateTime? lastWriteUtc = null)
    {
        string key = Normalize(path);
        lock (_gate)
        {
            _files[key] = (content, lastWriteUtc ?? DefaultLastWriteUtc);
            AddAncestors(key);
        }

        return this;
    }

    public FakeFileSystem AddDirectory(string path)
    {
        string key = Normalize(path);
        lock (_gate)
        {
            _directories.Add(key);
            AddAncestors(key);
        }

        return this;
    }

    /// <summary>Removes a file, or a directory together with everything below it.</summary>
    public FakeFileSystem Remove(string path)
    {
        string key = Normalize(path);
        string prefix = key + "\\";
        lock (_gate)
        {
            _files.Remove(key);
            _directories.Remove(key);
            foreach (string file in _files.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList())
            {
                _files.Remove(file);
            }

            _directories.RemoveWhere(d => d.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        }

        return this;
    }

    /// <summary>
    /// Queues results returned by the next ReadAllTextShared calls for this path, before the stored
    /// content is used again. A null entry simulates "file missing" for that one read.
    /// Use it to simulate a half-written file followed by the complete one.
    /// </summary>
    public FakeFileSystem EnqueueReads(string path, params string?[] contents)
    {
        string key = Normalize(path);
        lock (_gate)
        {
            if (!_queuedReads.TryGetValue(key, out Queue<string?>? queue))
            {
                queue = new Queue<string?>();
                _queuedReads[key] = queue;
            }

            foreach (string? content in contents)
            {
                queue.Enqueue(content);
            }
        }

        return this;
    }

    /// <summary>Every ReadAllTextShared call for this path throws <paramref name="exception"/>.</summary>
    public FakeFileSystem ThrowOnRead(string path, Exception exception)
    {
        lock (_gate)
        {
            _readErrors[Normalize(path)] = exception;
        }

        return this;
    }

    public int ReadCount(string path)
    {
        lock (_gate)
        {
            return _readCounts.TryGetValue(Normalize(path), out int count) ? count : 0;
        }
    }

    public bool FileExists(string path)
    {
        lock (_gate)
        {
            return _files.ContainsKey(Normalize(path));
        }
    }

    public bool DirectoryExists(string path)
    {
        lock (_gate)
        {
            return _directories.Contains(Normalize(path));
        }
    }

    public string? ReadAllTextShared(string path)
    {
        string key = Normalize(path);
        lock (_gate)
        {
            _readCounts[key] = (_readCounts.TryGetValue(key, out int count) ? count : 0) + 1;

            if (_readErrors.TryGetValue(key, out Exception? error))
            {
                throw error;
            }

            if (_queuedReads.TryGetValue(key, out Queue<string?>? queue) && queue.Count > 0)
            {
                return queue.Dequeue();
            }

            return _files.TryGetValue(key, out var file) ? file.Content : null;
        }
    }

    public IEnumerable<string> EnumerateFiles(string directory, string searchPattern, bool recursive)
    {
        string key = Normalize(directory);
        string prefix = key + "\\";
        lock (_gate)
        {
            if (!_directories.Contains(key))
            {
                return [];
            }

            return _files.Keys
                .Where(p => p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .Where(p => recursive || p.IndexOf('\\', prefix.Length) < 0)
                .Where(p => FileSystemName.MatchesSimpleExpression(searchPattern, Path.GetFileName(p), ignoreCase: true))
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    public DateTime GetLastWriteTimeUtc(string path)
    {
        lock (_gate)
        {
            return _files.TryGetValue(Normalize(path), out var file) ? file.LastWriteUtc : MissingFileTime;
        }
    }

    public IEnumerable<string> ReadLinesReverse(string path)
    {
        string? content;
        lock (_gate)
        {
            content = _files.TryGetValue(Normalize(path), out var file) ? file.Content : null;
        }

        if (content is null)
        {
            return [];
        }

        var lines = new List<string>();
        using var reader = new StringReader(content);
        while (reader.ReadLine() is { } line)
        {
            lines.Add(line);
        }

        lines.Reverse();
        return lines;
    }

    private void AddAncestors(string key)
    {
        string? parent = Path.GetDirectoryName(key);
        while (!string.IsNullOrEmpty(parent))
        {
            _directories.Add(Normalize(parent));
            parent = Path.GetDirectoryName(parent);
        }
    }

    private static string Normalize(string path) => path.Replace('/', '\\').TrimEnd('\\');
}
