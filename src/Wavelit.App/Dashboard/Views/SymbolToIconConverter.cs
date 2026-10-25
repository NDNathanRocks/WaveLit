using System.Globalization;
using System.Windows.Data;
using Wpf.Ui.Controls;

namespace Wavelit.App.Dashboard.Views;

/// <summary>Turns a bound <see cref="SymbolRegular"/> into the IconElement that WPF-UI buttons expect.</summary>
public sealed class SymbolToIconConverter : IValueConverter
{
    public static readonly SymbolToIconConverter Instance = new();

    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is SymbolRegular symbol ? new SymbolIcon { Symbol = symbol } : null;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
