using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using Anno1800IslandTool.Core.Localization;

namespace Anno1800IslandTool.UI;

internal sealed class DisplayTextConverter : IValueConverter
{
    public static readonly DisplayTextConverter Instance = new();
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is string text ? TextCatalog.DisplayLines(text) : value;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => DependencyProperty.UnsetValue;
}
