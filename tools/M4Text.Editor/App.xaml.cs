using System.Text;
using System.Windows;

namespace M4Text.Editor;

public partial class App : Application
{
    public App()
    {
        // Enable Shift-JIS (code page 932) for the scanner.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }
}
