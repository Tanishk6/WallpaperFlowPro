using System.Windows;
using System.Windows.Controls;
using WallpaperFlow.App.ViewModels;

namespace WallpaperFlow.App.Views;

public partial class UploadView : UserControl
{
    public UploadView() => InitializeComponent();

    private void OnDragEnter(object sender, DragEventArgs e)
    {
        bool files = e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effects = files ? DragDropEffects.Copy : DragDropEffects.None;
        if (files) DropZone.SetResourceReference(Border.BorderBrushProperty, "AccentBrush");
    }

    private void OnDragLeave(object sender, DragEventArgs e) => DropZone.SetResourceReference(Border.BorderBrushProperty, "StrokeStrongBrush");

    private async void OnDrop(object sender, DragEventArgs e)
    {
        OnDragLeave(sender, e);
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && DataContext is UploadViewModel vm)
        {
            try { await vm.DropAsync(files); } catch { /* reported by the view model */ }
        }
    }
}
