using System.Collections.ObjectModel;
using System.IO;
using AuraClean.Helpers;
using AuraClean.Models;
using AuraClean.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Win32;

namespace AuraClean.ViewModels;

public partial class ThreatScannerViewModel : ObservableObject
{
    // ── Scan state ──
    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private bool _hasResults;
    [ObservableProperty] private string _statusMessage = "Ready to scan. Select a scan mode to begin.";
    [ObservableProperty] private double _progressValue;
    [ObservableProperty] private string _progressText = string.Empty;
    [ObservableProperty] private ScanMode _selectedScanMode = ScanMode.Quick;

    // ── Results ──
    [ObservableProperty] private ObservableCollection<ThreatCategory> _categories = [];
    [ObservableProperty] private int _totalThreats;
    [ObservableProperty] private int _criticalCount;
    [ObservableProperty] private int _highCount;
    [ObservableProperty] private int _mediumCount;
    [ObservableProperty] private int _lowCount;
    [ObservableProperty] private int _filesScanned;
    [ObservableProperty] private int _processesScanned;
    [ObservableProperty] private string _scanDuration = string.Empty;
    [ObservableProperty] private bool _isClean;
    [ObservableProperty] private string _lastScanDate = "Never";

    // ── Custom scan ──
    [ObservableProperty] private string _customScanPath = string.Empty;

    // ── Action state ──
    [ObservableProperty] private bool _isQuarantining;
    [ObservableProperty] private string _actionStatusMessage = string.Empty;

    // ── Whitelist ──
    [ObservableProperty] private ObservableCollection<WhitelistDisplayItem> _whitelistEntries = [];
    [ObservableProperty] private bool _showWhitelist;

    private CancellationTokenSource? _cts;
    private ThreatScanResult? _lastResult;

    // ── Scan Mode Display ──
    public string[] ScanModeNames { get; } = ["Quick Scan", "Full Scan", "Custom Scan", "Browser Scan"];

    public ThreatScannerViewModel()
    {
        LoadWhitelist();
        LoadLastScanDate();
    }

    // ══════════════════════════════════════════
    //  SCAN COMMANDS
    // ══════════════════════════════════════════

    [RelayCommand]
    private async Task StartScanAsync()
    {
        if (IsScanning) return;

        _cts = new CancellationTokenSource();
        IsScanning = true;
        HasResults = false;
        IsClean = false;
        ProgressValue = 0;
        Categories.Clear();
        ActionStatusMessage = string.Empty;

        var msgProgress = new Progress<string>(msg => StatusMessage = msg);
        var pctProgress = new Progress<double>(pct => ProgressValue = pct);

        try
        {
            ThreatScanResult result;

            switch (SelectedScanMode)
            {
                case ScanMode.Quick:
                    StatusMessage = "Starting Quick Scan...";
                    result = await ThreatScannerService.QuickScanAsync(msgProgress, pctProgress, _cts.Token);
                    break;

                case ScanMode.Full:
                    StatusMessage = "Starting Full System Scan...";
                    result = await ThreatScannerService.FullScanAsync(msgProgress, pctProgress, _cts.Token);
                    break;

                case ScanMode.Custom:
                    if (string.IsNullOrWhiteSpace(CustomScanPath) || !Directory.Exists(CustomScanPath))
                    {
                        StatusMessage = "Please select a valid directory to scan.";
                        IsScanning = false;
                        return;
                    }
                    StatusMessage = $"Scanning: {CustomScanPath}";
                    result = await ThreatScannerService.CustomScanAsync(
                        [CustomScanPath], msgProgress, pctProgress, _cts.Token);
                    break;

                case ScanMode.BrowserOnly:
                    StatusMessage = "Starting Browser Threat Scan...";
                    result = await ThreatScannerService.BrowserScanAsync(msgProgress, pctProgress, _cts.Token);
                    break;

                default:
                    return;
            }

            _lastResult = result;
            ApplyResults(result);
            SaveLastScanDate();
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Scan cancelled.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Scan failed: {ex.Message}";
            DiagnosticLogger.Error("ThreatScannerVM", "Scan failed", ex);
        }
        finally
        {
            IsScanning = false;
            ProgressValue = 100;
            _cts?.Dispose();
            _cts = null;
        }
    }

    [RelayCommand]
    private void CancelScan()
    {
        _cts?.Cancel();
        StatusMessage = "Cancelling scan...";
    }

    [RelayCommand]
    private void SelectScanMode(string mode)
    {
        SelectedScanMode = mode switch
        {
            "Quick" => ScanMode.Quick,
            "Full" => ScanMode.Full,
            "Custom" => ScanMode.Custom,
            "Browser" => ScanMode.BrowserOnly,
            _ => ScanMode.Quick
        };
    }

    [RelayCommand]
    private void BrowseCustomPath()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select Directory to Scan"
        };

        if (dialog.ShowDialog() == true)
        {
            CustomScanPath = dialog.FolderName;
        }
    }

    // ══════════════════════════════════════════
    //  THREAT ACTIONS
    // ══════════════════════════════════════════

    [RelayCommand]
    private async Task QuarantineSelectedAsync()
    {
        if (IsQuarantining) return;
        IsQuarantining = true;

        try
        {
            var selectedThreats = Categories
                .SelectMany(c => c.Items)
                .Where(t => t.IsSelected && !t.IsWhitelisted && !t.IsQuarantined)
                .ToList();

            if (selectedThreats.Count == 0)
            {
                ActionStatusMessage = "No threats selected for quarantine.";
                return;
            }

            var progress = new Progress<string>(msg => ActionStatusMessage = msg);
            var (quarantined, failed, errors) = await ThreatScannerService.QuarantineThreatsAsync(
                selectedThreats, progress, CancellationToken.None);

            // Remove quarantined items from the UI
            foreach (var cat in Categories.ToList())
            {
                var quarantinedItems = cat.Items.Where(t => t.IsQuarantined).ToList();
                foreach (var item in quarantinedItems)
                    cat.Items.Remove(item);

                if (cat.Items.Count == 0)
                    Categories.Remove(cat);
            }

            UpdateThreatCounts();

            ActionStatusMessage = $"Quarantined {quarantined} threat(s)." +
                (failed > 0 ? $" Failed: {failed}." : "") +
                (Categories.Sum(c => c.Items.Count) == 0 ? " System is clean!" : "");

            if (Categories.Sum(c => c.Items.Count) == 0)
                IsClean = true;

            // Notify the Quarantine view to refresh
            if (quarantined > 0)
                WeakReferenceMessenger.Default.Send(QuarantineChangedMessage.Instance);

            // Log to cleanup history
            try
            {
                if (SettingsService.Load().LogCleanupOperations)
                {
                    CleanupHistoryService.LogOperation(new CleanupRecord
                    {
                        OperationType = CleanupOperationType.ThreatQuarantine,
                        ItemCount = quarantined,
                        Details = $"Quarantined {quarantined} threat(s)"
                    });
                }
            }
            catch { }
        }
        catch (Exception ex)
        {
            ActionStatusMessage = $"Quarantine error: {ex.Message}";
            DiagnosticLogger.Error("ThreatScannerVM", "Quarantine failed", ex);
        }
        finally
        {
            IsQuarantining = false;
        }
    }

    [RelayCommand]
    private void QuarantineAll()
    {
        // Select all threats first
        foreach (var cat in Categories)
            foreach (var item in cat.Items)
                item.IsSelected = true;

        _ = QuarantineSelectedAsync();
    }

    [RelayCommand]
    private async Task DeleteSelectedAsync()
    {
        if (IsQuarantining) return;
        IsQuarantining = true;

        try
        {
            var selectedThreats = Categories
                .SelectMany(c => c.Items)
                .Where(t => t.IsSelected && !t.IsWhitelisted && !t.IsQuarantined)
                .ToList();

            if (selectedThreats.Count == 0)
            {
                ActionStatusMessage = "No threats selected for deletion.";
                return;
            }

            var progress = new Progress<string>(msg => ActionStatusMessage = msg);
            var (deleted, failed, errors) = await ThreatScannerService.DeleteThreatsAsync(
                selectedThreats, progress, CancellationToken.None);

            // Remove deleted items from UI
            foreach (var cat in Categories.ToList())
            {
                var deletedItems = cat.Items.Where(t => t.IsQuarantined).ToList();
                foreach (var item in deletedItems)
                    cat.Items.Remove(item);

                if (cat.Items.Count == 0)
                    Categories.Remove(cat);
            }

            UpdateThreatCounts();

            ActionStatusMessage = $"Permanently deleted {deleted} threat(s)." +
                (failed > 0 ? $" Failed: {failed}." : "") +
                (Categories.Sum(c => c.Items.Count) == 0 ? " System is clean!" : "");

            if (Categories.Sum(c => c.Items.Count) == 0)
                IsClean = true;

            // Log to cleanup history
            try
            {
                if (SettingsService.Load().LogCleanupOperations)
                {
                    CleanupHistoryService.LogOperation(new CleanupRecord
                    {
                        OperationType = CleanupOperationType.ThreatQuarantine,
                        ItemCount = deleted,
                        Details = $"Permanently deleted {deleted} threat(s)"
                    });
                }
            }
            catch { }
        }
        catch (Exception ex)
        {
            ActionStatusMessage = $"Delete error: {ex.Message}";
            DiagnosticLogger.Error("ThreatScannerVM", "Delete failed", ex);
        }
        finally
        {
            IsQuarantining = false;
        }
    }

    [RelayCommand]
    private void DeleteAll()
    {
        foreach (var cat in Categories)
            foreach (var item in cat.Items)
                item.IsSelected = true;

        _ = DeleteSelectedAsync();
    }

    [RelayCommand]
    private void WhitelistSelected()
    {
        var selectedThreats = Categories
            .SelectMany(c => c.Items)
            .Where(t => t.IsSelected)
            .ToList();

        if (selectedThreats.Count == 0)
        {
            ActionStatusMessage = "No threats selected to whitelist.";
            return;
        }

        int whitelisted = 0;
        foreach (var threat in selectedThreats)
        {
            ThreatScannerService.WhitelistThreat(threat, "User marked as safe");
            whitelisted++;
        }

        // Remove whitelisted items from UI
        foreach (var cat in Categories.ToList())
        {
            var wlItems = cat.Items.Where(t => t.IsWhitelisted).ToList();
            foreach (var item in wlItems)
                cat.Items.Remove(item);

            if (cat.Items.Count == 0)
                Categories.Remove(cat);
        }

        UpdateThreatCounts();
        LoadWhitelist();

        ActionStatusMessage = $"Whitelisted {whitelisted} item(s).";

        if (Categories.Sum(c => c.Items.Count) == 0)
            IsClean = true;
    }

    [RelayCommand]
    private void RemoveFromWhitelist(WhitelistDisplayItem item)
    {
        ThreatSignatureDatabase.RemoveFromWhitelist(item.Hash);
        WhitelistEntries.Remove(item);
        ActionStatusMessage = $"Removed '{item.FilePath}' from whitelist.";
    }

    [RelayCommand]
    private void ToggleWhitelist()
    {
        ShowWhitelist = !ShowWhitelist;
        if (ShowWhitelist)
            LoadWhitelist();
    }

    [RelayCommand]
    private void SelectAll()
    {
        foreach (var cat in Categories)
            cat.IsAllSelected = true;
    }

    [RelayCommand]
    private void DeselectAll()
    {
        foreach (var cat in Categories)
            cat.IsAllSelected = false;
    }

    // ══════════════════════════════════════════
    //  PRIVATE HELPERS
    // ══════════════════════════════════════════

    private void ApplyResults(ThreatScanResult result)
    {
        HasResults = true;
        IsClean = result.IsClean;
        FilesScanned = result.TotalFilesScanned;
        ProcessesScanned = result.TotalProcessesScanned;
        ScanDuration = FormatHelper.FormatDuration(result.ScanDuration);

        if (result.IsClean)
        {
            StatusMessage = "No threats detected — your system is clean!";
            TotalThreats = 0;
            CriticalCount = HighCount = MediumCount = LowCount = 0;
            return;
        }

        // Group threats by category
        var grouped = result.Threats
            .GroupBy(t => t.CategoryDisplay)
            .OrderByDescending(g => g.Max(t => (int)t.ThreatLevel))
            .ThenByDescending(g => g.Count());

        Categories.Clear();
        foreach (var group in grouped)
        {
            var cat = new ThreatCategory
            {
                Name = group.Key,
                Items = new ObservableCollection<ThreatItem>(
                    group.OrderByDescending(t => t.ThreatLevel))
            };
            Categories.Add(cat);
        }

        UpdateThreatCounts();

        StatusMessage = $"Scan complete: {TotalThreats} threat(s) found" +
            (CriticalCount > 0 ? $" ({CriticalCount} critical!)" : "") +
            $" in {ScanDuration}";
    }

    private void UpdateThreatCounts()
    {
        var allThreats = Categories.SelectMany(c => c.Items).ToList();
        TotalThreats = allThreats.Count;
        CriticalCount = allThreats.Count(t => t.ThreatLevel == ThreatLevel.Critical);
        HighCount = allThreats.Count(t => t.ThreatLevel == ThreatLevel.High);
        MediumCount = allThreats.Count(t => t.ThreatLevel == ThreatLevel.Medium);
        LowCount = allThreats.Count(t => t.ThreatLevel == ThreatLevel.Low);
    }

    private void LoadWhitelist()
    {
        var entries = ThreatSignatureDatabase.GetWhitelistEntries();
        WhitelistEntries = new ObservableCollection<WhitelistDisplayItem>(
            entries.Select(e => new WhitelistDisplayItem
            {
                Hash = e.Hash,
                FilePath = e.FilePath,
                Reason = e.Reason,
                AddedAt = e.AddedAt.ToString("MMM dd, yyyy HH:mm")
            }));
    }

    private void LoadLastScanDate()
    {
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AuraClean", "last_threat_scan.txt");
            if (File.Exists(path))
            {
                LastScanDate = File.ReadAllText(path).Trim();
            }
        }
        catch { }
    }

    private void SaveLastScanDate()
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AuraClean");
            Directory.CreateDirectory(dir);
            var now = DateTime.Now.ToString("MMM dd, yyyy HH:mm");
            File.WriteAllText(Path.Combine(dir, "last_threat_scan.txt"), now);
            LastScanDate = now;
        }
        catch { }
    }
}

// ══════════════════════════════════════════
//  Supporting View Models
// ══════════════════════════════════════════

public partial class ThreatCategory : ObservableObject
{
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private bool _isExpanded = true;
    [ObservableProperty] private bool _isAllSelected = true;

    public ObservableCollection<ThreatItem> Items { get; set; } = [];

    public int ItemCount => Items.Count;
    public int CriticalCount => Items.Count(t => t.ThreatLevel == ThreatLevel.Critical);
    public int HighCount => Items.Count(t => t.ThreatLevel == ThreatLevel.High);

    public string SeveritySummary
    {
        get
        {
            var parts = new List<string>();
            if (CriticalCount > 0) parts.Add($"{CriticalCount} critical");
            if (HighCount > 0) parts.Add($"{HighCount} high");
            var rest = ItemCount - CriticalCount - HighCount;
            if (rest > 0) parts.Add($"{rest} other");
            return string.Join(", ", parts);
        }
    }

    partial void OnIsAllSelectedChanged(bool value)
    {
        foreach (var item in Items)
            item.IsSelected = value;
    }
}

public class WhitelistDisplayItem
{
    public string Hash { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public string AddedAt { get; set; } = string.Empty;
}
