using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ModdingTool.App.ViewModels;

public sealed partial class TextureItemViewModel : ObservableObject
{
    private Task<ImageSource?>? thumbnailLoadTask;

    public TextureItemViewModel(string fileName, string folder, bool isOutput)
    {
        FileName = fileName;
        FullPath = Path.Combine(folder, fileName);
        IsOutput = isOutput;
    }

    public string FileName { get; }

    public string FullPath { get; }

    public bool IsOutput { get; }

    [ObservableProperty]
    private ImageSource? thumbnail;

    [ObservableProperty]
    private string? copySourceFileName;

    [ObservableProperty]
    private int brightness = 100;

    [ObservableProperty]
    private string groupName = "Ungrouped";

    [ObservableProperty]
    private bool hasError;

    [ObservableProperty]
    private bool isSeen;

    public bool HasCopySource => !string.IsNullOrWhiteSpace(CopySourceFileName);

    partial void OnCopySourceFileNameChanged(string? value)
    {
        OnPropertyChanged(nameof(HasCopySource));
    }

    public async Task EnsureThumbnailAsync()
    {
        if (Thumbnail is not null || HasError)
        {
            return;
        }

        try
        {
            thumbnailLoadTask ??= ThumbnailCache.LoadAsync(FullPath);
            Thumbnail = await thumbnailLoadTask;
            HasError = false;
        }
        catch
        {
            Thumbnail = null;
            HasError = true;
        }
    }

    private static class ThumbnailCache
    {
        private const int Capacity = 400;
        private static readonly Dictionary<string, ImageSource> Images = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Queue<string> InsertionOrder = new();
        private static readonly SemaphoreSlim DecodeSlots = new(4);
        private static readonly object SyncRoot = new();

        public static async Task<ImageSource?> LoadAsync(string path)
        {
            var file = new FileInfo(path);
            var cacheKey = $"{path}|{file.Length}|{file.LastWriteTimeUtc.Ticks}";
            lock (SyncRoot)
            {
                if (Images.TryGetValue(cacheKey, out var cached))
                {
                    return cached;
                }
            }

            await DecodeSlots.WaitAsync();
            try
            {
                lock (SyncRoot)
                {
                    if (Images.TryGetValue(cacheKey, out var cached))
                    {
                        return cached;
                    }
                }

                var image = await Task.Run(() => Decode(path));
                lock (SyncRoot)
                {
                    Images[cacheKey] = image;
                    InsertionOrder.Enqueue(cacheKey);
                    while (Images.Count > Capacity)
                    {
                        Images.Remove(InsertionOrder.Dequeue());
                    }
                }

                return image;
            }
            finally
            {
                DecodeSlots.Release();
            }
        }

        private static ImageSource Decode(string path)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 160;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
    }
}