using System;
using System.Globalization;
using System.Windows.Data;
using DriverScanTester.Models;

namespace DriverScanTester.Utils
{
    /// <summary>
    /// Converts a 0-based item index (e.g. ListBox AlternationIndex) into a 1-based route number.
    /// </summary>
    public sealed class RouteNumberConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value is int index ? index + 1 : 0;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => System.Windows.Data.Binding.DoNothing;
    }

    /// <summary>
    /// Converts a TravelRouteCompletionMode into its friendly display text.
    /// </summary>
    public sealed class TravelRouteCompletionModeTextConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value is TravelRouteCompletionMode mode
                ? (mode == TravelRouteCompletionMode.ExpectedMapReached ? "Expected Map Reached" : "Final Waypoint")
                : "";

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => System.Windows.Data.Binding.DoNothing;
    }
}
