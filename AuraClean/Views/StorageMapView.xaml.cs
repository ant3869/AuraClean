using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace AuraClean.Views;

public partial class StorageMapView : UserControl
{
    public StorageMapView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Converts a size percentage (0–100) to a pixel width for treemap rectangles.
    /// The base container width is ~520px (accounting for sidebar + margins).
    /// </summary>
    public static readonly IValueConverter PercentToWidthConverter = new PercentToWidthValueConverter();

    private class PercentToWidthValueConverter : IValueConverter
    {
        private const double BaseWidth = 480.0; // approximate available width for treemap

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is double percent)
            {
                // Minimum width of 90px, maximum of full width
                double width = Math.Max(90, BaseWidth * (percent / 100.0));
                return Math.Min(width, BaseWidth);
            }
            return 120.0;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
