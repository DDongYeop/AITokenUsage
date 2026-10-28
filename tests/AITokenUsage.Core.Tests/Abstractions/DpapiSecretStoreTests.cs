using System.Text;
using AITokenUsage.Core.Abstractions;
using AITokenUsage.Core.Tests.Fakes;

namespace AITokenUsage.Core.Tests.Abstractions;

public sealed class DpapiSecretStoreTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void SaveThenLoad_RoundTrips_AndFileIsNotPlainText()
    {
        string directory = _temp.Combine("AITokenUsage");
        var store = new DpapiSecretStore(directory);

        store.Save("copilot-token", "ghu_test-token-한글");

        string file = Path.Combine(directory, "copilot-token.bin");
        Assert.True(File.Exists(file));
        byte[] raw = File.ReadAllBytes(file);
        Assert.DoesNotContain("ghu_test", Encoding.UTF8.GetString(raw));
        Assert.DoesNotContain("ghu_test", Encoding.Unicode.GetString(raw));
        Assert.Equal("ghu_test-token-한글", store.Load("copilot-token"));
        Assert.Equal("ghu_test-token-한글", new DpapiSecretStore(directory).Load("copilot-token"));
    }

    [Fact]
    public void Save_Overwrites_AndLeavesNoTempFile()
    {
        var store = new DpapiSecretStore(_temp.Path);

        store.Save("copilot-token", "first");
        store.Save("copilot-token", "second");

        Assert.Equal("second", store.Load("copilot-token"));
        Assert.Equal(["copilot-token.bin"], Directory.GetFiles(_temp.Path).Select(f => Path.GetFileName(f)).ToArray());
    }

    [Fact]
    public void Load_ReturnsNull_WhenMissing()
    {
        Assert.Null(new DpapiSecretStore(_temp.Combine("does-not-exist")).Load("copilot-token"));
    }

    [Fact]
    public void Load_Succeeds_WhileAnotherHandleHoldsTheFileOpenForWriting()
    {
        var store = new DpapiSecretStore(_temp.Path);
        store.Save("copilot-token", "shared-secret");

        using var holder = new FileStream(_temp.Combine("copilot-token.bin"), FileMode.Open, FileAccess.ReadWrite,
            FileShare.ReadWrite | FileShare.Delete);

        Assert.Equal("shared-secret", store.Load("copilot-token"));
        Assert.Null(store.Load("never-saved"));
    }

    [Fact]
    public void Load_ReturnsNull_WhenFileIsCorrupt()
    {
        File.WriteAllBytes(_temp.Combine("copilot-token.bin"), [1, 2, 3, 4, 5]);

        Assert.Null(new DpapiSecretStore(_temp.Path).Load("copilot-token"));
    }

    [Fact]
    public void Delete_RemovesTheFile_AndIsIdempotent()
    {
        var store = new DpapiSecretStore(_temp.Path);
        store.Save("copilot-token", "secret");

        store.Delete("copilot-token");
        store.Delete("copilot-token");

        Assert.Null(store.Load("copilot-token"));
        Assert.False(File.Exists(_temp.Combine("copilot-token.bin")));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("..")]
    [InlineData(@"..\escape")]
    [InlineData("a/b")]
    public void InvalidNames_Throw(string name)
    {
        var store = new DpapiSecretStore(_temp.Path);

        Assert.ThrowsAny<ArgumentException>(() => store.Save(name, "x"));
    }
}
