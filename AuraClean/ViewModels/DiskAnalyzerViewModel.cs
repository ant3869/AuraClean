using AuraClean.Helpers;
using AuraClean.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.IO;

namespace AuraClean.ViewModels;

/// <summary>
/// ViewModel for the Visual Disk Analyzer ("Storage Map") view.
/// Manages drive selection, directory crawling, and treemap data generation.
/// </summary>
public partial class DiskAnalyzerViewModel : ObservableObject
{
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusMessage = "Select a drive or directory to analyze.";
    [ObservableProperty] private bool _hasResults;
    [ObservableProperty] private double _progressPercent;
    [ObservableProperty] private string _selectedPath = string.Empty;

    // Drive list
    [ObservableProperty]
    private ObservableCollection<DriveEntry> _drives = [];

    [ObservableProperty] private DriveEntry? _selectedDrive;

    // Analysis results
    [ObservableProperty] private DiskAnalyzerService.AnalysisResult? _analysisResult;
    [ObservableProperty] private ObservableCollection<TreemapNode> _treemapData = [];
    [ObservableProperty] private ObservableCollection<LargeFileEntry> _largestFiles = [];
    [ObservableProperty] private ObservableCollection<LargeFileEntry> _largestDirs = [];

    // Navigation
    [ObservableProperty] private string _currentPath = string.Empty;
    [ObservableProperty] private ObservableCollection<BreadcrumbItem> _breadcrumbs = [];

    // Summary
    [ObservableProperty] private long _totalSizeBytes;
    [ObservableProperty] private int _totalFileCount;
    [ObservableProperty] private int _totalDirCount;
    [ObservableProperty] private string _scanDuration = string.Empty;

    public string FormattedTotalSize => FormatHelper.FormatBytes(TotalSizeBytes);

    private CancellationTokenSource? _scanCts;

    public DiskAnalyzerViewModel()
    {
        LoadDrives();
    }

    private void LoadDrives()
    {
        var driveStats = DiskAnalyzerService.GetDriveStats();
        Drives = new ObservableCollection<DriveEntry>(
            driveStats.Select(d => new DriveEntry
            {
                Name = d.Name,
                Label = string.IsNullOrEmpty(d.Label) ? "Local Disk" : d.Label,
                TotalBytes = d.TotalBytes,
                UsedBytes = d.UsedBytes,
                FreeBytes = d.FreeBytes,
                UsagePercent = d.UsagePercent
            }));

        if (Drives.Count > 0)
            SelectedDrive = Drives[0];
    }

    partial void OnSelectedDriveChanged(DriveEntry? value)
    {
        if (value != null)
            SelectedPath = value.Name;
    }

    [RelayCommand]
    private async Task AnalyzeAsync()
    {
        var pathToAnalyze = SelectedPath;
        if (string.IsNullOrWhiteSpace(pathToAnalyze) || !Directory.Exists(pathToAnalyze))
        {
            StatusMessage = "Please select a valid directory to analyze.";
            return;
        }

        _scanCts?.Cancel();
        _scanCts?.Dispose();
        _scanCts = new CancellationTokenSource();

        IsBusy = true;
        HasResults = false;
        StatusMessage = $"Analyzing {pathToAnalyze}...";
        ProgressPercent = 0;

        try
        {
            var progress = new Progress<string>(msg => StatusMessage = msg);

            var result = await DiskAnalyzerService.AnalyzeDirectoryAsync(
                pathToAnalyze, maxDepth: 4,
                progress: progress,
                ct: _scanCts.Token);

            AnalysisResult = result;
            TotalSizeBytes = result.TotalSizeBytes;
            TotalFileCount = result.TotalFiles;
            TotalDirCount = result.TotalDirectories;
            ScanDuration = $"{result.ScanDuration.TotalSeconds:F1}s";

            OnPropertyChanged(nameof(FormattedTotalSize));

            // Build treemap data from root children
            BuildTreemapData(result.Root);

            // Largest files
            LargestFiles = new ObservableCollection<LargeFileEntry>(
                result.LargestFiles.Select(f => new LargeFileEntry
                {
                    Name = f.Name,
                    FullPath = f.FullPath,
                    SizeBytes = f.SizeBytes,
                    LastModified = f.LastModified,
                    IsDirectory = false
                }));

            // Largest directories
            LargestDirs = new ObservableCollection<LargeFileEntry>(
                result.LargestDirectories.Select(d => new LargeFileEntry
                {
                    Name = d.Name,
                    FullPath = d.FullPath,
                    SizeBytes = d.SizeBytes,
                    LastModified = d.LastModified,
                    IsDirectory = true,
                    FileCount = d.FileCount
                }));

            CurrentPath = pathToAnalyze;
            UpdateBreadcrumbs(pathToAnalyze);

            HasResults = true;
            ProgressPercent = 100;
            StatusMessage = $"Analysis complete: {result.TotalFiles:N0} files, " +
                          $"{result.TotalDirectories:N0} directories, " +
                          $"{FormatHelper.FormatBytes(result.TotalSizeBytes)} total ({ScanDuration}).";
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Analysis cancelled.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void CancelAnalysis()
    {
        _scanCts?.Cancel();
    }

    [RelayCommand]
    private void NavigateToNode(TreemapNode? node)
    {
        if (node == null || AnalysisResult == null) return;

        // Find the DiskNode matching this path and rebuild treemap from there
        var diskNode = FindNode(AnalysisResult.Root, node.FullPath);
        if (diskNode != null && diskNode.IsDirectory && diskNode.Children.Count > 0)
        {
            BuildTreemapData(diskNode);
            CurrentPath = node.FullPath;
            UpdateBreadcrumbs(node.FullPath);
        }
    }

    [RelayCommand]
    private void NavigateUp()
    {
        if (AnalysisResult == null || CurrentPath == AnalysisResult.Root.FullPath) return;

        var parent = Path.GetDirectoryName(CurrentPath);
        if (parent != null)
        {
            var diskNode = FindNode(AnalysisResult.Root, parent);
            if (diskNode != null)
            {
                BuildTreemapData(diskNode);
                CurrentPath = parent;
                UpdateBreadcrumbs(parent);
            }
        }
    }

    [RelayCommand]
    private void NavigateToBreadcrumb(BreadcrumbItem? item)
    {
        if (item == null || AnalysisResult == null) return;

        var diskNode = FindNode(AnalysisResult.Root, item.FullPath);
        if (diskNode != null)
        {
            BuildTreemapData(diskNode);
            CurrentPath = item.FullPath;
            UpdateBreadcrumbs(item.FullPath);
        }
    }

    [RelayCommand]
    private void OpenInExplorer(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            System.Diagnostics.Process.Start("explorer.exe",
                Directory.Exists(path) ? path : $"/select,\"{path}\"");
        }
        catch (Exception ex)
        {
            DiagnosticLogger.Warn("DiskAnalyzerVM", "Failed to open in Explorer", ex);
        }
    }

    private void BuildTreemapData(DiskAnalyzerService.DiskNode node)
    {
        var items = new ObservableCollection<TreemapNode>();

        foreach (var child in node.Children.Where(c => c.SizeBytes > 0).Take(50))
        {
            items.Add(new TreemapNode
            {
                Name = child.Name,
                FullPath = child.FullPath,
                SizeBytes = child.SizeBytes,
                SizePercent = node.SizeBytes > 0 ? (double)child.SizeBytes / node.SizeBytes * 100.0 : 0,
                IsDirectory = child.IsDirectory,
                FileCount = child.FileCount,
                ChildCount = child.Children.Count,
                ColorIndex = items.Count % 10
            });
        }

        TreemapData = items;
    }

    private static DiskAnalyzerService.DiskNode? FindNode(
        DiskAnalyzerService.DiskNode root, string path)
    {
        if (root.FullPath.Equals(path, StringComparison.OrdinalIgnoreCase))
            return root;

        foreach (var child in root.Children.Where(c => c.IsDirectory))
        {
            if (path.StartsWith(child.FullPath, StringComparison.OrdinalIgnoreCase))
            {
                var found = FindNode(child, path);
                if (found != null) return found;
            }
        }

        return null;
    }

    private void UpdateBreadcrumbs(string path)
    {
        var parts = new List<BreadcrumbItem>();
        var current = path;

        while (!string.IsNullOrEmpty(current))
        {
            parts.Insert(0, new BreadcrumbItem
            {
                Name = Path.GetFileName(current),
                FullPath = current
            });

            var parent = Path.GetDirectoryName(current);
            if (parent == current) break;
            current = parent ?? string.Empty;

            // Stop at root of analysis
            if (AnalysisResult != null &&
                current.Length < AnalysisResult.Root.FullPath.Length)
                break;
        }

        if (parts.Count > 0 && string.IsNullOrEmpty(parts[0].Name))
            parts[0].Name = parts[0].FullPath;

        Breadcrumbs = new ObservableCollection<BreadcrumbItem>(parts);
    }
}

/// <summary>Represents a disk drive for display.</summary>
public partial class DriveEntry : ObservableObject
{
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _label = string.Empty;
    [ObservableProperty] private long _totalBytes;
    [ObservableProperty] private long _usedBytes;
    [ObservableProperty] private long _freeBytes;
    [ObservableProperty] private double _usagePercent;

    public string FormattedTotal => FormatHelper.FormatBytes(TotalBytes);
    public string FormattedFree => FormatHelper.FormatBytes(FreeBytes);
    public string DisplayLabel => $"{Name} {Label} ({FormattedFree} free)";
}

/// <summary>Treemap node for visualization.</summary>
public partial class TreemapNode : ObservableObject
{
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _fullPath = string.Empty;
    [ObservableProperty] private long _sizeBytes;
    [ObservableProperty] private double _sizePercent;
    [ObservableProperty] private bool _isDirectory;
    [ObservableProperty] private int _fileCount;
    [ObservableProperty] private int _childCount;
    [ObservableProperty] private int _colorIndex;

    public string FormattedSize => SizeBytes switch
    {
        < 1024 => $"{SizeBytes} B",
        < 1_048_576 => $"{SizeBytes / 1024.0:F1} KB",
        < 1_073_741_824 => $"{SizeBytes / 1_048_576.0:F1} MB",
        _ => $"{SizeBytes / 1_073_741_824.0:F2} GB"
    };

    public string PercentLabel => $"{SizePercent:F1}%";
}

/// <summary>Breadcrumb navigation item.</summary>
public partial class BreadcrumbItem : ObservableObject
{
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _fullPath = string.Empty;
}

/// <summary>Large file/directory entry for the details list.</summary>
public partial class LargeFileEntry : ObservableObject
{
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _fullPath = string.Empty;
    [ObservableProperty] private long _sizeBytes;
    [ObservableProperty] private DateTime _lastModified;
    [ObservableProperty] private bool _isDirectory;
    [ObservableProperty] private int _fileCount;

    public string FormattedSize => SizeBytes switch
    {
        < 1024 => $"{SizeBytes} B",
        < 1_048_576 => $"{SizeBytes / 1024.0:F1} KB",
        < 1_073_741_824 => $"{SizeBytes / 1_048_576.0:F1} MB",
        _ => $"{SizeBytes / 1_073_741_824.0:F2} GB"
    };
}
