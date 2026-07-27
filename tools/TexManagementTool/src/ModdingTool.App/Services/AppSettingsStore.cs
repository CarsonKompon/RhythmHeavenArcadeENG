using System.IO;
using System.Text.Json;
using ModdingTool.App.ViewModels;

namespace ModdingTool.App.Services;

public sealed record AppSettings(
    string? OriginalFolder = null,
    string? OutputFolder = null,
    bool HideSeenOriginals = false,
    bool HideTodoOriginals = false,
    bool HideGroups = false,
    PaneSource? LeftPaneSource = null,
    PaneSource? RightPaneSource = null,
    bool? LeftHideGroups = null,
    bool? RightHideGroups = null,
    bool LeftOnlyUnfinished = false,
    bool RightOnlyUnfinished = false);

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
                return new AppSettings();
            }

            await using var stream = File.OpenRead(SettingsPath);
            return await JsonSerializer.DeserializeAsync<AppSettings>(stream) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
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

    public static void Save(AppSettings settings)
    {
        var directory = Path.GetDirectoryName(SettingsPath)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = SettingsPath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(settings));
        File.Move(temporaryPath, SettingsPath, true);
    }
}