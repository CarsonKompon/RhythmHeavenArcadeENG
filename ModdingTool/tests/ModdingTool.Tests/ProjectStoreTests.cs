using ModdingTool.Core.Models;
using ModdingTool.Core.Services;

namespace ModdingTool.Tests;

public sealed class ProjectStoreTests
{
    [Fact]
    public void TextureGroup_DefaultsToCollapsed()
    {
        Assert.True(new TextureGroup().IsCollapsed);
    }

    [Fact]
    public async Task SaveAndLoad_PreservesCaseInsensitiveTextureLookup()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"TextureModStoreTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            var project = new TextureProject();
            project.Textures["ABCDEF12.png"] = new TextureEntry { Brightness = 42, IsSeen = true, IsTodo = true };
            project.Groups.Add(new TextureGroup { Name = "Menus", Order = 2, IsCollapsed = true });
            var store = new ProjectStore();

            await store.SaveAsync(folder, project);
            var loaded = await store.LoadAsync(folder);

            Assert.Equal(42, loaded.Textures["abcdef12.PNG"].Brightness);
            Assert.True(loaded.Textures["abcdef12.PNG"].IsSeen);
            Assert.True(loaded.Textures["abcdef12.PNG"].IsTodo);
            Assert.True(loaded.Groups.Single().IsCollapsed);
            Assert.Equal(2, loaded.Groups.Single().Order);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }
}