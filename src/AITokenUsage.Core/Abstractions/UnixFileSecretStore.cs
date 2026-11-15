using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;

namespace AITokenUsage.Core.Abstractions;

/// <summary>
/// macOS/Linux counterpart of <see cref="DpapiSecretStore"/>: "&lt;name&gt;.secret" files readable only by the current
/// user (0600 in a 0700 folder), the same protection the Codex and GitHub CLIs give their own tokens.
/// </summary>
[UnsupportedOSPlatform("windows")]
public sealed partial class UnixFileSecretStore : ISecretStore
{
    private readonly string _directory;

    public UnixFileSecretStore(string directory)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        _directory = directory;
    }

    public void Save(string name, string secret)
    {
        ArgumentNullException.ThrowIfNull(secret);
        string path = GetPath(name);
        EnsureDirectory();

        string tempPath = path + ".tmp";
        var options = new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
        };
        using (var stream = new FileStream(tempPath, options))
        {
            stream.Write(Encoding.UTF8.GetBytes(secret));
        }

        File.SetUnixFileMode(tempPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(tempPath, path, overwrite: true);
    }

    public string? Load(string name)
    {
        try
        {
            return File.ReadAllText(GetPath(name), Encoding.UTF8);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Delete(string name)
    {
        string path = GetPath(name);
        if (File.Exists(path)) File.Delete(path);
    }

    private void EnsureDirectory()
    {
        if (!Directory.Exists(_directory))
        {
            Directory.CreateDirectory(_directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private string GetPath(string name)
    {
        if (string.IsNullOrEmpty(name) || !ValidName().IsMatch(name))
        {
            throw new ArgumentException($"Invalid secret name '{name}'.", nameof(name));
        }

        return Path.Combine(_directory, name + ".secret");
    }

    [GeneratedRegex(@"^[A-Za-z0-9._#\-]+$")]
    private static partial Regex ValidName();
}
