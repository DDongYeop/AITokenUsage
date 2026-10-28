using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AITokenUsage.Core.Abstractions;

namespace AITokenUsage.Core.Settings;

/// <summary>
/// Loads and saves <see cref="AppSettings"/> as camelCase JSON with string enums.
/// Thread-safe; callers should still keep it off the UI thread.
/// </summary>
public sealed class SettingsStore
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private readonly object _gate = new();
    private readonly IAppLog _log;

    public SettingsStore(string filePath, IAppLog log)
    {
        ArgumentException.ThrowIfNullOrEmpty(filePath);
        FilePath = filePath;
        _log = log;
    }

    public string FilePath { get; }

    public string BackupPath => FilePath + ".bak";

    /// <summary>
    /// Missing file → defaults. Unreadable file → defaults (file left alone).
    /// Corrupt JSON → the file is moved to settings.json.bak and defaults are returned.
    /// The result is always normalized.
    /// </summary>
    public AppSettings Load()
    {
        lock (_gate)
        {
            string json;
            try
            {
                if (!File.Exists(FilePath))
                {
                    return new AppSettings().Normalize();
                }

                json = File.ReadAllText(FilePath, Encoding.UTF8);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.Warn("Settings file could not be read; using defaults for this session.", ex);
                return new AppSettings().Normalize();
            }

            try
            {
                AppSettings? settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                if (settings is null)
                {
                    throw new JsonException("Settings root is null.");
                }

                return settings.Normalize();
            }
            catch (JsonException ex)
            {
                _log.Warn("Settings file is corrupt; moving it to settings.json.bak and using defaults.", ex);
                BackUpCorruptFile();
                return new AppSettings().Normalize();
            }
        }
    }

    /// <summary>Writes settings.json.tmp, then atomically renames it over settings.json.</summary>
    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        string json = JsonSerializer.Serialize(settings, JsonOptions);
        string tempPath = FilePath + ".tmp";

        lock (_gate)
        {
            string? directory = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            try
            {
                File.WriteAllText(tempPath, json, Utf8NoBom);
                File.Move(tempPath, FilePath, overwrite: true);
            }
            catch
            {
                TryDelete(tempPath);
                throw;
            }
        }
    }

    private void BackUpCorruptFile()
    {
        try
        {
            File.Move(FilePath, BackupPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warn("Corrupt settings file could not be moved to settings.json.bak.", ex);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort; a stale .tmp file is overwritten by the next save.
        }
    }
}
