using AITokenUsage.Core.Accounts;
using AITokenUsage.Core.Tests.Fakes;

namespace AITokenUsage.Core.Tests.Accounts;

public class AccountFoldersTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly List<(string From, string To)> _moves = [];
    private readonly List<IDisposable> _handles = [];

    private string Source => _temp.Combine("login", "claude-abc123");

    private string Ready => _temp.Combine("login", "claude-abc123.ready");

    private string Target => _temp.Combine("accounts", "claude-abc123");

    private string Aside => _temp.Combine("login", "claude-abc123.old");

    public void Dispose()
    {
        foreach (IDisposable handle in _handles)
        {
            handle.Dispose();
        }

        _temp.Dispose();
    }

    private static void WriteFile(string directory, string name, string content)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, name), content);
    }

    private static string ReadFile(string directory, string name) => File.ReadAllText(Path.Combine(directory, name));

    /// <summary>Directory.Move that records every call and fails the listed ones (from, to) like a busy folder.</summary>
    private Action<string, string> Moves(params (string From, string To)[] failing) => (from, to) =>
    {
        _moves.Add((from, to));
        if (failing.Contains((from, to)))
        {
            throw new IOException("busy");
        }

        Directory.Move(from, to);
    };

    private bool MoveIntoPlace(Action<string, string>? move = null) =>
        AccountFolders.MoveIntoPlace(Source, Ready, Target, Aside, move ?? Moves());

    [Fact]
    public void MoveIntoPlace_NewAccount_MovesTheFolderAndCreatesTheParent()
    {
        WriteFile(Source, ".credentials.json", "new");
        WriteFile(Path.Combine(Source, "backups"), "b.json", "backup");

        bool cleanedUp = AccountFolders.MoveIntoPlace(Source, Ready, Target, Aside);

        Assert.True(cleanedUp);
        Assert.False(Directory.Exists(Source));
        Assert.False(Directory.Exists(Ready));
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

        bool cleanedUp = MoveIntoPlace();

        Assert.True(cleanedUp);
        Assert.Equal("new", ReadFile(Target, ".credentials.json"));
        Assert.False(File.Exists(Path.Combine(Target, "only-in-old.txt")));
        Assert.False(Directory.Exists(Source));
        Assert.False(Directory.Exists(Ready));
        Assert.False(Directory.Exists(Aside));
        Assert.Equal(new[] { (Source, Ready), (Target, Aside), (Ready, Target) }, _moves);
    }

    [Fact]
    public void MoveIntoPlace_BusySignInFolder_LeavesTheAccountFolderUntouched()
    {
        WriteFile(Target, ".credentials.json", "old");
        WriteFile(Source, ".credentials.json", "new");

        // Claude Code still has a file in the sign-in folder open: Windows refuses to rename it.
        using (new FileStream(Path.Combine(Source, ".credentials.json"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            Assert.ThrowsAny<IOException>(() => MoveIntoPlace());
        }

        Assert.Equal(new[] { (Source, Ready) }, _moves);
        Assert.Equal("old", ReadFile(Target, ".credentials.json"));
        Assert.Equal("new", ReadFile(Source, ".credentials.json"));
        Assert.False(Directory.Exists(Aside));
    }

    [Fact]
    public void MoveIntoPlace_AccountFolderInUse_KeepsItAndTheReadyLogin()
    {
        WriteFile(Target, ".credentials.json", "old");
        WriteFile(Source, ".credentials.json", "new");

        // An open file inside a folder keeps Windows from renaming that folder (like a running renewal command).
        using (new FileStream(Path.Combine(Target, ".credentials.json"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            Assert.ThrowsAny<IOException>(() => MoveIntoPlace());
        }

        Assert.Equal("old", ReadFile(Target, ".credentials.json"));
        Assert.Equal("new", ReadFile(Ready, ".credentials.json"));
        Assert.False(Directory.Exists(Aside));
    }

    [Fact]
    public void MoveIntoPlace_RestoresTheOldFolderWhenTheNewOneCannotBeMoved()
    {
        WriteFile(Target, ".credentials.json", "old");
        WriteFile(Source, ".credentials.json", "new");

        Assert.Throws<IOException>(() => MoveIntoPlace(Moves((Ready, Target))));

        Assert.Equal("old", ReadFile(Target, ".credentials.json"));
        Assert.Equal("new", ReadFile(Ready, ".credentials.json"));
        Assert.False(Directory.Exists(Aside));
    }

    [Fact]
    public void MoveIntoPlace_AfterARestoreFailed_TheNextAttemptPutsTheOldFolderBackBeforeAnythingElse()
    {
        WriteFile(Target, ".credentials.json", "old");
        WriteFile(Source, ".credentials.json", "new");

        // Attempt 1: the new folder cannot move and neither can the old one go back.
        Assert.Throws<IOException>(() => MoveIntoPlace(Moves((Ready, Target), (Aside, Target))));
        Assert.False(Directory.Exists(Target));
        Assert.Equal("old", ReadFile(Aside, ".credentials.json"));

        // Attempt 2 fails again after restoring: the old login must be back in place, not deleted.
        _moves.Clear();
        Assert.Throws<IOException>(() => MoveIntoPlace(Moves((Ready, Target))));
        Assert.Equal((Aside, Target), _moves[0]);
        Assert.Equal("old", ReadFile(Target, ".credentials.json"));
        Assert.False(Directory.Exists(Aside));

        // Attempt 3 resumes from the ready folder.
        Assert.True(MoveIntoPlace());
        Assert.Equal("new", ReadFile(Target, ".credentials.json"));
        Assert.False(Directory.Exists(Ready));
        Assert.False(Directory.Exists(Aside));
    }

    [Fact]
    public void MoveIntoPlace_ResumesFromAnExistingReadyFolder()
    {
        WriteFile(Target, ".credentials.json", "old");
        WriteFile(Ready, ".credentials.json", "new");

        Assert.True(MoveIntoPlace());

        Assert.DoesNotContain(_moves, m => m.From == Source);
        Assert.Equal("new", ReadFile(Target, ".credentials.json"));
        Assert.False(Directory.Exists(Ready));
        Assert.False(Directory.Exists(Aside));
    }

    [Fact]
    public void MoveIntoPlace_RemovesALeftoverAsideFolderWhenTheAccountFolderExists()
    {
        WriteFile(Aside, "stale.txt", "stale");
        WriteFile(Target, ".credentials.json", "old");
        WriteFile(Source, ".credentials.json", "new");

        Assert.True(MoveIntoPlace());

        Assert.Equal("new", ReadFile(Target, ".credentials.json"));
        Assert.False(Directory.Exists(Aside));
    }

    [Fact]
    public void MoveIntoPlace_ReturnsFalseWhenOnlyTheOldFolderCannotBeDeleted()
    {
        WriteFile(Target, ".credentials.json", "old");
        WriteFile(Source, ".credentials.json", "new");
        Action<string, string> move = (from, to) =>
        {
            Directory.Move(from, to);
            if (to == Aside)
            {
                // Something opens a file in the old folder right after it was moved aside.
                _handles.Add(new FileStream(Path.Combine(Aside, ".credentials.json"), FileMode.Open, FileAccess.Read, FileShare.Read));
            }
        };

        bool cleanedUp = MoveIntoPlace(move);

        Assert.False(cleanedUp);
        Assert.Equal("new", ReadFile(Target, ".credentials.json"));
        Assert.True(Directory.Exists(Aside));
    }

    [Fact]
    public void CleanUp_PutsAnOldFolderBackWhenTheAccountFolderIsMissing()
    {
        WriteFile(Aside, ".credentials.json", "old");
        WriteFile(Ready, ".credentials.json", "new");
        WriteFile(Source, ".claude.json", "{}");

        AccountFolders.CleanUp(Source, Ready, Target, Aside);

        Assert.Equal("old", ReadFile(Target, ".credentials.json"));
        Assert.False(Directory.Exists(Aside));
        Assert.False(Directory.Exists(Ready));
        Assert.False(Directory.Exists(Source));
    }

    [Fact]
    public void CleanUp_DeletesALeftoverOldFolderWhenTheAccountFolderExists()
    {
        WriteFile(Target, ".credentials.json", "current");
        WriteFile(Aside, ".credentials.json", "old");

        AccountFolders.CleanUp(Source, Ready, Target, Aside);

        Assert.Equal("current", ReadFile(Target, ".credentials.json"));
        Assert.False(Directory.Exists(Aside));
    }

    [Fact]
    public void RestoreAside_OnlyWhenTheAccountFolderIsMissing()
    {
        WriteFile(Aside, ".credentials.json", "old");
        WriteFile(Target, ".credentials.json", "current");

        Assert.False(AccountFolders.RestoreAside(Target, Aside));
        Assert.Equal("current", ReadFile(Target, ".credentials.json"));

        AccountFolders.Delete(Target);
        Assert.True(AccountFolders.RestoreAside(Target, Aside));
        Assert.Equal("old", ReadFile(Target, ".credentials.json"));
        Assert.False(AccountFolders.RestoreAside(Target, Aside));
    }

    private string LoginDirectory => _temp.Combine("login");

    private string AccountsDirectory => _temp.Combine("accounts");

    [Fact]
    public void SweepLoginFolder_PutsBackAReplacedFolderOfAnAccountInSettingsAndDeletesTheRest()
    {
        var log = new RecordingLog();
        WriteFile(Aside, ".credentials.json", "old");
        WriteFile(Source, ".credentials.json", "unfinished");
        WriteFile(Ready, ".credentials.json", "new");
        WriteFile(_temp.Combine("login", "claude-zzz999.old"), ".credentials.json", "removed account");
        WriteFile(LoginDirectory, "stray.txt", "x");

        AccountFolders.SweepLoginFolder(LoginDirectory, AccountsDirectory, ["claude", "claude#abc123"], log);

        Assert.Equal("old", ReadFile(Target, ".credentials.json"));
        Assert.False(Directory.Exists(_temp.Combine("accounts", "claude-zzz999")));
        Assert.False(Directory.Exists(LoginDirectory));
        Assert.Contains(log.Entries, e => e.Level == "INFO" && e.Message.Contains("claude-abc123", StringComparison.Ordinal));
        Assert.DoesNotContain(log.Entries, e => e.Level == "WARN");
    }

    [Fact]
    public void SweepLoginFolder_DeletesAReplacedFolderWhenTheAccountFolderExists()
    {
        WriteFile(Target, ".credentials.json", "current");
        WriteFile(Aside, ".credentials.json", "old");

        AccountFolders.SweepLoginFolder(LoginDirectory, AccountsDirectory, ["claude#abc123"], new RecordingLog());

        Assert.Equal("current", ReadFile(Target, ".credentials.json"));
        Assert.False(Directory.Exists(LoginDirectory));
    }

    [Fact]
    public void SweepLoginFolder_LeavesAFolderThatCannotBePutBackAndDeletesEverythingElse()
    {
        var log = new RecordingLog();
        WriteFile(Aside, ".credentials.json", "old");
        WriteFile(Source, ".credentials.json", "unfinished");
        WriteFile(Ready, ".credentials.json", "new");
        WriteFile(LoginDirectory, "stray.txt", "x");

        // A file open inside the replaced folder keeps Windows from moving it back.
        using (new FileStream(Path.Combine(Aside, ".credentials.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            AccountFolders.SweepLoginFolder(LoginDirectory, AccountsDirectory, ["claude#abc123"], log);
        }

        Assert.Equal("old", ReadFile(Aside, ".credentials.json"));
        Assert.False(Directory.Exists(Target));
        Assert.False(Directory.Exists(Source));
        Assert.False(Directory.Exists(Ready));
        Assert.False(File.Exists(Path.Combine(LoginDirectory, "stray.txt")));
        Assert.Contains(log.Entries, e => e.Level == "WARN" && e.Message.Contains("claude-abc123", StringComparison.Ordinal));
    }

    [Fact]
    public void SweepLoginFolder_KeepsGoingWhenOneEntryCannotBeDeleted()
    {
        var log = new RecordingLog();
        string busy = _temp.Combine("login", "claude-busy11");
        WriteFile(busy, ".credentials.json", "x");
        WriteFile(Source, ".credentials.json", "unfinished");

        using (new FileStream(Path.Combine(busy, ".credentials.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            AccountFolders.SweepLoginFolder(LoginDirectory, AccountsDirectory, [], log);
        }

        Assert.True(Directory.Exists(busy));
        Assert.False(Directory.Exists(Source));
        Assert.Contains(log.Entries, e => e.Level == "WARN" && e.Message.Contains("claude-busy11", StringComparison.Ordinal));
    }

    [Fact]
    public void SweepLoginFolder_MissingFolderIsNothingToDo()
    {
        var log = new RecordingLog();

        AccountFolders.SweepLoginFolder(LoginDirectory, AccountsDirectory, [], log);

        Assert.Empty(log.Entries);
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
