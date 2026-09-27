using System.Windows;
using System.Windows.Controls;
using WallpaperFlow.App.ViewModels;

namespace WallpaperFlow.App.Views;

public partial class LibraryView : UserControl
{
    private const double CardWidth = 264; // card 248 + right margin 16

    public LibraryView() => InitializeComponent();

    private LibraryViewModel? Vm => DataContext as LibraryViewModel;

    private void OnGridSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (Vm is { } vm) vm.Columns = Math.Max(1, (int)((e.NewSize.Width - 12) / CardWidth));
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && Vm is { } vm) vm.ImportDropped(files);
    }

    private void OnCollectionMenu(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        var menu = new ContextMenu();
        foreach (var c in vm.Collections)
            menu.Items.Add(new MenuItem { Header = c.Name, Command = vm.AddToCollectionCommand, CommandParameter = c });
        if (menu.Items.Count > 0) menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = "New collection…", Command = vm.NewCollectionCommand });
        menu.PlacementTarget = (UIElement)sender;
        menu.IsOpen = true;
    }
}
