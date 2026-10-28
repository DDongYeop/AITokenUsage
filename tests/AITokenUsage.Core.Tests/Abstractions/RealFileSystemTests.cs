using System.Text;
using AITokenUsage.Core.Abstractions;
using AITokenUsage.Core.Tests.Fakes;

namespace AITokenUsage.Core.Tests.Abstractions;

public sealed class RealFileSystemTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly RealFileSystem _fs = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void ReadAllTextShared_ReadsWhileAnotherHandleHoldsTheFileOpenForWriting()
    {
        string path = _temp.Combine("auth.json");
        File.WriteAllText(path, """{"tokens":{"access_token":"test-access-token"}}""");

        // Same share mode Rust std (Codex) and Node use for their writers.
        using var writer = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);

        // The default .NET reader (FileShare.Read) is refused while a writer is open...
        Assert.Throws<IOException>(() => File.ReadAllText(path));

        // ...but the shared reader is not.
        string? text = _fs.ReadAllTextShared(path);

        Assert.Equal("""{"tokens":{"access_token":"test-access-token"}}""", text);
    }

    [Fact]
    public void ReadAllTextShared_DoesNotBlockWriterFromReplacingTheFile()
    {
        string path = _temp.Combine("creds.json");
        File.WriteAllText(path, "old");

        Assert.Equal("old", _fs.ReadAllTextShared(path));

        // The handle is closed right away, so an in-place rewrite succeeds.
        using (var writer = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            writer.Write("new"u8);
        }

        Assert.Equal("new", _fs.ReadAllTextShared(path));
    }

    [Fact]
    public void ReadAllTextShared_ReturnsNull_WhenFileOrDirectoryIsMissing()
    {
        Assert.Null(_fs.ReadAllTextShared(_temp.Combine("missing.json")));
        Assert.Null(_fs.ReadAllTextShared(_temp.Combine("no-dir", "missing.json")));
    }

    [Fact]
    public void ReadAllTextShared_StripsUtf8Bom()
    {
        string path = _temp.Combine("bom.json");
        File.WriteAllText(path, "{\"a\":1}", new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        Assert.Equal("{\"a\":1}", _fs.ReadAllTextShared(path));
    }

    [Fact]
    public void ExistsMethods_ReflectDisk()
    {
        string file = _temp.Combine("a.txt");
        File.WriteAllText(file, "x");

        Assert.True(_fs.FileExists(file));
        Assert.False(_fs.FileExists(_temp.Combine("b.txt")));
        Assert.True(_fs.DirectoryExists(_temp.Path));
        Assert.False(_fs.DirectoryExists(_temp.Combine("nope")));
    }

    [Fact]
    public void EnumerateFiles_HonoursPatternAndRecursion_AndToleratesMissingDirectory()
    {
        string sessions = _temp.Combine("sessions");
        Directory.CreateDirectory(Path.Combine(sessions, "2026", "10", "28"));
        File.WriteAllText(Path.Combine(sessions, "rollout-top.jsonl"), "");
        File.WriteAllText(Path.Combine(sessions, "2026", "10", "28", "rollout-deep.jsonl"), "");
        File.WriteAllText(Path.Combine(sessions, "2026", "10", "28", "other.txt"), "");

        var flat = _fs.EnumerateFiles(sessions, "rollout-*.jsonl", recursive: false).Select(f => Path.GetFileName(f)).ToList();
        var deep = _fs.EnumerateFiles(sessions, "rollout-*.jsonl", recursive: true).Select(f => Path.GetFileName(f)).Order().ToList();

        Assert.Equal(["rollout-top.jsonl"], flat);
        Assert.Equal(["rollout-deep.jsonl", "rollout-top.jsonl"], deep);
        Assert.Empty(_fs.EnumerateFiles(_temp.Combine("missing"), "*", recursive: true));
    }

    [Fact]
    public void GetLastWriteTimeUtc_ReturnsDiskValue()
    {
        string path = _temp.Combine("t.txt");
        File.WriteAllText(path, "x");
        var stamp = new DateTime(2026, 10, 1, 8, 30, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, stamp);

        Assert.Equal(stamp, _fs.GetLastWriteTimeUtc(path));
    }

    [Theory]
    [InlineData("l1\nl2\nl3\n", new[] { "l3", "l2", "l1" })]
    [InlineData("l1\nl2\nl3", new[] { "l3", "l2", "l1" })]
    [InlineData("l1\r\nl2\r\n", new[] { "l2", "l1" })]
    [InlineData("a\n\nb\n", new[] { "b", "", "a" })]
    [InlineData("only", new[] { "only" })]
    [InlineData("\n", new[] { "" })]
    [InlineData("", new string[0])]
    public void ReadLinesReverse_MatchesReadAllLinesReversed(string content, string[] expected)
    {
        string path = _temp.Combine("small.jsonl");
        File.WriteAllText(path, content, new UTF8Encoding(false));

        Assert.Equal(expected, _fs.ReadLinesReverse(path).ToArray());
        Assert.Equal(File.ReadAllLines(path).Reverse().ToArray(), expected);
    }

    [Fact]
    public void ReadLinesReverse_HandlesLinesAcrossChunkBoundaries_WithMultiByteText()
    {
        string path = _temp.Combine("big.jsonl");
        var lines = Enumerable.Range(0, 6000)
            .Select(i => $"{{\"n\":{i},\"text\":\"사용량 한도 {i} — {new string('가', i % 50)}\"}}")
            .ToArray();
        File.WriteAllLines(path, lines, new UTF8Encoding(false));
        Assert.True(new FileInfo(path).Length > 3 * 64 * 1024);

        string[] reversed = _fs.ReadLinesReverse(path).ToArray();

        Assert.Equal(lines.Reverse().ToArray(), reversed);
    }

    [Fact]
    public void ReadLinesReverse_SkipsUtf8Bom()
    {
        string path = _temp.Combine("bom.jsonl");
        File.WriteAllText(path, "first\nsecond\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        Assert.Equal(["second", "first"], _fs.ReadLinesReverse(path).ToArray());
    }

    [Fact]
    public void ReadLinesReverse_YieldsNothing_WhenFileIsMissing()
    {
        Assert.Empty(_fs.ReadLinesReverse(_temp.Combine("missing.jsonl")));
    }

    [Fact]
    public void ReadLinesReverse_LetsWriterAppendAndDeleteWhileEnumerating()
    {
        string path = _temp.Combine("live.jsonl");
        File.WriteAllText(path, "a\nb\n");

        using IEnumerator<string> lines = _fs.ReadLinesReverse(path).GetEnumerator();
        Assert.True(lines.MoveNext());
        Assert.Equal("b", lines.Current);

        // The CLI keeps appending while we scan; our handle must not get in the way.
        using (var appender = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
        {
            appender.Write("c\n"u8);
        }

        Assert.True(lines.MoveNext());
        Assert.Equal("a", lines.Current);

        File.Delete(path);
        Assert.False(lines.MoveNext());
    }
}
