using ModdingTool.Core.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;

namespace ModdingTool.Core.Services;

public sealed class TextureRenderer(DependencyResolver resolver)
{
    public async Task RenderAsync(
        string outputFolder,
        TextureProject project,
        string targetFileName,
        CancellationToken cancellationToken = default)
    {
        var resolved = resolver.Resolve(project, targetFileName);
        var sourcePath = Path.Combine(outputFolder, resolved.BaseFileName);
        var targetPath = Path.Combine(outputFolder, targetFileName);

        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("The Copy Image source does not exist.", sourcePath);
        }

        using var image = await Image.LoadAsync<Rgba32>(sourcePath, cancellationToken);
        var multiplier = resolved.BrightnessMultiplier;

        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    ref var pixel = ref row[x];
                    pixel.R = Scale(pixel.R, multiplier);
                    pixel.G = Scale(pixel.G, multiplier);
                    pixel.B = Scale(pixel.B, multiplier);
                }
            }
        });

        var temporaryPath = targetPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await image.SaveAsync(temporaryPath, new PngEncoder(), cancellationToken);
            File.Move(temporaryPath, targetPath, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static byte Scale(byte channel, double multiplier) =>
        (byte)Math.Clamp(Math.Round(channel * multiplier), byte.MinValue, byte.MaxValue);
}