using System.Text.Json.Serialization;

namespace ModdingTool.Core.Models;

public sealed class TextureProject
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    public string? OriginalFolder { get; set; }

    public Dictionary<string, TextureEntry> Textures { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    public List<TextureGroup> Groups { get; set; } = [];

    [JsonIgnore]
    public bool IsSupported => Version == CurrentVersion;
}

public sealed class TextureEntry
{
    public bool IsSeen { get; set; }

    public string? CopySourceFileName { get; set; }

    public int Brightness { get; set; } = 100;

    public Guid? GroupId { get; set; }

    public int Order { get; set; }
}

public sealed class TextureGroup
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = "New group";

    public int Order { get; set; }

    public bool IsCollapsed { get; set; }
}