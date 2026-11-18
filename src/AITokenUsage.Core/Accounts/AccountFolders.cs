using AITokenUsage.Core.Abstractions;

namespace AITokenUsage.Core.Accounts;

/// <summary>
/// Moves and deletes whole account folders inside the app folder (Claude spec 2026-11-16 §2.4). Only renames on one
/// volume: the files inside are never opened, so what the CLI wrote stays exactly as it was. Windows refuses to rename a
/// folder while a file in it is open or a process runs in it; that surfaces as an IOException, and the caller simply
/// tries again.
/// </summary>
public static class AccountFolders
{
    /// <summary>
    /// Puts the finished sign-in folder <paramref name="source"/> in place as the account folder <paramref name="target"/>,
    /// creating the target's parent. Safe to call again after a failure; each call resumes where the last one stopped:
    /// <list type="number">
    /// <item>An old account folder that an earlier call left at <paramref name="aside"/> goes back to the target first.</item>
    /// <item>The sign-in folder is renamed to <paramref name="ready"/>, which shows that nothing holds it any more, before
    /// the live account folder is touched. An existing ready folder means that rename was done already.</item>
    /// <item>An existing target moves to <paramref name="aside"/>, the ready folder moves to the target and the old folder
    /// is deleted. When the ready folder cannot move, the old folder goes back.</item>
    /// </list>
    /// Returns false when only that last delete failed: the new folder is in place and the old one is left at
    /// <paramref name="aside"/>. Throws IOException or UnauthorizedAccessException when a folder could not be moved.
    /// </summary>
    public static bool MoveIntoPlace(string source, string ready, string target, string aside) =>
        MoveIntoPlace(source, ready, target, aside, Directory.Move);

    /// <param name="move">Directory.Move; tests make chosen moves fail.</param>
    internal static bool MoveIntoPlace(string source, string ready, string target, string aside, Action<string, string> move)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(ready);
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(aside);
        ArgumentNullException.ThrowIfNull(move);

        RestoreAside(target, aside, move);
        if (!Directory.Exists(ready))
        {
            move(source, ready);
        }

        CreateParent(target);
        bool replacing = Directory.Exists(target);
        if (replacing)
        {
            // The target exists, so a folder at the aside path is a leftover, never the only copy of a login.
            Delete(aside);
            move(target, aside);
        }

        try
        {
            move(ready, target);
        }
        catch (Exception) when (replacing)
        {
            try
            {
                RestoreAside(target, aside, move);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The old folder stays at the aside path; the next call (or CleanUp) puts it back before anything else.
            }

            throw;
        }

        if (!replacing)
        {
            return true;
        }

        try
        {
            Delete(aside);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Puts an account folder that a replacement left at <paramref name="aside"/> back to <paramref name="target"/> when the
    /// target is missing. True when it moved one. Throws IOException or UnauthorizedAccessException when it could not.
    /// </summary>
    public static bool RestoreAside(string target, string aside) => RestoreAside(target, aside, Directory.Move);

    /// <summary>
    /// After a sign-in, whatever its outcome: an old account folder left at <paramref name="aside"/> goes back when the target
    /// is missing (else it is deleted), then the <paramref name="ready"/> and <paramref name="source"/> folders are deleted.
    /// Throws IOException or UnauthorizedAccessException when a folder could not be moved or deleted.
    /// </summary>
    public static void CleanUp(string source, string ready, string target, string aside)
    {
        if (!RestoreAside(target, aside))
        {
            // Either the target exists or there is no aside folder: nothing at the aside path is the only copy of a login.
            Delete(aside);
        }

        Delete(ready);
        Delete(source);
    }

    /// <summary>
    /// The startup sweep of the login folder (Claude spec 2026-11-16 §2.5), entry by entry. A replaced account folder
    /// ("claude-&lt;key&gt;.old") of an account in <paramref name="settingsIds"/> whose account folder under
    /// <paramref name="accountsDirectory"/> is missing (the app stopped in the middle of a replacement) goes back in place;
    /// when that fails it is logged and left where it is. Everything else is deleted, and an entry that cannot be deleted is
    /// logged and skipped. The login folder itself goes once nothing is left in it; a login folder that is a link is left alone.
    /// </summary>
    public static void SweepLoginFolder(string loginDirectory, string accountsDirectory, IEnumerable<string> settingsIds, IAppLog log)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(loginDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(accountsDirectory);
        ArgumentNullException.ThrowIfNull(settingsIds);
        ArgumentNullException.ThrowIfNull(log);

        var folder = new DirectoryInfo(loginDirectory);
        if (!folder.Exists)
        {
            return;
        }

        if (folder.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            log.Warn("account cleanup: the login folder is a link; left alone");
            return;
        }

        string[] ids = settingsIds.ToArray();
        bool keptAny = false;
        foreach (FileSystemInfo entry in folder.EnumerateFileSystemInfos())
        {
            if (AccountStorage.ClaudeHomeToRestore(entry.Name, ids) is { } home)
            {
                try
                {
                    if (RestoreAside(Path.Combine(accountsDirectory, home), entry.FullName))
                    {
                        log.Info($"account cleanup: put the login folder '{home}' back in place");
                        continue;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // The only copy of that account's login: never deleted.
                    log.Warn($"account cleanup: could not put the login folder '{home}' back; left in place", ex);
                    keptAny = true;
                    continue;
                }
            }

            try
            {
                if (entry is DirectoryInfo)
                {
                    Delete(entry.FullName);
                }
                else
                {
                    entry.Delete();
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log.Warn($"account cleanup: could not remove '{entry.Name}' from the login folder", ex);
                keptAny = true;
            }
        }

        if (!keptAny)
        {
            folder.Delete();
            log.Info("account cleanup: removed leftover login folder");
        }
    }

    /// <summary>Deletes a folder with everything in it; a link (junction or symlink) is removed itself, never followed.</summary>
    public static void Delete(string directory)
    {
        var info = new DirectoryInfo(directory);
        if (!info.Exists)
        {
            return;
        }

        if (info.LinkTarget is not null)
        {
            info.Delete();
        }
        else
        {
            info.Delete(recursive: true);
        }
    }

    private static bool RestoreAside(string target, string aside, Action<string, string> move)
    {
        if (Directory.Exists(target) || !Directory.Exists(aside))
        {
            return false;
        }

        CreateParent(target);
        move(aside, target);
        return true;
    }

    private static void CreateParent(string directory)
    {
        if (Path.GetDirectoryName(directory) is { Length: > 0 } parent)
        {
            Directory.CreateDirectory(parent);
        }
    }
}
