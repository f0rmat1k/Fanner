using System.Text.Json;

namespace Fanner.Core.Config;

/// <summary>
/// Reads and writes the config file under the user's roaming profile.
/// </summary>
/// <remarks>
/// Never throws. A missing, unreadable or corrupt file yields an empty config: the
/// app must start and show the hardware even when its settings are unusable, since
/// failing to launch is a far worse outcome than losing a curve.
/// </remarks>
public sealed class ConfigStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _path;

    public ConfigStore(string? path = null) =>
        _path = path ?? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Fanner",
            "config.json");

    /// <summary>Where the config lives. Named to stay clear of <see cref="System.IO.Path"/>.</summary>
    public string FilePath => _path;

    /// <summary>
    /// Reads the config, already migrated to the current shape and guaranteed to
    /// contain at least one profile.
    /// </summary>
    public FannerConfig Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return new FannerConfig().Migrated();
            }

            var loaded = JsonSerializer.Deserialize<FannerConfig>(File.ReadAllText(_path), Options)
                ?? new FannerConfig();

            return loaded.Migrated();
        }
        catch (Exception)
        {
            return new FannerConfig().Migrated();
        }
    }

    /// <summary>Writes the config. Returns false if it could not be saved.</summary>
    public bool Save(FannerConfig config)
    {
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // Write beside the target and move into place, so an interrupted save
            // cannot leave a half-written file that the next launch would parse as
            // a curve and act on.
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(config, Options));
            File.Move(temp, _path, overwrite: true);

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
