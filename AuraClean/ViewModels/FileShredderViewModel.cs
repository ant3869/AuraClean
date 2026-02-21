using AuraClean.Helpers;
using AuraClean.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.IO;

namespace AuraClean.ViewModels;

/// <summary>
/// ViewModel for the File Shredder feature.
/// Manages file selection, algorithm choice, and secure deletion operations.
/// </summary>
public partial class FileShredderViewModel : ObservableObject
{
    [ObservableProperty] private ObservableCollection<ShredFileItem> _files = [];
    [ObservableProperty] private FileShredderService.ShredAlgorithm _selectedAlgorithm = FileShredderService.ShredAlgorithm.DoD3Pass;
    [ObservableProperty] private AlgorithmOption? _selectedAlgorithmOption;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusMessage = "Add files to securely shred them.";
    [ObservableProperty] private int _progressCurrent;
    [ObservableProperty] private int _progressTotal;
    [ObservableProperty] private string _progressText = string.Empty;
    [ObservableProperty] private bool _hasResults;
    [ObservableProperty] private int _lastShredded;
    [ObservableProperty] private int _lastFailed;
    [ObservableProperty] private long _lastBytesOverwritten;
    [ObservableProperty] private string _algorithmDescription = string.Empty;

    public string FormattedBytesOverwritten => FormatHelper.FormatBytes(LastBytesOverwritten);

    public ObservableCollection<AlgorithmOption> Algorithms { get; } =
    [
        new(FileShredderService.ShredAlgorithm.QuickZero, "Quick Zero", "1 pass — zeros", 1),
        new(FileShredderService.ShredAlgorithm.Random, "Random", "1 pass — crypto random", 1),
        new(FileShredderService.ShredAlgorithm.DoD3Pass, "DoD 5220.22-M", "3 passes — defense standard", 3),
        new(FileShredderService.ShredAlgorithm.Enhanced7Pass, "Enhanced", "7 passes — maximum security", 7),
    ];

    public FileShredderViewModel()
    {
        SelectedAlgorithmOption = Algorithms[2]; // DoD3Pass default
        UpdateAlgorithmDescription();
    }

    partial void OnSelectedAlgorithmChanged(FileShredderService.ShredAlgorithm value) =>
        UpdateAlgorithmDescription();

    partial void OnSelectedAlgorithmOptionChanged(AlgorithmOption? value)
    {
        if (value != null)
            SelectedAlgorithm = value.Algorithm;
    }

    private void UpdateAlgorithmDescription()
    {
        AlgorithmDescription = FileShredderService.GetAlgorithmDescription(SelectedAlgorithm);
    }

    [RelayCommand]
    private void AddFiles()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Multiselect = true,
            Title = "Select files to shred",
            Filter = "All Files (*.*)|*.*"
        };

        if (dialog.ShowDialog() == true)
        {
            foreach (var path in dialog.FileNames)
            {
                if (Files.Any(f => f.FullPath.Equals(path, StringComparison.OrdinalIgnoreCase)))
                    continue;

                try
                {
                    var info = new FileInfo(path);
                    Files.Add(new ShredFileItem
                    {
                        FullPath = path,
                        FileName = info.Name,
                        SizeBytes = info.Length,
                        FormattedSize = FormatHelper.FormatBytes(info.Length),
                    });
                }
                catch (Exception ex)
                {
                    DiagnosticLogger.Warn("FileShredder", $"Could not add file: {path}", ex);
                }
            }

            StatusMessage = $"{Files.Count} file(s) ready to shred.";
        }
    }

    [RelayCommand]
    private void AddFolder()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select a folder (select any file inside it)",
            Filter = "All Files (*.*)|*.*",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog() == true)
        {
            var dir = Path.GetDirectoryName(dialog.FileName);
            if (dir == null) return;

            try
            {
                var folderFiles = Directory.GetFiles(dir, "*", SearchOption.TopDirectoryOnly);
                int added = 0;

                foreach (var path in folderFiles)
                {
                    if (Files.Any(f => f.FullPath.Equals(path, StringComparison.OrdinalIgnoreCase)))
                        continue;

                    try
                    {
                        var info = new FileInfo(path);
                        Files.Add(new ShredFileItem
                        {
                            FullPath = path,
                            FileName = info.Name,
                            SizeBytes = info.Length,
                            FormattedSize = FormatHelper.FormatBytes(info.Length),
                        });
                        added++;
                    }
                    catch { }
                }

                StatusMessage = $"Added {added} files from {Path.GetFileName(dir)}. Total: {Files.Count} file(s).";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error reading folder: {ex.Message}";
            }
        }
    }

    [RelayCommand]
    private void RemoveFile(ShredFileItem? item)
    {
        if (item == null) return;
        Files.Remove(item);
        StatusMessage = $"{Files.Count} file(s) in queue.";
    }

    [RelayCommand]
    private void ClearAll()
    {
        Files.Clear();
        HasResults = false;
        StatusMessage = "Add files to securely shred them.";
    }

    [RelayCommand]
    private async Task ShredAllAsync()
    {
        if (Files.Count == 0)
        {
            StatusMessage = "No files to shred. Add files first.";
            return;
        }

        IsBusy = true;
        HasResults = false;
        ProgressTotal = Files.Count;
        ProgressCurrent = 0;

        int passes = FileShredderService.GetPassCount(SelectedAlgorithm);
        StatusMessage = $"Shredding {Files.Count} files with {passes}-pass overwrite...";

        try
        {
            var paths = Files.Select(f => f.FullPath).ToList();

            var progress = new Progress<(int current, int total, string fileName)>(p =>
            {
                ProgressCurrent = p.current;
                ProgressText = $"[{p.current}/{p.total}] Shredding {p.fileName}...";
            });

            var result = await FileShredderService.ShredFilesAsync(
                paths, SelectedAlgorithm, progress);

            LastShredded = result.FilesShredded;
            LastFailed = result.FilesFailed;
            LastBytesOverwritten = result.TotalBytesOverwritten;
            HasResults = true;

            OnPropertyChanged(nameof(FormattedBytesOverwritten));

            // Remove successfully shredded files from the list
            var remaining = Files.Where(f => File.Exists(f.FullPath)).ToList();
            Files = new ObservableCollection<ShredFileItem>(remaining);

            StatusMessage = result.FilesFailed == 0
                ? $"Successfully shredded {result.FilesShredded} files ({FormatHelper.FormatBytes(result.TotalBytesOverwritten)} overwritten)."
                : $"Shredded {result.FilesShredded} files, {result.FilesFailed} failed. {FormatHelper.FormatBytes(result.TotalBytesOverwritten)} overwritten.";
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Shredding cancelled.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            ProgressText = string.Empty;
        }
    }

    /// <summary>
    /// Represents a file queued for shredding.
    /// </summary>
    public class ShredFileItem
    {
        public string FullPath { get; init; } = string.Empty;
        public string FileName { get; init; } = string.Empty;
        public long SizeBytes { get; init; }
        public string FormattedSize { get; init; } = string.Empty;
    }

    /// <summary>
    /// Represents a shredding algorithm choice in the UI.
    /// </summary>
    public record AlgorithmOption(
        FileShredderService.ShredAlgorithm Algorithm,
        string Name,
        string Description,
        int Passes);
}
