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

    // Selection
    [ObservableProperty] private int _selectedCount;
    [ObservableProperty] private bool _isAllSelected;
    public bool HasCheckedItems => SelectedCount > 0;

    public string SmartShredLabel
    {
        get
        {
            if (Files.Count == 0) return "ADD FILES TO SHRED";
            var count = SelectedCount > 0 ? SelectedCount : Files.Count;
            return $"SHRED {count} FILE{(count != 1 ? "S" : "")}";
        }
    }

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

    partial void OnIsAllSelectedChanged(bool value)
    {
        foreach (var file in Files)
            file.IsSelected = value;
        UpdateSelectionCount();
    }

    private void HookSelectionEvents()
    {
        foreach (var file in Files)
        {
            file.PropertyChanged -= OnFilePropertyChanged;
            file.PropertyChanged += OnFilePropertyChanged;
        }
    }

    private void OnFilePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShredFileItem.IsSelected))
            UpdateSelectionCount();
    }

    private void UpdateSelectionCount()
    {
        SelectedCount = Files.Count(f => f.IsSelected);
        OnPropertyChanged(nameof(HasCheckedItems));
        OnPropertyChanged(nameof(SmartShredLabel));
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
            HookSelectionEvents();
            UpdateSelectionCount();
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
                    catch (Exception ex)
                    {
                        DiagnosticLogger.Warn("FileShredderVM", $"Failed to read file info: {path}", ex);
                    }
                }
                HookSelectionEvents();
                UpdateSelectionCount();
            }
            catch (Exception ex)
            {
                StatusMessage = "Couldn't read the folder contents. Check permissions and try again.";
                DiagnosticLogger.Error("FileShredderVM", "Error reading folder", ex);
            }
        }
    }

    [RelayCommand]
    private void RemoveFile(ShredFileItem? item)
    {
        if (item == null) return;
        Files.Remove(item);
        UpdateSelectionCount();
        StatusMessage = $"{Files.Count} file(s) in queue.";
    }

    [RelayCommand]
    private void ClearAll()
    {
        Files.Clear();
        HasResults = false;
        StatusMessage = "Add files to securely shred them.";
        UpdateSelectionCount();
    }

    [RelayCommand]
    private async Task ShredAllAsync()
    {
        if (Files.Count == 0)
        {
            StatusMessage = "No files to shred. Add files first.";
            return;
        }

        var filesToShred = Files.Where(f => f.IsSelected).ToList();
        if (filesToShred.Count == 0)
            filesToShred = Files.ToList();

        IsBusy = true;
        HasResults = false;
        ProgressTotal = filesToShred.Count;
        ProgressCurrent = 0;

        int passes = FileShredderService.GetPassCount(SelectedAlgorithm);
        StatusMessage = $"Shredding {filesToShred.Count} files with {passes}-pass overwrite...";

        try
        {
            var paths = filesToShred.Select(f => f.FullPath).ToList();

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
            HookSelectionEvents();
            UpdateSelectionCount();

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
            StatusMessage = "Something went wrong during shredding. Please try again.";
            DiagnosticLogger.Error("FileShredderVM", "ShredAllAsync failed", ex);
        }
        finally
        {
            IsBusy = false;
            ProgressText = string.Empty;
        }
    }

    /// <summary>
    /// Accepts files dropped via drag-and-drop.
    /// Resolves directories to their contained files.
    /// </summary>
    public void AddDroppedFiles(string[] paths)
    {
        int added = 0;
        foreach (var path in paths)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    foreach (var file in Directory.GetFiles(path, "*", SearchOption.TopDirectoryOnly))
                        added += TryAddFile(file) ? 1 : 0;
                }
                else if (File.Exists(path))
                {
                    added += TryAddFile(path) ? 1 : 0;
                }
            }
            catch (Exception ex)
            {
                DiagnosticLogger.Warn("FileShredder", $"Error processing dropped path: {path}", ex);
            }
        }

        StatusMessage = added > 0
            ? $"Added {added} file(s) via drag-and-drop. Total: {Files.Count} file(s)."
            : $"{Files.Count} file(s) in queue (dropped files already in list).";
        HookSelectionEvents();
        UpdateSelectionCount();
    }

    private bool TryAddFile(string path)
    {
        if (Files.Any(f => f.FullPath.Equals(path, StringComparison.OrdinalIgnoreCase)))
            return false;

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
            return true;
        }
        catch (Exception ex)
        {
            DiagnosticLogger.Warn("FileShredderVM", $"Failed to add file: {path}", ex);
            return false;
        }
    }

    public class ShredFileItem : ObservableObject
    {
        public string FullPath { get; init; } = string.Empty;
        public string FileName { get; init; } = string.Empty;
        public long SizeBytes { get; init; }
        public string FormattedSize { get; init; } = string.Empty;

        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set => SetProperty(ref _isSelected, value);
        }
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
