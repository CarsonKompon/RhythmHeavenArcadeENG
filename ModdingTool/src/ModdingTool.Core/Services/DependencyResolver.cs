using ModdingTool.Core.Models;

namespace ModdingTool.Core.Services;

public sealed record ResolvedTexture(string BaseFileName, double BrightnessMultiplier);

public sealed class DependencyResolver
{
    public bool WouldCreateCycle(TextureProject project, string targetFileName, string sourceFileName)
    {
        var current = sourceFileName;
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        while (!string.IsNullOrWhiteSpace(current) && visited.Add(current))
        {
            if (string.Equals(current, targetFileName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            current = project.Textures.GetValueOrDefault(current)?.CopySourceFileName;
        }

        return false;
    }

    public ResolvedTexture Resolve(TextureProject project, string fileName)
    {
        var current = fileName;
        var multiplier = 1d;
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        while (project.Textures.TryGetValue(current, out var entry) &&
               !string.IsNullOrWhiteSpace(entry.CopySourceFileName))
        {
            if (!visited.Add(current))
            {
                throw new InvalidOperationException($"Copy Image cycle detected at '{current}'.");
            }

            multiplier *= Math.Clamp(entry.Brightness, 0, 100) / 100d;
            current = entry.CopySourceFileName;
        }

        return new ResolvedTexture(current, multiplier);
    }

    public IReadOnlyList<string> GetDependents(TextureProject project, string sourceFileName)
    {
        var result = new List<string>();
        var queue = new Queue<string>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { sourceFileName };
        queue.Enqueue(sourceFileName);

        while (queue.TryDequeue(out var current))
        {
            foreach (var pair in project.Textures.Where(pair =>
                         string.Equals(pair.Value.CopySourceFileName, current, StringComparison.OrdinalIgnoreCase)))
            {
                if (visited.Add(pair.Key))
                {
                    result.Add(pair.Key);
                    queue.Enqueue(pair.Key);
                }
            }
        }

        return result;
    }
}