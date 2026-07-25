using ModdingTool.Core.Models;
using ModdingTool.Core.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace ModdingTool.Tests;

public sealed class TextureRendererTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), $"TextureModTests-{Guid.NewGuid():N}");

    public TextureRendererTests()
    {
        Directory.CreateDirectory(folder);
    }

    [Fact]
    public async Task RenderAsync_ScalesRgbAndPreservesAlpha()
    {
        var basePath = Path.Combine(folder, "base.png");
        using (var source = new Image<Rgba32>(1, 1, new Rgba32(200, 100, 40, 73)))
        {
            await source.SaveAsPngAsync(basePath);
        }

        var project = new TextureProject
        {
            Textures = new Dictionary<string, TextureEntry>(StringComparer.OrdinalIgnoreCase)
            {
                ["target.png"] = new() { CopySourceFileName = "base.png", Brightness = 25 }
            }
        };

        var renderer = new TextureRenderer(new DependencyResolver());
        await renderer.RenderAsync(folder, project, "target.png");

        Assert.True(File.Exists(Path.Combine(folder, "target.png")));
        using var result = await Image.LoadAsync<Rgba32>(Path.Combine(folder, "target.png"));
        Assert.Equal(new Rgba32(50, 25, 10, 73), result[0, 0]);
    }

    [Fact]
    public async Task RenderAsync_ZeroBrightnessMakesRgbBlackOnly()
    {
        using (var source = new Image<Rgba32>(1, 1, new Rgba32(255, 127, 4, 191)))
        {
            await source.SaveAsPngAsync(Path.Combine(folder, "base.png"));
        }

        var project = new TextureProject
        {
            Textures = new Dictionary<string, TextureEntry>(StringComparer.OrdinalIgnoreCase)
            {
                ["dark.png"] = new() { CopySourceFileName = "base.png", Brightness = 0 }
            }
        };

        await new TextureRenderer(new DependencyResolver()).RenderAsync(folder, project, "dark.png");

        using var result = await Image.LoadAsync<Rgba32>(Path.Combine(folder, "dark.png"));
        Assert.Equal(new Rgba32(0, 0, 0, 191), result[0, 0]);
    }

    public void Dispose()
    {
        Directory.Delete(folder, true);
    }
}