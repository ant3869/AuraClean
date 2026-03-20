using AuraClean.Helpers;
using AuraClean.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;

namespace AuraClean.ViewModels;

/// <summary>
/// ViewModel for the System Information page.
/// Displays detailed hardware/software info with weighted performance scores.
/// </summary>
public partial class SystemInfoViewModel : ObservableObject
{
    [ObservableProperty] private ObservableCollection<SystemInfoService.InfoEntry> _entries = [];
    [ObservableProperty] private ObservableCollection<SystemInfoService.InfoEntry> _filteredEntries = [];
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusMessage = "Loading system information...";
    [ObservableProperty] private string _filterCategory = "All";
    [ObservableProperty] private string _searchText = string.Empty;

    // ── Scoring properties ──
    [ObservableProperty] private int _overallScore;
    [ObservableProperty] private string _overallGrade = "—";
    [ObservableProperty] private string _overallGradeLabel = "";
    [ObservableProperty] private string _overallColor = "#7C5CFC";
    [ObservableProperty] private ObservableCollection<HardwareScoreService.CategoryScore> _categoryScores = [];

    // ── Individual category scores for binding ──
    [ObservableProperty] private int _cpuScore;
    [ObservableProperty] private string _cpuGrade = "—";
    [ObservableProperty] private string _cpuSummary = "";
    [ObservableProperty] private string _cpuColor = "#7C5CFC";

    [ObservableProperty] private int _memoryScore;
    [ObservableProperty] private string _memoryGrade = "—";
    [ObservableProperty] private string _memorySummary = "";
    [ObservableProperty] private string _memoryColor = "#7C5CFC";

    [ObservableProperty] private int _gpuScore;
    [ObservableProperty] private string _gpuGrade = "—";
    [ObservableProperty] private string _gpuSummary = "";
    [ObservableProperty] private string _gpuColor = "#7C5CFC";

    [ObservableProperty] private int _storageScore;
    [ObservableProperty] private string _storageGrade = "—";
    [ObservableProperty] private string _storageSummary = "";
    [ObservableProperty] private string _storageColor = "#7C5CFC";

    [ObservableProperty] private int _systemScore;
    [ObservableProperty] private string _systemGrade = "—";
    [ObservableProperty] private string _systemSummary = "";
    [ObservableProperty] private string _systemColor = "#7C5CFC";

    // ── Key hardware summary lines ──
    [ObservableProperty] private string _cpuName = "";
    [ObservableProperty] private string _gpuName = "";
    [ObservableProperty] private string _ramSummary = "";
    [ObservableProperty] private string _osSummary = "";

    public ObservableCollection<string> Categories { get; } =
    [
        "All", "Operating System", "Processor", "Memory",
        "Graphics", "Storage", "Network", "Motherboard", "Runtime"
    ];

    public SystemInfoViewModel()
    {
        _ = LoadInfoAsync().ContinueWith(t =>
        {
            if (t.Exception != null)
                DiagnosticLogger.Warn("SystemInfoViewModel", "LoadInfoAsync failed", t.Exception.InnerException ?? t.Exception);
        }, TaskContinuationOptions.OnlyOnFaulted);
    }

    partial void OnFilterCategoryChanged(string value) => ApplyFilter();
    partial void OnSearchTextChanged(string value) => ApplyFilter();

    [RelayCommand]
    private void FilterByCategory(string category)
    {
        FilterCategory = category;
    }

    private void ApplyFilter()
    {
        var filtered = Entries.AsEnumerable();

        if (FilterCategory != "All")
            filtered = filtered.Where(e => e.Category == FilterCategory);

        if (!string.IsNullOrWhiteSpace(SearchText))
            filtered = filtered.Where(e =>
                e.Label.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
                e.Value.Contains(SearchText, StringComparison.OrdinalIgnoreCase));

        FilteredEntries = new ObservableCollection<SystemInfoService.InfoEntry>(filtered);
    }

    [RelayCommand]
    private async Task LoadInfoAsync()
    {
        IsBusy = true;
        StatusMessage = "Collecting system information...";

        try
        {
            var progress = new Progress<string>(msg => StatusMessage = msg);
            var entries = await SystemInfoService.CollectAllAsync(progress);

            Entries = new ObservableCollection<SystemInfoService.InfoEntry>(entries);
            ApplyFilter();

            // Compute scores
            StatusMessage = "Computing performance scores...";
            var result = HardwareScoreService.ComputeScores(entries);

            OverallScore = result.OverallScore;
            OverallGrade = result.OverallGrade;
            OverallGradeLabel = HardwareScoreService.GradeLabel(result.OverallScore);
            OverallColor = HardwareScoreService.GradeColor(result.OverallScore);
            CategoryScores = new ObservableCollection<HardwareScoreService.CategoryScore>(result.Categories);

            // Map individual category scores
            foreach (var cat in result.Categories)
            {
                switch (cat.Category)
                {
                    case "Processor":
                        CpuScore = cat.Score; CpuGrade = cat.Grade; CpuSummary = cat.Summary; CpuColor = cat.Color;
                        break;
                    case "Memory":
                        MemoryScore = cat.Score; MemoryGrade = cat.Grade; MemorySummary = cat.Summary; MemoryColor = cat.Color;
                        break;
                    case "Graphics":
                        GpuScore = cat.Score; GpuGrade = cat.Grade; GpuSummary = cat.Summary; GpuColor = cat.Color;
                        break;
                    case "Storage":
                        StorageScore = cat.Score; StorageGrade = cat.Grade; StorageSummary = cat.Summary; StorageColor = cat.Color;
                        break;
                    case "System":
                        SystemScore = cat.Score; SystemGrade = cat.Grade; SystemSummary = cat.Summary; SystemColor = cat.Color;
                        break;
                }
            }

            // Extract key summary lines
            CpuName = entries.FirstOrDefault(e => e.Category == "Processor" && e.Label.Contains("Name"))?.Value?.Trim() ?? "";
            GpuName = entries.FirstOrDefault(e => e.Category == "Graphics" && e.Label.Contains("Name"))?.Value?.Trim() ?? "";
            RamSummary = MemorySummary;
            OsSummary = entries.FirstOrDefault(e => e.Category == "Operating System" && e.Label == "Name")?.Value?.Trim() ?? "";

            StatusMessage = $"Loaded {entries.Count} properties — Overall Score: {result.OverallScore}/100 ({result.OverallGrade})";
        }
        catch (Exception ex)
        {
            StatusMessage = "Couldn't load system information. Please try again.";
            DiagnosticLogger.Error("SystemInfoVM", "LoadInfoAsync failed", ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void CopyToClipboard()
    {
        try
        {
            var text = SystemInfoService.FormatAsText(new(Entries));
            System.Windows.Clipboard.SetText(text);
            StatusMessage = "System information copied to clipboard.";
        }
        catch (Exception ex)
        {
            StatusMessage = "Couldn't copy to clipboard. Please try again.";
            DiagnosticLogger.Error("SystemInfoVM", "Copy to clipboard failed", ex);
        }
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        Entries.Clear();
        FilteredEntries.Clear();
        await LoadInfoAsync();
    }
}
