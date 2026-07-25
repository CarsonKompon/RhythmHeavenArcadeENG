using ModdingTool.Core.Models;
using ModdingTool.Core.Services;

namespace ModdingTool.Tests;

public sealed class DependencyResolverTests
{
    private readonly DependencyResolver resolver = new();

    [Fact]
    public void Resolve_UsesUltimateSourceAndCumulativeBrightness()
    {
        var project = new TextureProject
        {
            Textures = new Dictionary<string, TextureEntry>(StringComparer.OrdinalIgnoreCase)
            {
                ["middle.png"] = new() { CopySourceFileName = "base.png", Brightness = 50 },
                ["target.png"] = new() { CopySourceFileName = "middle.png", Brightness = 40 }
            }
        };

        var result = resolver.Resolve(project, "target.png");

        Assert.Equal("base.png", result.BaseFileName);
        Assert.Equal(0.2, result.BrightnessMultiplier, 5);
    }

    [Fact]
    public void WouldCreateCycle_DetectsTransitiveCycle()
    {
        var project = new TextureProject
        {
            Textures = new Dictionary<string, TextureEntry>(StringComparer.OrdinalIgnoreCase)
            {
                ["b.png"] = new() { CopySourceFileName = "a.png" },
                ["c.png"] = new() { CopySourceFileName = "b.png" }
            }
        };

        Assert.True(resolver.WouldCreateCycle(project, "a.png", "c.png"));
        Assert.False(resolver.WouldCreateCycle(project, "c.png", "a.png"));
    }

    [Fact]
    public void GetDependents_ReturnsParentsBeforeChildren()
    {
        var project = new TextureProject
        {
            Textures = new Dictionary<string, TextureEntry>(StringComparer.OrdinalIgnoreCase)
            {
                ["b.png"] = new() { CopySourceFileName = "a.png" },
                ["c.png"] = new() { CopySourceFileName = "b.png" }
            }
        };

        Assert.Equal(["b.png", "c.png"], resolver.GetDependents(project, "a.png"));
    }
}