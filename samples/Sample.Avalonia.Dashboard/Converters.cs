// Sample.Avalonia.Dashboard - small XAML converters
//
// Avalonia 11.x has no built-in BoolToText / BoolToBrush. These two tiny
// converters keep the XAML readable for the IsPaused -> "LIVE" / "PAUSED"
// status badge and the matching colour.

using System;
using System.Globalization;

using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Sample.Avalonia.Dashboard;

/// <summary>
/// Bool -> string mapping used by the status badge's Text binding.
/// </summary>
public sealed class BoolToTextConverter : IValueConverter
{
    public string TrueText { get; set; } = string.Empty;
    public string FalseText { get; set; } = string.Empty;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => (value is bool b && b) ? TrueText : FalseText;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Bool -> SolidColorBrush mapping used by the status badge's Foreground.
/// </summary>
public sealed class BoolToBrushConverter : IValueConverter
{
    /// <summary>
    /// Hex color string (e.g. "#DA373C") used when the bound bool is true.
    /// </summary>
    public string? TrueBrush { get; set; }

    /// <summary>
    /// Hex color string used when the bound bool is false.
    /// </summary>
    public string? FalseBrush { get; set; }

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var hex = (value is bool b && b) ? TrueBrush : FalseBrush;
        if (string.IsNullOrEmpty(hex)) return AvaloniaProperty.UnsetValue;
        return new SolidColorBrush(Color.Parse(hex));
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
