using ModdingTool.Core.Models;

namespace ModdingTool.Core.Services;

public sealed class TextureWorkspace
{
    private readonly DependencyResolver resolver = new();
    private readonly ProjectStore store = new();
    private readonly SemaphoreSlim writeLock = new(1, 1);
    private readonly TextureRenderer renderer;

    public TextureWorkspace()
    {
        renderer = new TextureRenderer(resolver);
    }

    public string? OriginalFolder { get; private set; }

    public string? OutputFolder { get; private set; }

    public TextureProject Project { get; private set; } = new();

    public async Task OpenAsync(string originalFolder, string outputFolder, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(outputFolder);
        Project = await store.LoadAsync(outputFolder, cancellationToken);
        Project.OriginalFolder = originalFolder;
        OriginalFolder = originalFolder;
        OutputFolder = outputFolder;
        await store.SaveAsync(outputFolder, Project, cancellationToken);
    }

    public IReadOnlyList<string> ScanOriginals()
    {
        // Once a texture exists in the output folder it is considered "claimed" and must
        // disappear from the originals list (copying/dragging removes it immediately).
        var outputs = ScanFolder(OutputFolder).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return ScanFolder(OriginalFolder).Where(fileName => !outputs.Contains(fileName)).ToArray();
    }

    public IReadOnlyList<string> ScanOutputs() => ScanFolder(OutputFolder);

    public async Task CopyOriginalAsync(string fileName, bool overwrite, CancellationToken cancellationToken = default)
    {
        EnsureOpen();
        var sourcePath = Path.Combine(OriginalFolder!, fileName);
        var targetPath = Path.Combine(OutputFolder!, fileName);

        await writeLock.WaitAsync(cancellationToken);
        try
        {
            File.Copy(sourcePath, targetPath, overwrite);
            var entry = GetOrCreateEntry(fileName);
            entry.CopySourceFileName = null;
            entry.Brightness = 100;
            await SaveAndRenderDependentsAsync(fileName, cancellationToken);
        }
        finally
        {
            writeLock.Release();
        }
    }

    public async Task AssignCopyAsync(
        string targetFileName,
        string sourceFileName,
        CancellationToken cancellationToken = default)
    {
        EnsureOpen();
        if (resolver.WouldCreateCycle(Project, targetFileName, sourceFileName))
        {
            throw new InvalidOperationException("That Copy Image assignment would create a cycle.");
        }

        await writeLock.WaitAsync(cancellationToken);
        try
        {
            var entry = GetOrCreateEntry(targetFileName);
            entry.CopySourceFileName = sourceFileName;
            entry.Brightness = Math.Clamp(entry.Brightness, 0, 100);
            await store.SaveAsync(OutputFolder!, Project, cancellationToken);
            await renderer.RenderAsync(OutputFolder!, Project, targetFileName, cancellationToken);
            await RenderDependentsAsync(targetFileName, cancellationToken);
        }
        finally
        {
            writeLock.Release();
        }
    }

    public async Task SetBrightnessAsync(string fileName, int brightness, CancellationToken cancellationToken = default)
    {
        EnsureOpen();
        var entry = GetOrCreateEntry(fileName);
        if (string.IsNullOrWhiteSpace(entry.CopySourceFileName))
        {
            return;
        }

        await writeLock.WaitAsync(cancellationToken);
        try
        {
            entry.Brightness = Math.Clamp(brightness, 0, 100);
            await store.SaveAsync(OutputFolder!, Project, cancellationToken);
            await renderer.RenderAsync(OutputFolder!, Project, fileName, cancellationToken);
            await RenderDependentsAsync(fileName, cancellationToken);
        }
        finally
        {
            writeLock.Release();
        }
    }

    public async Task ClearCopyAsync(string fileName, CancellationToken cancellationToken = default)
    {
        EnsureOpen();
        await writeLock.WaitAsync(cancellationToken);
        try
        {
            var entry = GetOrCreateEntry(fileName);
            entry.CopySourceFileName = null;
            entry.Brightness = 100;
            var originalPath = Path.Combine(OriginalFolder!, fileName);
            if (File.Exists(originalPath))
            {
                File.Copy(originalPath, Path.Combine(OutputFolder!, fileName), true);
            }

            await SaveAndRenderDependentsAsync(fileName, cancellationToken);
        }
        finally
        {
            writeLock.Release();
        }
    }

    public async Task RefreshSourceAsync(string fileName, CancellationToken cancellationToken = default)
    {
        EnsureOpen();
        await writeLock.WaitAsync(cancellationToken);
        try
        {
            await RenderDependentsAsync(fileName, cancellationToken);
        }
        finally
        {
            writeLock.Release();
        }
    }

    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        EnsureOpen();
        await store.SaveAsync(OutputFolder!, Project, cancellationToken);
    }

    public ResolvedTexture Resolve(string fileName) => resolver.Resolve(Project, fileName);

    private TextureEntry GetOrCreateEntry(string fileName)
    {
        if (!Project.Textures.TryGetValue(fileName, out var entry))
        {
            entry = new TextureEntry { Order = Project.Textures.Count };
            Project.Textures[fileName] = entry;
        }

        return entry;
    }

    private async Task SaveAndRenderDependentsAsync(string fileName, CancellationToken cancellationToken)
    {
        await store.SaveAsync(OutputFolder!, Project, cancellationToken);
        await RenderDependentsAsync(fileName, cancellationToken);
    }

    private async Task RenderDependentsAsync(string fileName, CancellationToken cancellationToken)
    {
        foreach (var dependent in resolver.GetDependents(Project, fileName))
        {
            await renderer.RenderAsync(OutputFolder!, Project, dependent, cancellationToken);
        }
    }

    private static IReadOnlyList<string> ScanFolder(string? folder) =>
        string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)
            ? []
            : Directory.EnumerateFiles(folder, "*.png", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName)
                .Where(fileName => fileName is not null)
                .Cast<string>()
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();

    private void EnsureOpen()
    {
        if (OriginalFolder is null || OutputFolder is null)
        {
            throw new InvalidOperationException("Open original and output folders first.");
        }
    }
}