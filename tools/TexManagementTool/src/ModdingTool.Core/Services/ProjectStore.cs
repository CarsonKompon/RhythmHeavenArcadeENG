using System.Text.Json;
using ModdingTool.Core.Models;

namespace ModdingTool.Core.Services;

public sealed class ProjectStore
{
    public const string MetadataFileName = ".texturemod.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public async Task<TextureProject> LoadAsync(string outputFolder, CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(outputFolder, MetadataFileName);
        if (!File.Exists(path))
        {
            return new TextureProject();
        }

        await using var stream = File.OpenRead(path);
        var project = await JsonSerializer.DeserializeAsync<TextureProject>(stream, JsonOptions, cancellationToken)
                      ?? throw new InvalidDataException("Project metadata is empty.");

        if (!project.IsSupported)
        {
            throw new InvalidDataException($"Project version {project.Version} is not supported.");
        }

        project.Textures = new Dictionary<string, TextureEntry>(project.Textures, StringComparer.OrdinalIgnoreCase);
        return project;
    }

    public async Task SaveAsync(
        string outputFolder,
        TextureProject project,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(outputFolder);
        var path = Path.Combine(outputFolder, MetadataFileName);
        var temporaryPath = path + ".tmp";
        var backupPath = path + ".bak";

        await using (var stream = new FileStream(
                         temporaryPath,
                         FileMode.Create,
                         FileAccess.Write,
                         FileShare.None,
                         4096,
                         FileOptions.Asynchronous))
        {
            await JsonSerializer.SerializeAsync(stream, project, JsonOptions, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }

        if (File.Exists(path))
        {
            File.Replace(temporaryPath, path, backupPath, true);
        }
        else
        {
            File.Move(temporaryPath, path);
        }
    }
}