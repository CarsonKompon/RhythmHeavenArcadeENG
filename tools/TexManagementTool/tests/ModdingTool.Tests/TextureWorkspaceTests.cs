using ModdingTool.Core.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace ModdingTool.Tests;

public sealed class TextureWorkspaceTests
{
    [Fact]
    public async Task AssignCopy_RendersToTargetNameAndPersistsRelationship()
    {
        var root = Path.Combine(Path.GetTempPath(), $"TextureWorkspaceTests-{Guid.NewGuid():N}");
        var originals = Path.Combine(root, "originals");
        var output = Path.Combine(root, "output");
        Directory.CreateDirectory(originals);
        Directory.CreateDirectory(output);

        try
        {
            using (var source = new Image<Rgba32>(1, 1, new Rgba32(80, 60, 40, 20)))
            {
                await source.SaveAsPngAsync(Path.Combine(originals, "source.png"));
                await source.SaveAsPngAsync(Path.Combine(originals, "target.png"));
            }

            var workspace = new TextureWorkspace();
            await workspace.OpenAsync(originals, output);
            await workspace.CopyOriginalAsync("source.png", true);
            await workspace.CopyOriginalAsync("target.png", true);
            await workspace.AssignCopyAsync("target.png", "source.png");
            await workspace.SetBrightnessAsync("target.png", 50);

            Assert.True(File.Exists(Path.Combine(output, "target.png")));
            Assert.Equal("source.png", workspace.Project.Textures["target.png"].CopySourceFileName);
            using var result = await Image.LoadAsync<Rgba32>(Path.Combine(output, "target.png"));
            Assert.Equal(new Rgba32(40, 30, 20, 20), result[0, 0]);

            var reloaded = await new ProjectStore().LoadAsync(output);
            Assert.Equal(50, reloaded.Textures["target.png"].Brightness);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}