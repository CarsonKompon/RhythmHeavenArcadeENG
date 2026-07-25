using System.IO;
using System.Text.Json;

namespace ModdingTool.App.Services;

public sealed record AppSettings(string? OriginalFolder, string? OutputFolder);

public static class AppSettingsStore
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TextureWorkshop",
        "settings.json");

    public static async Task<AppSettings> LoadAsync()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return new AppSettings(null, null);
            }

            await using var stream = File.OpenRead(SettingsPath);
            return await JsonSerializer.DeserializeAsync<AppSettings>(stream) ?? new AppSettings(null, null);
        }
        catch
        {
            return new AppSettings(null, null);
        }
    }

    public static async Task SaveAsync(AppSettings settings)
    {
        var directory = Path.GetDirectoryName(SettingsPath)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = SettingsPath + ".tmp";
        await using (var stream = File.Create(temporaryPath))
        {
            await JsonSerializer.SerializeAsync(stream, settings);
        }

        File.Move(temporaryPath, SettingsPath, true);
    }
}