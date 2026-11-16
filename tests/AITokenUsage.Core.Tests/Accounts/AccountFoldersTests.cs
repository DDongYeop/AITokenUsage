using AITokenUsage.Core.Accounts;
using AITokenUsage.Core.Tests.Fakes;

namespace AITokenUsage.Core.Tests.Accounts;

public class AccountFoldersTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    private string Source => _temp.Combine("login", "claude-abc123");

    private string Target => _temp.Combine("accounts", "claude-abc123");

    private string Aside => _temp.Combine("login", "claude-abc123.old");

    public void Dispose() => _temp.Dispose();

    private static void WriteFile(string directory, string name, string content)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, name), content);
    }

    private static string ReadFile(string directory, string name) => File.ReadAllText(Path.Combine(directory, name));

    [Fact]
    public void MoveIntoPlace_NewAccount_MovesTheFolderAndCreatesTheParent()
    {
        WriteFile(Source, ".credentials.json", "new");
        WriteFile(Path.Combine(Source, "backups"), "b.json", "backup");

        bool cleanedUp = AccountFolders.MoveIntoPlace(Source, Target, Aside);

        Assert.True(cleanedUp);
        Assert.False(Directory.Exists(Source));
        Assert.Equal("new", ReadFile(Target, ".credentials.json"));
        Assert.Equal("backup", ReadFile(Path.Combine(Target, "backups"), "b.json"));
        Assert.False(Directory.Exists(Aside));
    }

    [Fact]
    public void MoveIntoPlace_ExistingAccount_ReplacesTheFolderAndDeletesTheOldOne()
    {
        WriteFile(Target, ".credentials.json", "old");
        WriteFile(Target, "only-in-old.txt", "x");
        WriteFile(Source, ".credentials.json", "new");

        bool cleanedUp = AccountFolders.MoveIntoPlace(Source, Target, Aside);

        Assert.True(cleanedUp);
        Assert.Equal("new", ReadFile(Target, ".credentials.json"));
        Assert.False(File.Exists(Path.Combine(Target, "only-in-old.txt")));
        Assert.False(Directory.Exists(Source));
        Assert.False(Directory.Exists(Aside));
    }

    [Fact]
    public void MoveIntoPlace_RestoresTheOldFolderWhenTheNewOneCannotBeMoved()
    {
        WriteFile(Target, ".credentials.json", "old");

        // The sign-in folder is missing: the second move fails after the old folder was moved aside.
        Assert.Throws<DirectoryNotFoundException>(() => AccountFolders.MoveIntoPlace(Source, Target, Aside));

        Assert.Equal("old", ReadFile(Target, ".credentials.json"));
        Assert.False(Directory.Exists(Aside));
    }

    [Fact]
    public void MoveIntoPlace_LeavesEverythingWhenTheOldFolderIsInUse()
    {
        WriteFile(Target, ".credentials.json", "old");
        WriteFile(Source, ".credentials.json", "new");

        // An open file inside a folder keeps Windows from renaming that folder (like a running renewal command).
        using (new FileStream(Path.Combine(Target, ".credentials.json"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            Assert.ThrowsAny<IOException>(() => AccountFolders.MoveIntoPlace(Source, Target, Aside));
        }

        Assert.Equal("old", ReadFile(Target, ".credentials.json"));
        Assert.Equal("new", ReadFile(Source, ".credentials.json"));
        Assert.False(Directory.Exists(Aside));
    }

    [Fact]
    public void MoveIntoPlace_RemovesALeftoverAsideFolderFirst()
    {
        WriteFile(Aside, "stale.txt", "stale");
        WriteFile(Target, ".credentials.json", "old");
        WriteFile(Source, ".credentials.json", "new");

        Assert.True(AccountFolders.MoveIntoPlace(Source, Target, Aside));

        Assert.Equal("new", ReadFile(Target, ".credentials.json"));
        Assert.False(Directory.Exists(Aside));
    }

    [Fact]
    public void Delete_RemovesTheFolderAndIgnoresAMissingOne()
    {
        WriteFile(Path.Combine(Target, "backups"), "b.json", "backup");

        AccountFolders.Delete(Target);
        AccountFolders.Delete(Target);

        Assert.False(Directory.Exists(Target));
    }
}
