using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using AuraClean.Helpers;
using AuraClean.ViewModels;
using AuraClean.Views.Controls;

namespace AuraClean.Views;

public partial class FileShredderView : UserControl
{
    private static readonly Color DropHighlightColor = (Color)ColorConverter.ConvertFromString("#E86070");
    private FileShredderViewModel? _subscribedVm;

    public FileShredderView()
    {
        InitializeComponent();
        AddHandler(DragEnterEvent, new DragEventHandler(OnDragEnterZone), true);
        AddHandler(DragLeaveEvent, new DragEventHandler(OnDragLeaveZone), true);
        DataContextChanged += OnDataContextChanged;
        Unloaded += (_, _) => UnsubscribeVm();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        UnsubscribeVm();
        if (e.NewValue is FileShredderViewModel vm)
        {
            _subscribedVm = vm;
            vm.PropertyChanged += OnVmPropertyChanged;
        }
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FileShredderViewModel.HasResults) &&
            sender is FileShredderViewModel vm && vm.HasResults)
        {
            var severity = vm.LastFailed > 0 ? ResultCard.Severity.Warning : ResultCard.Severity.Success;
            var desc = vm.LastFailed > 0
                ? $"{vm.LastShredded} shredded, {vm.LastFailed} failed"
                : $"{vm.LastShredded} files securely shredded";
            ShredderResultCard.Show(
                FormatHelper.FormatBytes(vm.LastBytesOverwritten),
                desc,
                severity);
        }
    }

    private void UnsubscribeVm()
    {
        if (_subscribedVm is not null)
        {
            _subscribedVm.PropertyChanged -= OnVmPropertyChanged;
            _subscribedVm = null;
        }
    }

    private void OnDropZoneKeyDown(object sender, KeyEventArgs e)
    {
        if ((e.Key == Key.Enter || e.Key == Key.Space) &&
            DataContext is FileShredderViewModel vm &&
            vm.AddFilesCommand.CanExecute(null))
        {
            vm.AddFilesCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDragEnterZone(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
            return;

        DropOverlay.Visibility = Visibility.Visible;

        var anim = new ColorAnimation(DropHighlightColor, TimeSpan.FromMilliseconds(200))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        DropZoneBorder.BorderBrush = new SolidColorBrush(Colors.Transparent);
        DropZoneBorder.BorderBrush.BeginAnimation(SolidColorBrush.ColorProperty, anim);
    }

    private void OnDragLeaveZone(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;

        var anim = new ColorAnimation(Colors.Transparent, TimeSpan.FromMilliseconds(250))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
        };
        DropZoneBorder.BorderBrush?.BeginAnimation(SolidColorBrush.ColorProperty, anim);
    }

    private void OnFileDrop(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;

        if (e.Data.GetDataPresent(DataFormats.FileDrop) &&
            e.Data.GetData(DataFormats.FileDrop) is string[] files &&
            DataContext is FileShredderViewModel vm)
        {
            // Flash: coral → white → coral → transparent (confirmation pulse)
            var flash = new ColorAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(400) };
            flash.KeyFrames.Add(new LinearColorKeyFrame(Colors.White, KeyTime.FromPercent(0.3)));
            flash.KeyFrames.Add(new LinearColorKeyFrame(DropHighlightColor, KeyTime.FromPercent(0.6)));
            flash.KeyFrames.Add(new LinearColorKeyFrame(Colors.Transparent, KeyTime.FromPercent(1.0)));
            DropZoneBorder.BorderBrush = new SolidColorBrush(DropHighlightColor);
            DropZoneBorder.BorderBrush.BeginAnimation(SolidColorBrush.ColorProperty, flash);

            vm.AddDroppedFiles(files);
        }
        else
        {
            // Non-file drop — reset border
            var anim = new ColorAnimation(Colors.Transparent, TimeSpan.FromMilliseconds(200));
            DropZoneBorder.BorderBrush?.BeginAnimation(SolidColorBrush.ColorProperty, anim);
        }
    }
}
