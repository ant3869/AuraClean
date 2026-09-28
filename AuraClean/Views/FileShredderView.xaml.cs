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
    private static readonly Color FallbackHighlightColor = Color.FromRgb(0xF8, 0x51, 0x49);
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
        AnimateDropBorder(new ColorAnimation(HighlightColor, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        });
    }

    private void OnDragLeaveZone(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
        AnimateDropBorder(new ColorAnimation(Colors.Transparent, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
        });
    }

    /// <summary>Destructive-action tone from the active theme (red), for the drop highlight.</summary>
    private Color HighlightColor => TryFindResource("AuraErr") is SolidColorBrush brush ? brush.Color : FallbackHighlightColor;

    /// <summary>
    /// Animates the drop-zone border on a brush owned by this element. The border starts with a
    /// frozen system brush (and must never animate a shared theme brush), so each animation
    /// starts from a fresh brush at the current color.
    /// </summary>
    private void AnimateDropBorder(AnimationTimeline animation)
    {
        var from = DropZoneBorder.BorderBrush is SolidColorBrush current ? current.Color : Colors.Transparent;
        var brush = new SolidColorBrush(from);
        DropZoneBorder.BorderBrush = brush;
        brush.BeginAnimation(SolidColorBrush.ColorProperty, animation);
    }

    private void OnFileDrop(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;

        if (e.Data.GetDataPresent(DataFormats.FileDrop) &&
            e.Data.GetData(DataFormats.FileDrop) is string[] files &&
            DataContext is FileShredderViewModel vm)
        {
            // Confirmation pulse: highlight → foreground → highlight → transparent.
            var highlight = HighlightColor;
            var foreground = TryFindResource("AuraTextBright") is SolidColorBrush fg ? fg.Color : Colors.White;
            var flash = new ColorAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(400) };
            flash.KeyFrames.Add(new LinearColorKeyFrame(foreground, KeyTime.FromPercent(0.3)));
            flash.KeyFrames.Add(new LinearColorKeyFrame(highlight, KeyTime.FromPercent(0.6)));
            flash.KeyFrames.Add(new LinearColorKeyFrame(Colors.Transparent, KeyTime.FromPercent(1.0)));
            AnimateDropBorder(flash);

            vm.AddDroppedFiles(files);
        }
        else
        {
            // Non-file drop — reset border
            AnimateDropBorder(new ColorAnimation(Colors.Transparent, TimeSpan.FromMilliseconds(180)));
        }
    }
}
