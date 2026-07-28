using System.Linq;
using System.Windows;

namespace M4Text.Editor;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        var vm = new MainViewModel();
        vm.SelectionShouldScrollIntoView += ScrollStringsSelectionIntoView;
        DataContext = vm;
    }

    // After the Strings list is re-filtered, bring the (preserved) selected row back
    // into view. Deferred to Background priority so the DataGrid has regenerated its
    // rows for the new filter before we scroll.
    private void ScrollStringsSelectionIntoView()
    {
        if (DataContext is not MainViewModel vm || vm.SelectedEntry is null) return;
        Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Background,
            () =>
            {
                var item = vm.SelectedEntry;
                if (item is null) return;
                StringsGrid.ScrollIntoView(item);
            });
    }

    // Hide/unhide the current multi-selection. SelectedItems isn't bindable, so the
    // menu clicks are handled here and forwarded to the view-model.
    private void HideSelected_Click(object sender, RoutedEventArgs e) => SetSelectionHidden(true);
    private void UnhideSelected_Click(object sender, RoutedEventArgs e) => SetSelectionHidden(false);

    private void SetSelectionHidden(bool hidden)
    {
        if (DataContext is not MainViewModel vm) return;
        var items = StringsGrid.SelectedItems.OfType<TextEntry>().ToList();
        if (items.Count == 0) return;
        vm.SetEntriesHidden(items, hidden);
    }
}
