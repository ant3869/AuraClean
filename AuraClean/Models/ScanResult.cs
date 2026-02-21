using AuraClean.Helpers;
using System.Collections.ObjectModel;

namespace AuraClean.Models;

/// <summary>
/// Aggregated scan result containing categorized junk items and summary statistics.
/// </summary>
public class ScanResult
{
    public ObservableCollection<JunkItem> Items { get; } = [];

    public long TotalSizeBytes => Items.Where(i => i.IsSelected).Sum(i => i.SizeBytes);

    public int TotalCount => Items.Count;

    public int SelectedCount => Items.Count(i => i.IsSelected);

    public string FormattedTotalSize => FormatHelper.FormatBytes(TotalSizeBytes);

    /// <summary>Returns items grouped by their category label.</summary>
    public IEnumerable<IGrouping<string, JunkItem>> GroupedByCategory =>
        Items.GroupBy(i => i.Category);

    public void AddRange(IEnumerable<JunkItem> items)
    {
        foreach (var item in items)
            Items.Add(item);
    }

    public void Clear() => Items.Clear();
}
