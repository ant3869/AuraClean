using System.Collections.ObjectModel;
using System.IO;
using AuraClean.Helpers;
using AuraClean.Models;
using AuraClean.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Windows;
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

        if (!IsAdvancedMode && SelectedScanMode != ScanMode.Quick)
            SelectedScanMode = ScanMode.Quick;

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
                        StatusMessage = "Please select a valid folder to scan.";
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
            StatusMessage = "Something went wrong during the scan. Please try again.";
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
        if (!IsAdvancedMode && mode != "Quick")
        {
            StatusMessage = "Normal mode uses Quick Scan. Turn on Advanced mode for full, custom, and browser-only scans.";
            SelectedScanMode = ScanMode.Quick;
            return;
        }

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
            Title = "Select Folder to Scan"
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
        if (IsQuarantining || IsScanning) return;

        var selectedThreats = Categories
            .SelectMany(c => c.Items)
            .Where(t => t.IsSelected && !t.IsWhitelisted && !t.IsQuarantined)
            .ToList();

        if (selectedThreats.Count == 0)
        {
            ActionStatusMessage = "No threats selected for quarantine.";
            return;
        }

        if (SafetyPromptService.IsDryRunEnabled())
        {
            ActionStatusMessage = $"Dry run: would quarantine {selectedThreats.Count} item(s).";
            return;
        }

        if (!SafetyPromptService.ConfirmDestructiveAction(
                $"Quarantine {selectedThreats.Count} selected item(s)?\n\n" +
                "Running threat processes are closed, files are moved to quarantine (restorable), " +
                "and startup entries or scheduled tasks are disabled.",
                "Confirm quarantine"))
        {
            ActionStatusMessage = "Quarantine cancelled.";
            return;
        }

        IsQuarantining = true;
        try
        {
            var progress = new Progress<string>(msg => ActionStatusMessage = msg);
            var result = await ThreatScannerService.QuarantineThreatsAsync(selectedThreats, progress);

            RemoveHandledItems();
            ActionStatusMessage = DescribeOutcome("Quarantined", result);

            if (result.Handled > 0)
            {
                WeakReferenceMessenger.Default.Send(QuarantineChangedMessage.Instance);
                CleanupHistoryService.Record(CleanupOperationType.ThreatQuarantine, result.Handled,
                    selectedThreats.Where(t => t.IsQuarantined).Sum(t => t.SizeBytes),
                    $"Quarantined {result.Handled} threat(s)");
            }

            foreach (var message in result.Messages.Take(20))
                DiagnosticLogger.Warn("ThreatScannerVM", message);
        }
        catch (Exception ex)
        {
            ActionStatusMessage = "Couldn't quarantine the selected items. Some files may be in use.";
            DiagnosticLogger.Error("ThreatScannerVM", "Quarantine failed", ex);
        }
        finally
        {
            IsQuarantining = false;
        }
    }

    [RelayCommand]
    private async Task QuarantineAllAsync()
    {
        foreach (var cat in Categories)
            cat.IsAllSelected = true;

        await QuarantineSelectedAsync();
    }

    [RelayCommand]
    private async Task DeleteSelectedAsync()
    {
        if (IsQuarantining || IsScanning) return;
        if (!IsAdvancedMode)
        {
            ActionStatusMessage = "Normal mode uses quarantine instead of permanent deletion. Turn on Advanced mode to delete threats.";
            return;
        }

        var selectedThreats = Categories
            .SelectMany(c => c.Items)
            .Where(t => t.IsSelected && !t.IsWhitelisted && !t.IsQuarantined)
            .ToList();

        if (selectedThreats.Count == 0)
        {
            ActionStatusMessage = "No threats selected for deletion.";
            return;
        }

        if (SafetyPromptService.IsDryRunEnabled())
        {
            ActionStatusMessage = $"Dry run: would permanently delete {selectedThreats.Count} threat(s).";
            return;
        }

        if (!SafetyPromptService.ConfirmDestructiveAction(
                $"Permanently delete {selectedThreats.Count} selected item(s)? This cannot be undone — quarantine is safer."))
        {
            ActionStatusMessage = "Threat deletion cancelled.";
            return;
        }

        IsQuarantining = true;
        try
        {
            var progress = new Progress<string>(msg => ActionStatusMessage = msg);
            var result = await ThreatScannerService.DeleteThreatsAsync(selectedThreats, progress);

            RemoveHandledItems();
            ActionStatusMessage = DescribeOutcome("Removed", result);

            CleanupHistoryService.Record(CleanupOperationType.ThreatDelete, result.Handled,
                selectedThreats.Where(t => t.IsQuarantined).Sum(t => t.SizeBytes),
                $"Permanently removed {result.Handled} threat(s)");

            foreach (var message in result.Messages.Take(20))
                DiagnosticLogger.Warn("ThreatScannerVM", message);
        }
        catch (Exception ex)
        {
            ActionStatusMessage = "Couldn't delete the selected items. Some files may be in use.";
            DiagnosticLogger.Error("ThreatScannerVM", "Delete failed", ex);
        }
        finally
        {
            IsQuarantining = false;
        }
    }

    [RelayCommand]
    private async Task DeleteAllAsync()
    {
        if (!IsAdvancedMode)
        {
            ActionStatusMessage = "Normal mode uses quarantine instead of permanent deletion. Turn on Advanced mode to delete threats.";
            return;
        }

        foreach (var cat in Categories)
            cat.IsAllSelected = true;

        await DeleteSelectedAsync();
    }

    private void RemoveHandledItems()
    {
        foreach (var cat in Categories.ToList())
        {
            foreach (var item in cat.Items.Where(t => t.IsQuarantined).ToList())
                cat.Items.Remove(item);

            if (cat.Items.Count == 0)
                Categories.Remove(cat);
        }

        UpdateThreatCounts();
        IsClean = TotalThreats == 0;
    }

    private string DescribeOutcome(string verb, ThreatScannerService.ThreatActionResult result)
    {
        var text = $"{verb} {result.Handled} item(s).";
        if (result.Failed > 0)
            text += $" {result.Failed} failed.";
        if (result.ManualActionRequired > 0)
            text += $" {result.ManualActionRequired} need manual action (see each item's note).";
        if (TotalThreats == 0)
            text += " System is clean!";
        return text;
    }

    [RelayCommand]
    private void WhitelistSelected()
    {
        if (!IsAdvancedMode)
        {
            ActionStatusMessage = "Turn on Advanced mode to whitelist detected items.";
            return;
        }

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
            StatusMessage = "No threats detected.";
            TotalThreats = 0;
            CriticalCount = HighCount = MediumCount = LowCount = 0;
            return;
        }

        // Group threats by category — marshal to UI thread for ObservableCollection safety
        var grouped = result.Threats
            .GroupBy(t => t.CategoryDisplay)
            .OrderByDescending(g => g.Max(t => (int)t.ThreatLevel))
            .ThenByDescending(g => g.Count())
            .ToList();

        void ApplyToCollection()
        {
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
        }

        if (Application.Current?.Dispatcher.CheckAccess() == true)
            ApplyToCollection();
        else
            Application.Current?.Dispatcher.Invoke(ApplyToCollection);

        UpdateThreatCounts();

        StatusMessage = $"Scan complete: {TotalThreats} threat(s) found" +
            (CriticalCount > 0 ? $" ({CriticalCount} critical!)" : "") +
            $" in {ScanDuration}";

        // Tray notification for threats
        if (CriticalCount > 0)
            NotificationService.ShowWarning("Threats Detected",
                $"{TotalThreats} threat(s) found — {CriticalCount} critical!");
        else
            NotificationService.ShowSuccess("Scan Complete",
                $"{TotalThreats} threat(s) found in {ScanDuration}.");
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
        catch (Exception ex)
        {
            DiagnosticLogger.Warn("ThreatScannerVM", "Failed to load last scan date", ex);
        }
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
        catch (Exception ex)
        {
            DiagnosticLogger.Warn("ThreatScannerVM", "Failed to save last scan date", ex);
        }
    }
}

// ══════════════════════════════════════════
//  Supporting View Models
// ══════════════════════════════════════════

public partial class ThreatCategory : ObservableObject
{
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private bool _isExpanded = true;
    [ObservableProperty] private bool _isAllSelected;

    private bool _syncingSelection;
    private ObservableCollection<ThreatItem> _items = [];

    public ObservableCollection<ThreatItem> Items
    {
        get => _items;
        set
        {
            _items.CollectionChanged -= OnItemsChanged;
            foreach (var item in _items)
                item.PropertyChanged -= OnItemPropertyChanged;

            _items = value ?? [];
            _items.CollectionChanged += OnItemsChanged;
            foreach (var item in _items)
                item.PropertyChanged += OnItemPropertyChanged;

            OnPropertyChanged();
            RaiseCountsChanged();
            SyncSelectionState();
        }
    }

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
        if (_syncingSelection)
            return;

        foreach (var item in Items)
            item.IsSelected = value;
    }

    private void SyncSelectionState()
    {
        _syncingSelection = true;
        try { IsAllSelected = Items.Count > 0 && Items.All(i => i.IsSelected); }
        finally { _syncingSelection = false; }
    }

    private void OnItemsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems != null)
            foreach (ThreatItem item in e.OldItems)
                item.PropertyChanged -= OnItemPropertyChanged;
        if (e.NewItems != null)
            foreach (ThreatItem item in e.NewItems)
                item.PropertyChanged += OnItemPropertyChanged;

        RaiseCountsChanged();
        SyncSelectionState();
    }

    private void OnItemPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ThreatItem.IsSelected))
            SyncSelectionState();
    }

    private void RaiseCountsChanged()
    {
        OnPropertyChanged(nameof(ItemCount));
        OnPropertyChanged(nameof(CriticalCount));
        OnPropertyChanged(nameof(HighCount));
        OnPropertyChanged(nameof(SeveritySummary));
    }
}

public class WhitelistDisplayItem
{
    public string Hash { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public string AddedAt { get; set; } = string.Empty;
}
