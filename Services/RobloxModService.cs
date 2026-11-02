using System.Text.Json;
using System.Text.Json.Nodes;

namespace RobloxMultiLauncher.Services;

public static class RobloxModService
{
    private const string SkyFlag = "FFlagDebugSkyGray";
    private const string FontBaseName = "CustomFont";
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static string? FindLatestVersionDir()
    {
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Roblox", "Versions");
        if (!Directory.Exists(root)) return null;
        return new DirectoryInfo(root).GetDirectories("version-*")
            .Where(d => File.Exists(Path.Combine(d.FullName, "RobloxPlayerBeta.exe")))
            .OrderByDescending(d => d.LastWriteTimeUtc)
            .FirstOrDefault()?.FullName;
    }

    private static string FlagsPath(string versionDir) =>
        Path.Combine(versionDir, "ClientSettings", "ClientAppSettings.json");

    private static JsonObject ReadFlags(string versionDir)
    {
        string path = FlagsPath(versionDir);
        if (!File.Exists(path)) return new JsonObject();
        try { return JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new JsonObject(); }
        catch (JsonException) { return new JsonObject(); }
    }

    // Applies the edit, then writes the file or deletes it when no flags remain.
    private static void UpdateFlags(string versionDir, Action<JsonObject> edit)
    {
        var flags = ReadFlags(versionDir);
        edit(flags);

        string path = FlagsPath(versionDir);
        if (flags.Count == 0) { if (File.Exists(path)) File.Delete(path); return; }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, flags.ToJsonString(Indented));
    }

    public static bool GetGraySky(string versionDir) =>
        ReadFlags(versionDir).TryGetPropertyValue(SkyFlag, out var v) &&
        string.Equals(v?.ToString(), "true", StringComparison.OrdinalIgnoreCase);

    public static void SetGraySky(string versionDir, bool enabled) =>
        UpdateFlags(versionDir, f => { if (enabled) f[SkyFlag] = true; else f.Remove(SkyFlag); });

    // Points every font family at one custom file; original family JSON is kept as .bak for restore.
    public static int ApplyFont(string versionDir, string fontFile)
    {
        string fontsDir = Path.Combine(versionDir, "content", "fonts");
        string familiesDir = Path.Combine(fontsDir, "families");
        if (!Directory.Exists(familiesDir)) throw new DirectoryNotFoundException("Font families folder not found in this Roblox version.");

        string ext = Path.GetExtension(fontFile).ToLowerInvariant();
        if (ext is not (".ttf" or ".otf")) throw new ArgumentException("Choose a .ttf or .otf font file.");

        DeleteCustomFonts(fontsDir);
        string targetName = FontBaseName + ext;
        File.Copy(fontFile, Path.Combine(fontsDir, targetName), true);

        int changed = 0;
        foreach (string json in Directory.GetFiles(familiesDir, "*.json"))
        {
            if (JsonNode.Parse(File.ReadAllText(json)) is not JsonObject family || family["faces"] is not JsonArray faces) continue;

            string backup = json + ".bak";
            if (!File.Exists(backup)) File.Copy(json, backup);

            foreach (var face in faces.OfType<JsonObject>())
                face["assetId"] = "rbxasset://fonts/" + targetName;
            File.WriteAllText(json, family.ToJsonString(Indented));
            changed++;
        }
        return changed;
    }

    public static void RestoreFont(string versionDir)
    {
        string fontsDir = Path.Combine(versionDir, "content", "fonts");
        string familiesDir = Path.Combine(fontsDir, "families");
        if (Directory.Exists(familiesDir))
            foreach (string backup in Directory.GetFiles(familiesDir, "*.json.bak"))
                File.Move(backup, backup[..^4], true);
        DeleteCustomFonts(fontsDir);
    }

    private static void DeleteCustomFonts(string fontsDir)
    {
        if (!Directory.Exists(fontsDir)) return;
        foreach (string f in Directory.GetFiles(fontsDir, FontBaseName + ".*")) File.Delete(f);
    }
}
