using System.Windows;
using System.Windows.Controls;
using AuraClean.ViewModels;

namespace AuraClean.Views;

public partial class FileShredderView : UserControl
{
    public FileShredderView()
    {
        InitializeComponent();
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnFileDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop) &&
            e.Data.GetData(DataFormats.FileDrop) is string[] files &&
            DataContext is FileShredderViewModel vm)
        {
            vm.AddDroppedFiles(files);
        }
    }
}
