using System.Text.Json;
using RobloxMultiLauncher.Models;

namespace RobloxMultiLauncher.Services;

public sealed class ProfileStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static string ProfilesPath => Path.Combine(AppPaths.DataDir, "profiles.json");
    private static string SettingsPath => Path.Combine(AppPaths.DataDir, "settings.json");

    public List<StoredProfile> LoadProfiles() => Load<List<StoredProfile>>(ProfilesPath) ?? new();
    public AppSettings LoadSettings() => Load<AppSettings>(SettingsPath) ?? new();
    public void SaveProfiles(IEnumerable<StoredProfile> profiles) => Save(ProfilesPath, profiles.ToList());
    public void SaveSettings(AppSettings settings) => Save(SettingsPath, settings);

    private static T? Load<T>(string path)
    {
        try
        {
            if (!File.Exists(path)) return default;
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path));
        }
        catch (Exception ex)
        {
            Logger.Error($"Could not read {Path.GetFileName(path)}; keeping a backup and starting fresh", ex);
            try { File.Move(path, path + ".corrupt-" + DateTime.Now.ToString("yyyyMMddHHmmss")); } catch { }
            return default;
        }
    }

    private static void Save<T>(string path, T value)
    {
        try
        {
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(value, Json));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            Logger.Error($"Could not save {Path.GetFileName(path)}", ex);
            throw;
        }
    }
}
