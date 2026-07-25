using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ModdingTool.App.ViewModels;

public sealed partial class TextureItemViewModel : ObservableObject
{
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

    public void EnsureThumbnail()
    {
        if (Thumbnail is not null || HasError)
        {
            return;
        }

        try
        {
            using var stream = new FileStream(FullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 160;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            Thumbnail = image;
            HasError = false;
        }
        catch
        {
            Thumbnail = null;
            HasError = true;
        }
    }
}