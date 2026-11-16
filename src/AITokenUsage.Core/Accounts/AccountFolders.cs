namespace AITokenUsage.Core.Accounts;

/// <summary>
/// Moves and deletes whole account folders inside the app folder (Claude spec 2026-11-16 §2.4). Only renames on one
/// volume: the files inside are never opened, so what the CLI wrote stays exactly as it was. Windows refuses to rename a
/// folder while a file in it is open or a process runs in it; that surfaces as an IOException.
/// </summary>
public static class AccountFolders
{
    /// <summary>
    /// Moves the finished sign-in folder <paramref name="source"/> to the account folder <paramref name="target"/>,
    /// creating the target's parent. An existing target is first moved to <paramref name="aside"/> (a leftover there is
    /// deleted before) and deleted once the new folder is in place; when the new folder cannot be moved, the old one is
    /// moved back and the exception is rethrown.
    /// Returns false when only that last delete failed: the new folder is in place and the old one is left at
    /// <paramref name="aside"/>. Throws IOException or UnauthorizedAccessException when a folder could not be moved.
    /// </summary>
    public static bool MoveIntoPlace(string source, string target, string aside)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(aside);

        Delete(aside);
        if (Path.GetDirectoryName(target) is { Length: > 0 } parent)
        {
            Directory.CreateDirectory(parent);
        }

        bool replacing = Directory.Exists(target);
        if (replacing)
        {
            Directory.Move(target, aside);
        }

        try
        {
            Directory.Move(source, target);
        }
        catch (Exception) when (replacing)
        {
            Restore(aside, target);
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

    private static void Restore(string aside, string target)
    {
        try
        {
            Directory.Move(aside, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The old folder stays at the aside path; the caller reports the original failure.
        }
    }
}
