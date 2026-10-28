using System.Text;

namespace AITokenUsage.Core.Abstractions;

/// <summary>
/// File access that never blocks CLI writers: every open uses FileShare.ReadWrite | FileShare.Delete
/// (Codex rewrites auth.json in place; a FileShare.Read reader would make its save fail).
/// </summary>
public sealed class RealFileSystem : IFileSystem
{
    private const int ReverseChunkSize = 64 * 1024;
    private const FileShare SharedMode = FileShare.ReadWrite | FileShare.Delete;

    public bool FileExists(string path) => File.Exists(path);

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public string? ReadAllTextShared(string path)
    {
        using FileStream? stream = OpenShared(path);
        if (stream is null)
        {
            return null;
        }

        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    public IEnumerable<string> EnumerateFiles(string directory, string searchPattern, bool recursive)
    {
        if (!Directory.Exists(directory))
        {
            return [];
        }

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = recursive,
            IgnoreInaccessible = true,
            AttributesToSkip = 0,
            MatchType = MatchType.Simple,
        };
        return Directory.EnumerateFiles(directory, searchPattern, options);
    }

    public DateTime GetLastWriteTimeUtc(string path) => File.GetLastWriteTimeUtc(path);

    public IEnumerable<string> ReadLinesReverse(string path)
    {
        FileStream? stream = OpenShared(path);
        if (stream is null)
        {
            yield break;
        }

        using (stream)
        {
            long position = stream.Length;
            byte[] carry = [];
            bool isFirstSegment = true;

            while (position > 0)
            {
                int toRead = (int)Math.Min(ReverseChunkSize, position);
                position -= toRead;

                // chunk = [bytes read from disk][carry: start of a line whose newline was not found yet]
                byte[] chunk = new byte[toRead + carry.Length];
                stream.Position = position;
                stream.ReadExactly(chunk, 0, toRead);
                Buffer.BlockCopy(carry, 0, chunk, toRead, carry.Length);

                int lineEnd = chunk.Length;
                for (int i = toRead - 1; i >= 0; i--)
                {
                    if (chunk[i] != (byte)'\n')
                    {
                        continue;
                    }

                    string line = DecodeLine(chunk, i + 1, lineEnd - (i + 1));
                    if (!(isFirstSegment && line.Length == 0))
                    {
                        yield return line;
                    }

                    isFirstSegment = false;
                    lineEnd = i;
                }

                carry = chunk[..lineEnd];
            }

            int offset = HasUtf8Bom(carry) ? 3 : 0;
            if (carry.Length - offset > 0 || !isFirstSegment)
            {
                yield return DecodeLine(carry, offset, carry.Length - offset);
            }
        }
    }

    private static FileStream? OpenShared(string path)
    {
        try
        {
            return new FileStream(path, FileMode.Open, FileAccess.Read, SharedMode);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }

    private static string DecodeLine(byte[] buffer, int start, int count)
    {
        if (count > 0 && buffer[start + count - 1] == (byte)'\r')
        {
            count--;
        }

        return Encoding.UTF8.GetString(buffer, start, count);
    }

    private static bool HasUtf8Bom(byte[] bytes) =>
        bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
}
